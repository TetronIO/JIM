// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the JIM SQL Connector's Delta Import in both of its modes: what each one can and cannot
/// observe, how a customer's own change-type vocabulary is mapped, how the watermark round-trips
/// between runs, and what happens when there is no usable watermark to read. No test here touches a
/// database server; the dialect seam and its connection, command and reader are substituted instead.
/// </summary>
[TestFixture]
public class SqlConnectorDeltaImportTests
{
    #region Documents

    /// <summary>
    /// One object type reading its changes from a customer-maintained change-log table. The shape most
    /// of the change-log tests vary from.
    /// </summary>
    private const string ChangeLogDocument = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "EMPLOYEES",
              "anchorColumns": [ "EMPLOYEE_ID" ],
              "changeLog": {
                "schema": "HR",
                "table": "EMPLOYEE_CHANGES",
                "anchorColumns": [ "EMPLOYEE_ID" ],
                "sequenceColumn": "CHANGE_NUMBER",
                "changeTypeColumn": "CHANGE_TYPE",
                "createValues": [ "I" ],
                "updateValues": [ "U" ],
                "deleteValues": [ "D" ]
              }
            }
          ]
        }
        """;

    /// <summary>
    /// The same object type detecting changes from a last-modified column on its own table.
    /// </summary>
    private const string WatermarkDocument = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "EMPLOYEES",
              "anchorColumns": [ "EMPLOYEE_ID" ],
              "watermarkColumn": "LAST_MODIFIED"
            }
          ]
        }
        """;

    #endregion

    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        (_logger as IDisposable)?.Dispose();
    }

    #region Change-log mode

    [Test]
    public async Task ImportAsync_ChangeLogModeWithCreatesUpdatesAndDeletes_PropagatesEveryChangeTypeIncludingDeletions()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"]],
            changes: [[1, 1, "I"], [2, 2, "U"], [3, 3, "D"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.Multiple(() =>
        {
            Assert.That(ChangeTypeOf(run, 1), Is.EqualTo(ObjectChangeType.Added));
            Assert.That(ChangeTypeOf(run, 2), Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(ChangeTypeOf(run, 3), Is.EqualTo(ObjectChangeType.Deleted),
                "The change-log table is the only mode that observes a deletion, which is the whole reason it is the recommended one.");

            Assert.That(AttributeOf(ObjectWithAnchor(run, 1), "DISPLAY_NAME").StringValues, Is.EqualTo(new[] { "Ada" }),
                "A create carries the row as it now stands, read back from the object type's own source.");

            var deleted = ObjectWithAnchor(run, 3);
            Assert.That(deleted.ObjectType, Is.EqualTo("Person"));
            Assert.That(AttributeOf(deleted, "EMPLOYEE_ID").IntValues, Is.EqualTo(new[] { 3 }),
                "A deletion carries the anchor and nothing else, because the anchor is all JIM needs to find the object it is about.");
        });
    }

    [Test]
    public async Task ImportAsync_ChangeLogModeCustomChangeTypeValues_MapsThemOntoJimsOwnChangeTypes()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "schema": "HR",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "changeLog": {
                    "schema": "HR",
                    "table": "EMPLOYEE_CHANGES",
                    "anchorColumns": [ "EMPLOYEE_ID" ],
                    "sequenceColumn": "CHANGE_NUMBER",
                    "changeTypeColumn": "CHANGE_TYPE",
                    "createValues": [ "NEW", "JOINER" ],
                    "updateValues": [ "MOD" ],
                    "deleteValues": [ "REM" ]
                  }
                }
              ]
            }
            """;

        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"]],
            changes: [[1, 1, "JOINER"], [2, 2, "MOD"], [3, 3, "rem"]]);

        var run = await RunDeltaAsync(provider, document, PersonSystem(), pageSize: 10, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.Multiple(() =>
        {
            Assert.That(ChangeTypeOf(run, 1), Is.EqualTo(ObjectChangeType.Added), "No two estates spell a change type the same way, so the vocabulary is the administrator's to declare.");
            Assert.That(ChangeTypeOf(run, 2), Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(ChangeTypeOf(run, 3), Is.EqualTo(ObjectChangeType.Deleted),
                "Values are matched without regard to case, because a column holding 'D' holds 'd' just as often.");
        });
    }

    [Test]
    public async Task ImportAsync_ChangeLogModeUnmappedChangeTypeValue_ErrorsThatObjectAloneRatherThanTheRun()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"]],
            changes: [[1, 1, "I"], [2, 2, "X"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.Multiple(() =>
        {
            Assert.That(ChangeTypeOf(run, 1), Is.EqualTo(ObjectChangeType.Added), "One unrecognisable row must not cost the whole run.");

            var errored = ObjectWithAnchor(run, 2);
            Assert.That(errored.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(errored.ErrorMessage, Does.Contain("CHANGE_TYPE").And.Contain("X"),
                "The administrator needs to know which value in which column their configuration does not account for.");
        });
    }

    [Test]
    public async Task ImportAsync_ChangeLogModeSameObjectChangedTwiceInAPage_EmitsOnlyItsLatestChange()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"]],
            changes: [[1, 1, "I"], [2, 1, "U"], [3, 1, "D"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.Multiple(() =>
        {
            Assert.That(run.ImportObjects, Has.Count.EqualTo(1), "An object that changed three times is still one object to synchronise.");
            Assert.That(run.ImportObjects[0].ChangeType, Is.EqualTo(ObjectChangeType.Deleted), "The change-log rows are read in sequence order, so the last one is what the object's fate now is.");
        });
    }

    [Test]
    public async Task ImportAsync_ChangeLogMode_ReadsOnlyChangesBeyondThePersistedWatermark()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"], [3, "Katherine"]],
            changes: [[1, 1, "U"], [2, 2, "U"], [3, 3, "U"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "2")));

        Assert.That(AnchorValues(run), Is.EqualTo(new[] { 3 }),
            "A Delta Import reads what changed since it last looked, which is the entire point of persisting a watermark.");
    }

    [Test]
    public async Task ImportAsync_ChangeLogModeCreateWhoseRowHasSinceGone_EmitsNothingForIt()
    {
        // Employee 2 was created and then deleted; only the create has been read so far.
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"]],
            changes: [[1, 1, "U"], [2, 2, "I"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1 }),
            "A row that is no longer in the source cannot be read, and inventing an object for it would be worse than waiting for its deletion to arrive.");
    }

    [Test]
    public async Task ImportAsync_ChangeLogModeMoreChangesThanOnePage_ReplaysTheTokenUntilAShortPageEndsTheRun()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"], [3, "Katherine"], [4, "Dorothy"], [5, "Mary"]],
            changes: [[1, 1, "U"], [2, 2, "U"], [3, 3, "U"], [4, 4, "U"], [5, 5, "U"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 2, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Pages, Has.Count.EqualTo(3), "Five changes at two per page is two full pages and a short one.");
            Assert.That(run.Pages[^1].PaginationTokens, Is.Empty,
                "An empty pagination token list is how JIM is told there is no more data; returning one forever is an infinite import.");
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }),
                "Every change arrives exactly once, in sequence order, which is only true if every page was read against the run's original watermark.");
        });
    }

    [Test]
    public async Task ImportAsync_ChangeLogModeFirstCall_ReportsWhatItIsAboutToRead()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"]],
            changes: [[1, 1, "U"], [2, 2, "U"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 1, Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Progress.ExpectedObjectCounts, Is.EqualTo(new[] { 2 }),
                "A database states the size of a change set cheaply, so the Activity shows a percentage rather than a number counting up.");
            Assert.That(run.Progress.PhaseKeys.First(), Is.EqualTo(SqlConnectorPhases.QueryChanges));
            Assert.That(run.Progress.PhaseKeys, Does.Contain(SqlConnectorPhases.Fetch));
            Assert.That(run.Progress.PhaseKeys.Count(key => key == SqlConnectorPhases.QueryChanges), Is.EqualTo(1),
                "Asking again on every page would make an extra query the price of paging.");
        });
    }

    #endregion

    #region Watermark round trip

    [Test]
    public async Task ImportAsync_DeltaAcrossSeveralPages_ReturnsTheNewWatermarkFromTheFirstPageOnly()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"], [3, "Katherine"]],
            changes: [[1, 1, "U"], [2, 2, "U"], [3, 3, "U"]]);

        var store = Store(PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0"));
        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 1, store);

        Assert.Multiple(() =>
        {
            Assert.That(run.Pages[0].PersistedConnectorData, Is.Not.Null, "The new watermark is captured before any change is read, and returned from the first page.");
            Assert.That(run.Pages.Skip(1).Select(page => page.PersistedConnectorData), Is.All.Null,
                "A later page returning a watermark would overwrite the run's own starting point mid-run.");
            Assert.That(PersonWatermarkValueOf(store.Value), Is.EqualTo("3"),
                "The watermark advances to where the change log stood when the run began; anything logged since is read by the next run.");
        });
    }

    [Test]
    public void ImportAsync_RunThatFailsPartWay_LeavesThePersistedWatermarkUntouched()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"], [3, "Katherine"]],
            changes: [[1, 1, "U"], [2, 2, "U"], [3, 3, "U"]]);

        // Enough commands for the first page to complete and return the new watermark, and not enough
        // for the run to finish.
        provider.FailAfterCommandCount = 5;

        var original = PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "0");
        var store = Store(original);

        Assert.That(async () => await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 1, store),
            Throws.InstanceOf<Exception>());

        Assert.That(store.Value, Is.EqualTo(original),
            "JIM saves the watermark only once every page has been read, so a run that dies half way through re-reads its changes rather than skipping them.");
    }

    [Test]
    public async Task ImportAsync_FullImportWithADeltaImportModeConfigured_EstablishesTheWatermarkForTheNextDelta()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"]],
            changes: [[1, 1, "I"], [2, 2, "I"]]);

        var store = Store(null);
        await RunFullImportAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, store);

        Assert.That(PersonWatermarkValueOf(store.Value), Is.EqualTo("2"),
            "A Full Import is how a Delta Import's baseline is established, so it records where the change log stood when it ran.");
    }

    #endregion

    #region Watermark column mode

    [Test]
    public async Task ImportAsync_WatermarkColumnMode_PropagatesCreatesAndUpdates()
    {
        var provider = WatermarkProvider(
            [1, "Ada", new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Unspecified)],
            [2, "Grace", new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Unspecified)]);

        var watermark = PersonWatermark(SqlDeltaImportMode.WatermarkColumn, "2026-07-15T12:00:00.0000000Z", AttributeDataType.DateTime);
        var run = await RunDeltaAsync(provider, WatermarkDocument, WatermarkSystem(), pageSize: 10, Store(watermark), SqlConnectorConstants.DeltaImportModeWatermarkColumn);

        Assert.Multiple(() =>
        {
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 2 }), "Only the row whose last-modified column moved beyond the watermark has changed.");
            Assert.That(run.ImportObjects[0].ChangeType, Is.EqualTo(ObjectChangeType.Updated),
                "A last-modified column cannot tell a create from an update, so the Connector says what it knows and JIM creates the object where it has none.");
            Assert.That(AttributeOf(run.ImportObjects[0], "DISPLAY_NAME").StringValues, Is.EqualTo(new[] { "Grace" }));
        });
    }

    [Test]
    public async Task ImportAsync_WatermarkColumnModeRowDeletedFromTheSource_DoesNotDetectTheDeletion()
    {
        // Ada was deleted from the source outright. A row that is gone has no last-modified column left
        // to move, so nothing about its absence can reach the watermark's predicate.
        var provider = WatermarkProvider(
            [2, "Grace", new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Unspecified)]);

        var watermark = PersonWatermark(SqlDeltaImportMode.WatermarkColumn, "2026-07-15T12:00:00.0000000Z", AttributeDataType.DateTime);
        var run = await RunDeltaAsync(provider, WatermarkDocument, WatermarkSystem(), pageSize: 10, Store(watermark), SqlConnectorConstants.DeltaImportModeWatermarkColumn);

        Assert.Multiple(() =>
        {
            Assert.That(run.ImportObjects.Any(importObject => importObject.ChangeType == ObjectChangeType.Deleted), Is.False,
                "Watermark Column mode detects creates and updates only; deletions need the change-log table, or a periodic Full Import.");
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 2 }));
        });
    }

    #endregion

    #region Fallback and refusal

    [Test]
    public async Task ImportAsync_DeltaWithNoPersistedWatermark_FallsBackToAFullImportWithTheStandardWarning()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"], [2, "Grace"]],
            changes: [[1, 1, "I"]]);

        var store = Store(null);
        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, store);

        Assert.Multiple(() =>
        {
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1, 2 }), "A Full Import both delivers the right data and establishes the baseline the next Delta Import needs.");
            Assert.That(run.Pages[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(run.Pages[0].WarningMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(run.ImportObjects.Select(importObject => importObject.ChangeType), Is.All.EqualTo(ObjectChangeType.NotSet),
                "The run really is a Full Import, so what is a create and what is an update stays JIM's to work out.");
            Assert.That(PersonWatermarkValueOf(store.Value), Is.EqualTo("1"), "Falling back forever would be the worst of both; the fallback leaves a watermark behind.");
        });
    }

    [Test]
    public async Task ImportAsync_DeltaWithUnreadablePersistedWatermark_FallsBackToAFullImportWithTheStandardWarning()
    {
        var provider = ChangeLogProvider(
            employees: [[1, "Ada"]],
            changes: [[1, 1, "I"]]);

        var run = await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store("{ this is not a watermark"));

        Assert.Multiple(() =>
        {
            Assert.That(run.Pages[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public async Task ImportAsync_DeltaAfterTheDeltaImportModeChanged_FallsBackToAFullImportWithTheStandardWarning()
    {
        var provider = WatermarkProvider([1, "Ada", new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Unspecified)]);

        // A change-log sequence number means nothing to a last-modified column, so a watermark written
        // in the other mode cannot be compared against this one.
        var watermark = PersonWatermark(SqlDeltaImportMode.ChangeLogTable, "42");
        var run = await RunDeltaAsync(provider, WatermarkDocument, WatermarkSystem(), pageSize: 10, Store(watermark), SqlConnectorConstants.DeltaImportModeWatermarkColumn);

        Assert.Multiple(() =>
        {
            Assert.That(run.Pages[0].WarningErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport));
            Assert.That(run.Pages[0].WarningMessage, Does.Contain(SqlConnectorConstants.SettingDeltaImportMode));
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public void ImportAsync_DeltaWithNoDeltaImportModeConfigured_RefusesToRun()
    {
        var provider = ChangeLogProvider(employees: [[1, "Ada"]], changes: [[1, 1, "I"]]);

        Assert.That(async () => await RunDeltaAsync(provider, ChangeLogDocument, PersonSystem(), pageSize: 10, Store(null), deltaMode: null),
            Throws.TypeOf<CannotPerformDeltaImportException>(),
            "An unanswered configuration question is not something to work around: silently running a Full Import on every scheduled Delta Import would hide it for ever.");
    }

    #endregion

    #region Save-time configuration validation

    [Test]
    public void ValidateSettingValues_TheDocumentedDeltaExample_IsAccepted()
    {
        var connector = new SqlConnector { ProviderFactory = _ => new FakeSqlProvider() };
        var settingValues = DeltaSettingValues(connector, SqlConnectorConstants.DeltaConfigurationExample, SqlConnectorConstants.DeltaImportModeChangeLogTable);

        Assert.That(connector.ValidateSettingValues(settingValues, _logger), Is.Empty,
            "The example in the setting's own description is the first thing an administrator will paste in, so it has to be one the Connector accepts.");
    }

    [Test]
    public void ValidateSettingValues_TheDocumentedDeltaExampleInWatermarkColumnMode_IsAccepted()
    {
        var connector = new SqlConnector { ProviderFactory = _ => new FakeSqlProvider() };
        var settingValues = DeltaSettingValues(connector, SqlConnectorConstants.DeltaConfigurationExample, SqlConnectorConstants.DeltaImportModeWatermarkColumn);

        Assert.That(connector.ValidateSettingValues(settingValues, _logger), Is.Empty,
            "The example shows both modes, so it has to be complete under either one, related tables included.");
    }

    [Test]
    public void ValidateSettingValues_WatermarkColumnModeWithARelatedTableThatHasNoWatermarkColumn_IsRefused()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "watermarkColumn": "LAST_MODIFIED",
                  "relatedTables": [
                    { "attributeName": "PhoneNumbers", "table": "EMPLOYEE_PHONES", "valueColumn": "PHONE_NUMBER", "joinColumns": [ "EMPLOYEE_ID" ] }
                  ]
                }
              ]
            }
            """;

        Assert.That(ValidationMessageFor(document, SqlConnectorConstants.DeltaImportModeWatermarkColumn),
            Does.Contain("Person").And.Contain("PhoneNumbers").And.Contain("watermarkColumn"),
            "A related table with no watermark column can never report a change of its own, and a membership that changes without JIM noticing is exactly the silent drift this mode must not have.");
    }

    [Test]
    public void ValidateSettingValues_ChangeLogModeWithARelatedTableThatHasNoWatermarkColumn_IsAccepted()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "relatedTables": [
                    { "attributeName": "PhoneNumbers", "table": "EMPLOYEE_PHONES", "valueColumn": "PHONE_NUMBER", "joinColumns": [ "EMPLOYEE_ID" ] }
                  ],
                  "changeLog": {
                    "table": "EMPLOYEE_CHANGES",
                    "anchorColumns": [ "EMPLOYEE_ID" ],
                    "sequenceColumn": "CHANGE_NUMBER",
                    "changeTypeColumn": "CHANGE_TYPE",
                    "createValues": [ "I" ],
                    "updateValues": [ "U" ],
                    "deleteValues": [ "D" ]
                  }
                }
              ]
            }
            """;

        var connector = new SqlConnector { ProviderFactory = _ => new FakeSqlProvider() };

        Assert.That(connector.ValidateSettingValues(DeltaSettingValues(connector, document, SqlConnectorConstants.DeltaImportModeChangeLogTable), _logger), Is.Empty,
            "A change log records what happened to the object however it happened, so a related table needs no watermark of its own in that mode.");
    }

    [Test]
    public void ValidateSettingValues_ChangeLogWithNoDeleteValues_IsRefused()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "changeLog": {
                    "table": "EMPLOYEE_CHANGES",
                    "anchorColumns": [ "EMPLOYEE_ID" ],
                    "sequenceColumn": "CHANGE_NUMBER",
                    "changeTypeColumn": "CHANGE_TYPE",
                    "updateValues": [ "U" ]
                  }
                }
              ]
            }
            """;

        Assert.That(ValidationMessageFor(document, SqlConnectorConstants.DeltaImportModeChangeLogTable), Does.Contain("deleteValues"),
            "A change log that cannot say an object was deleted is the one thing this mode exists to provide.");
    }

    [Test]
    public void ValidateSettingValues_ChangeLogAnchorColumnCountNotMatchingTheObjectTypesAnchor_IsRefused()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "COMPANY_ID", "EMPLOYEE_ID" ],
                  "changeLog": {
                    "table": "EMPLOYEE_CHANGES",
                    "anchorColumns": [ "EMPLOYEE_ID" ],
                    "sequenceColumn": "CHANGE_NUMBER",
                    "changeTypeColumn": "CHANGE_TYPE",
                    "deleteValues": [ "D" ]
                  }
                }
              ]
            }
            """;

        Assert.That(ValidationMessageFor(document, SqlConnectorConstants.DeltaImportModeChangeLogTable), Does.Contain("anchor"),
            "A change-log row identified by part of an anchor names some other object, without any error.");
    }

    [Test]
    public void ValidateSettingValues_AChangeTypeValueMeaningTwoDifferentThings_IsRefused()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "changeLog": {
                    "table": "EMPLOYEE_CHANGES",
                    "anchorColumns": [ "EMPLOYEE_ID" ],
                    "sequenceColumn": "CHANGE_NUMBER",
                    "changeTypeColumn": "CHANGE_TYPE",
                    "updateValues": [ "D" ],
                    "deleteValues": [ "D" ]
                  }
                }
              ]
            }
            """;

        Assert.That(ValidationMessageFor(document, SqlConnectorConstants.DeltaImportModeChangeLogTable), Does.Contain("'D'"),
            "One value cannot mean both an update and a deletion, and guessing which the administrator meant is not JIM's to do.");
    }

    [Test]
    public void ValidateSettingValues_ChangeLogModeWithAnObjectTypeThatHasNoChangeLog_IsRefused()
    {
        Assert.That(ValidationMessageFor(WatermarkDocument, SqlConnectorConstants.DeltaImportModeChangeLogTable),
            Does.Contain("Person").And.Contain("changeLog"),
            "A Delta Import that silently skips an object type is a data-integrity trap, so the configuration is refused before it can be saved.");
    }

    [Test]
    public void ValidateSettingValues_WatermarkColumnModeWithAnObjectTypeThatHasNoWatermarkColumn_IsRefused()
    {
        Assert.That(ValidationMessageFor(ChangeLogDocument, SqlConnectorConstants.DeltaImportModeWatermarkColumn),
            Does.Contain("Person").And.Contain("watermarkColumn"));
    }

    [Test]
    public void ValidateSettingValues_ADeltaConfigurationWithNoModeChosen_IsAccepted()
    {
        var connector = new SqlConnector { ProviderFactory = _ => new FakeSqlProvider() };
        var settingValues = DeltaSettingValues(connector, ChangeLogDocument, deltaMode: null);

        Assert.That(connector.ValidateSettingValues(settingValues, _logger), Is.Empty,
            "A Connected System that only runs Full Imports is not obliged to answer the Delta Import question.");
    }

    #endregion

    #region Test helpers

    /// <summary>
    /// Stands in for the Connected System's persisted connector data, which the Worker replays to every
    /// page of a run and only writes back once every page has been read.
    /// </summary>
    private sealed class PersistedConnectorDataStore
    {
        internal string? Value { get; set; }
    }

    private static PersistedConnectorDataStore Store(string? value) => new() { Value = value };

    /// <summary>
    /// Everything one import run produced: each call's result in order, and what it narrated.
    /// </summary>
    private sealed record SqlDeltaRun(List<ConnectedSystemImportResult> Pages, RecordingConnectorProgress Progress)
    {
        internal List<ConnectedSystemImportObject> ImportObjects => [.. Pages.SelectMany(page => page.ImportObjects)];
    }

    private Task<SqlDeltaRun> RunDeltaAsync(
        FakeSqlProvider provider,
        string objectTypesDocument,
        ConnectedSystem connectedSystem,
        int pageSize,
        PersistedConnectorDataStore store,
        string? deltaMode = SqlConnectorConstants.DeltaImportModeChangeLogTable,
        int callLimit = 25) =>
        RunAsync(provider, objectTypesDocument, connectedSystem, ConnectedSystemRunType.DeltaImport, pageSize, store, deltaMode, callLimit);

    private Task<SqlDeltaRun> RunFullImportAsync(
        FakeSqlProvider provider,
        string objectTypesDocument,
        ConnectedSystem connectedSystem,
        int pageSize,
        PersistedConnectorDataStore store,
        string? deltaMode = SqlConnectorConstants.DeltaImportModeChangeLogTable,
        int callLimit = 25) =>
        RunAsync(provider, objectTypesDocument, connectedSystem, ConnectedSystemRunType.FullImport, pageSize, store, deltaMode, callLimit);

    /// <summary>
    /// Drives an import to completion exactly the way the Worker does: the persisted connector data the
    /// run started with is replayed to every page, the new value is taken from the first page that
    /// offers one, and it is written back only once every page has been read.
    /// </summary>
    private async Task<SqlDeltaRun> RunAsync(
        FakeSqlProvider provider,
        string objectTypesDocument,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunType runType,
        int pageSize,
        PersistedConnectorDataStore store,
        string? deltaMode,
        int callLimit)
    {
        var progress = new RecordingConnectorProgress();
        var pages = new List<ConnectedSystemImportResult>();

        // Disposal releases the import connection, so the Connector needs no explicit close here.
        using var connector = new SqlConnector { ProviderFactory = _ => provider };
        var settingValues = DeltaSettingValues(connector, objectTypesDocument, deltaMode);

        var originalPersistedData = store.Value;
        connector.OpenImportConnection(settingValues, originalPersistedData, _logger);

        var runProfile = new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = pageSize };
        var paginationTokens = new List<ConnectedSystemPaginationToken>();
        var initialPage = true;
        string? newPersistedData = null;

        while (initialPage || paginationTokens.Count > 0)
        {
            initialPage = false;

            var result = await connector.ImportAsync(connectedSystem, runProfile, paginationTokens, originalPersistedData, _logger, CancellationToken.None, progress);
            pages.Add(result);
            paginationTokens = result.PaginationTokens;
            newPersistedData ??= result.PersistedConnectorData;

            Assert.That(pages, Has.Count.LessThanOrEqualTo(callLimit),
                "The import never stopped returning pagination tokens, which is an infinite import.");
        }

        if (newPersistedData != null)
            store.Value = newPersistedData;

        return new SqlDeltaRun(pages, progress);
    }

    private static List<ConnectedSystemSettingValue> DeltaSettingValues(SqlConnector connector, string objectTypesDocument, string? deltaMode)
    {
        var settingValues = SqlConnectorSettingValues.CreateSqlServer(connector);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, objectTypesDocument);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingDeltaImportMode, deltaMode);
        return settingValues;
    }

    private string ValidationMessageFor(string objectTypesDocument, string deltaMode)
    {
        var connector = new SqlConnector { ProviderFactory = _ => new FakeSqlProvider() };
        var results = connector.ValidateSettingValues(DeltaSettingValues(connector, objectTypesDocument, deltaMode), _logger);

        Assert.That(results, Has.Count.EqualTo(1), "Configuration that cannot work is refused before it can be saved.");
        Assert.That(results[0].IsValid, Is.False);

        return results[0].ErrorMessage ?? string.Empty;
    }

    /// <summary>
    /// A stand-in database holding an object type's rows and its change log.
    /// </summary>
    private static FakeSqlProvider ChangeLogProvider(object?[][] employees, object?[][] changes)
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"], employees);
        provider.Catalogue.AddRows("HR", "EMPLOYEE_CHANGES", ["CHANGE_NUMBER", "EMPLOYEE_ID", "CHANGE_TYPE"], changes);
        return provider;
    }

    /// <summary>
    /// A stand-in database holding an object type whose rows carry their own last-modified column.
    /// </summary>
    private static FakeSqlProvider WatermarkProvider(params object?[][] employees)
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME", "LAST_MODIFIED"], employees);
        return provider;
    }

    private static string PersonWatermark(SqlDeltaImportMode mode, string value, AttributeDataType type = AttributeDataType.Number)
    {
        var watermark = new SqlConnectorWatermark { Mode = mode };
        watermark.ObjectTypes["Person"] = new SqlDeltaValue(value, type);
        return watermark.Serialise();
    }

    private static string? PersonWatermarkValueOf(string? persistedConnectorData)
    {
        var watermark = SqlConnectorWatermark.TryRead(persistedConnectorData);
        return watermark != null && watermark.ObjectTypes.TryGetValue("Person", out var value) ? value.Value : null;
    }

    private static ConnectedSystem PersonSystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType("Person",
                Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                Attribute("DISPLAY_NAME", AttributeDataType.Text))
        ]
    };

    private static ConnectedSystem WatermarkSystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType("Person",
                Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                Attribute("DISPLAY_NAME", AttributeDataType.Text),
                Attribute("LAST_MODIFIED", AttributeDataType.DateTime, selected: false))
        ]
    };

    private static ConnectedSystemObjectType ObjectType(string name, params ConnectedSystemObjectTypeAttribute[] attributes) =>
        new() { Name = name, Selected = true, Attributes = [.. attributes] };

    private static ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, bool isExternalId = false, bool selected = true) =>
        new() { Name = name, Type = type, Selected = selected, IsExternalId = isExternalId, AttributePlurality = AttributePlurality.SingleValued };

    private static ConnectedSystemImportObjectAttribute AttributeOf(ConnectedSystemImportObject importObject, string name) =>
        importObject.Attributes.Single(attribute => attribute.Name == name);

    private static ConnectedSystemImportObject ObjectWithAnchor(SqlDeltaRun run, int anchor) =>
        run.ImportObjects.Single(importObject => AttributeOf(importObject, "EMPLOYEE_ID").IntValues.Single() == anchor);

    private static ObjectChangeType ChangeTypeOf(SqlDeltaRun run, int anchor) => ObjectWithAnchor(run, anchor).ChangeType;

    private static List<int> AnchorValues(SqlDeltaRun run) =>
        [.. run.ImportObjects.Select(importObject => AttributeOf(importObject, "EMPLOYEE_ID").IntValues.Single()).Order()];

    #endregion
}
