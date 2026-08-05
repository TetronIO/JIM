// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Utilities;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the JIM SQL Connector's Full Import: how it pages, how it knows when to stop, how it shapes a
/// row into a Connected System Import Object, and what it narrates while doing it. No test here touches
/// a database server; the dialect seam and its connection, command and reader are substituted instead.
/// </summary>
[TestFixture]
public class SqlConnectorImportTests
{
    /// <summary>
    /// One object type, one table, one anchor column: the shape most of these tests vary from.
    /// </summary>
    private const string PersonDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] }
          ]
        }
        """;

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

    #region Paging and termination

    [Test]
    public async Task ImportAsync_MoreRowsThanOnePage_ReplaysTheTokenUntilAShortPageEndsTheRun()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"],
            [1, "Ada"], [2, "Grace"], [3, "Katherine"], [4, "Dorothy"], [5, "Mary"]);

        var run = await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(run.Pages, Has.Count.EqualTo(3), "Five rows at two per page is two full pages and a short one.");
            Assert.That(run.Pages[0].ImportObjects, Has.Count.EqualTo(2));
            Assert.That(run.Pages[1].ImportObjects, Has.Count.EqualTo(2));
            Assert.That(run.Pages[2].ImportObjects, Has.Count.EqualTo(1));

            Assert.That(run.Pages[0].PaginationTokens, Is.Not.Empty, "A full page means there may be more, so the anchor has to be carried forward.");
            Assert.That(run.Pages[2].PaginationTokens, Is.Empty,
                "An empty pagination token list is how JIM is told there is no more data; returning one forever is an infinite import.");

            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }), "Every row arrives exactly once, in anchor order.");
        });
    }

    [Test]
    public async Task ImportAsync_RowCountAnExactMultipleOfThePageSize_TerminatesOnAnEmptyFinalPage()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"],
            [1, "Ada"], [2, "Grace"], [3, "Katherine"], [4, "Dorothy"]);

        var run = await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(run.Pages, Has.Count.EqualTo(3), "A full last page cannot be told from a page with more behind it, so one empty read is unavoidable.");
            Assert.That(run.Pages[2].ImportObjects, Is.Empty);
            Assert.That(run.Pages[2].PaginationTokens, Is.Empty);
            Assert.That(AnchorValues(run), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        });
    }

    [Test]
    public async Task ImportAsync_AnyPage_NeverAsksTheDatabaseForAnOffset()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"],
            [1, "Ada"], [2, "Grace"], [3, "Katherine"]);

        await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 1);

        Assert.That(provider.ExecutedCommandTexts.Where(text => text.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)), Is.Empty,
            "OFFSET re-scans every skipped row, so a 500,000-row import degrades quadratically; the Connector must page on the anchor.");
    }

    [Test]
    public async Task ImportAsync_CompositeAnchor_PagesAcrossEveryAnchorColumnAndComposesTheExternalId()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "COMPANY_ID", "EMPLOYEE_ID" ] }
              ]
            }
            """;

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["COMPANY_ID", "EMPLOYEE_ID", "DISPLAY_NAME"],
            [1, 1, "Ada"], [1, 2, "Grace"], [2, 1, "Katherine"], [2, 2, "Dorothy"]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("COMPANY_ID", AttributeDataType.Number),
                    Attribute("EMPLOYEE_ID", AttributeDataType.Number),
                    Attribute("DISPLAY_NAME", AttributeDataType.Text),
                    Attribute("COMPANY_ID+EMPLOYEE_ID", AttributeDataType.Text, isExternalId: true))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem, pageSize: 3);

        var composed = run.ImportObjects
            .Select(importObject => importObject.Attributes.Single(a => a.Name == "COMPANY_ID+EMPLOYEE_ID").StringValues.Single())
            .ToList();

        Assert.That(composed, Is.EqualTo(new[] { "1+1", "1+2", "2+1", "2+2" }),
            "A Connected System Object is identified by one value, so a composite anchor is projected as the composed attribute schema discovery declared.");
    }

    #endregion

    #region Object shaping

    [Test]
    public async Task ImportAsync_AnyObject_LeavesTheChangeTypeUnsetForJimToDecide()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"], [1, "Ada"]);

        var run = await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(run.ImportObjects.Single().ObjectType, Is.EqualTo("Person"));
            Assert.That(run.ImportObjects.Single().ChangeType, Is.EqualTo(ObjectChangeType.NotSet),
                "A Full Import states what is there; whether that is a create or an update is JIM's to work out.");
        });
    }

    [Test]
    public async Task ImportAsync_EveryMappedType_ArrivesOnItsOwnTypedValueList()
    {
        var guid = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3 };

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES",
            ["EMPLOYEE_ID", "DISPLAY_NAME", "HEADCOUNT", "PAYROLL_NUMBER", "SALARY", "IS_ACTIVE", "OBJECT_GUID", "PHOTO"],
            [1, "Ada", 42, 9_000_000_000L, 1234.50m, true, guid, bytes]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                    Attribute("DISPLAY_NAME", AttributeDataType.Text),
                    Attribute("HEADCOUNT", AttributeDataType.Number),
                    Attribute("PAYROLL_NUMBER", AttributeDataType.LongNumber),
                    Attribute("SALARY", AttributeDataType.Decimal),
                    Attribute("IS_ACTIVE", AttributeDataType.Boolean),
                    Attribute("OBJECT_GUID", AttributeDataType.Guid),
                    Attribute("PHOTO", AttributeDataType.Binary))
            ]
        };

        var run = await RunImportAsync(provider, PersonDocument, connectedSystem, pageSize: 10);
        var imported = run.ImportObjects.Single();

        Assert.Multiple(() =>
        {
            Assert.That(Attribute(imported, "EMPLOYEE_ID").IntValues, Is.EqualTo(new[] { 1 }));
            Assert.That(Attribute(imported, "DISPLAY_NAME").StringValues, Is.EqualTo(new[] { "Ada" }));
            Assert.That(Attribute(imported, "HEADCOUNT").IntValues, Is.EqualTo(new[] { 42 }));
            Assert.That(Attribute(imported, "PAYROLL_NUMBER").LongValues, Is.EqualTo(new[] { 9_000_000_000L }));
            Assert.That(Attribute(imported, "SALARY").DecimalValues, Is.EqualTo(new[] { 1234.50m }));
            Assert.That(Attribute(imported, "IS_ACTIVE").BoolValue, Is.True);
            Assert.That(Attribute(imported, "OBJECT_GUID").GuidValues, Is.EqualTo(new[] { guid }));
            Assert.That(Attribute(imported, "PHOTO").ByteValues.Single(), Is.EqualTo(bytes));
        });
    }

    [Test]
    public async Task ImportAsync_NullColumnValue_ProducesNoAttributeRatherThanAnEmptyOne()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"], [1, null]);

        var run = await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 10);

        Assert.That(run.ImportObjects.Single().Attributes.Any(a => a.Name == "DISPLAY_NAME"), Is.False,
            "A NULL column has no value, and an empty attribute would read as one that was cleared.");
    }

    [Test]
    public async Task ImportAsync_DecimalAnchor_RendersTheAnchorInItsCanonicalDecimalForm()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "COMPANY_ID", "EMPLOYEE_ID" ] }
              ]
            }
            """;

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["COMPANY_ID", "EMPLOYEE_ID"], [1, 5.00m], [1, 5.50m]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("COMPANY_ID", AttributeDataType.Number),
                    Attribute("EMPLOYEE_ID", AttributeDataType.Decimal),
                    Attribute("COMPANY_ID+EMPLOYEE_ID", AttributeDataType.Text, isExternalId: true))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem, pageSize: 10);

        var composed = run.ImportObjects
            .Select(importObject => Attribute(importObject, "COMPANY_ID+EMPLOYEE_ID").StringValues.Single())
            .ToList();

        Assert.That(composed, Is.EqualTo(new[] { "1+5", "1+5.5" }),
            $"A Decimal anchor must render through {nameof(DecimalAttributeValue)}, so that 5.00 and 5.0 identify the same object.");
    }

    #endregion

    #region Date and time fidelity

    [Test]
    public async Task ImportAsync_ZonelessDateTime_IsInterpretedInTheConfiguredDatabaseTimeZone()
    {
        // A summer date, so a zone that observes daylight saving is an hour away from UTC and the
        // interpretation is visible rather than a coincidence.
        var zoneless = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Unspecified);

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "STARTED"], [1, zoneless]);

        var run = await RunImportAsync(provider, PersonDocument, DateSystem(), pageSize: 10, databaseTimeZone: "Europe/London");
        var imported = Attribute(run.ImportObjects.Single(), "STARTED");

        Assert.Multiple(() =>
        {
            Assert.That(imported.DateTimeValue, Is.EqualTo(new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc)));
            Assert.That(imported.DateTimeValue!.Value.Kind, Is.EqualTo(DateTimeKind.Utc), "JIM stores every date and time in UTC, so the kind is never left unspecified.");
        });
    }

    [Test]
    public async Task ImportAsync_ZonelessDateTimeWithUtcConfigured_IsTakenAsUtcUnchanged()
    {
        var zoneless = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Unspecified);

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "STARTED"], [1, zoneless]);

        var run = await RunImportAsync(provider, PersonDocument, DateSystem(), pageSize: 10);

        Assert.That(Attribute(run.ImportObjects.Single(), "STARTED").DateTimeValue,
            Is.EqualTo(new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task ImportAsync_OffsetCarryingDateTime_NormalisesToUtcRegardlessOfTheConfiguredTimeZone()
    {
        var withOffset = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.FromHours(2));

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "STARTED"], [1, withOffset]);

        var run = await RunImportAsync(provider, PersonDocument, DateSystem(), pageSize: 10, databaseTimeZone: "Europe/London");
        var imported = Attribute(run.ImportObjects.Single(), "STARTED");

        Assert.Multiple(() =>
        {
            Assert.That(imported.DateTimeValue, Is.EqualTo(new DateTime(2026, 7, 15, 7, 0, 0, DateTimeKind.Utc)),
                "A value that states its own offset needs no setting to interpret it.");
            Assert.That(imported.DateTimeValue!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        });
    }

    #endregion

    #region References

    [Test]
    public async Task ImportAsync_ReferenceColumn_CarriesTheReferencedRowsAnchorAsAString()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "schema": "HR",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "columns": [ { "name": "MANAGER_ID", "referencesObjectType": "Person" } ]
                }
              ]
            }
            """;

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "MANAGER_ID"], [1, null], [2, 1]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                    Attribute("MANAGER_ID", AttributeDataType.Reference))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem, pageSize: 10);
        var managed = run.ImportObjects.Single(importObject => Attribute(importObject, "EMPLOYEE_ID").IntValues.Single() == 2);

        Assert.That(Attribute(managed, "MANAGER_ID").ReferenceValues, Is.EqualTo(new[] { "1" }),
            "JIM resolves a reference from the referenced object's anchor, so the Connector hands over the anchor as text.");
    }

    #endregion

    #region Multi-valued attributes from related tables

    [Test]
    public async Task ImportAsync_RelatedTable_IsQueriedOncePerPageRatherThanOncePerRow()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1], [2], [3], [4]);
        provider.Catalogue.AddRows("HR", "EMPLOYEE_PHONES", ["EMPLOYEE_ID", "PHONE_NUMBER"],
            [1, "0100"], [1, "0101"], [3, "0300"]);

        var run = await RunImportAsync(provider, PhonesDocument, PhonesSystem(), pageSize: 4);

        var relatedQueries = provider.ExecutedCommandTexts.Count(text => text.Contains("EMPLOYEE_PHONES", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(relatedQueries, Is.EqualTo(1),
                "At 500,000 rows a query per row is the difference between a working Connector and an unusable one.");

            Assert.That(Attribute(ObjectWithAnchor(run, 1), "PhoneNumbers").StringValues, Is.EquivalentTo(new[] { "0100", "0101" }));
            Assert.That(ObjectWithAnchor(run, 2).Attributes.Any(a => a.Name == "PhoneNumbers"), Is.False, "An object with no related rows has no attribute at all.");
            Assert.That(Attribute(ObjectWithAnchor(run, 3), "PhoneNumbers").StringValues, Is.EqualTo(new[] { "0300" }));
        });
    }

    [Test]
    public async Task ImportAsync_RelatedTableAcrossPages_IsQueriedOncePerPage()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1], [2], [3], [4]);
        provider.Catalogue.AddRows("HR", "EMPLOYEE_PHONES", ["EMPLOYEE_ID", "PHONE_NUMBER"],
            [1, "0100"], [4, "0400"]);

        var run = await RunImportAsync(provider, PhonesDocument, PhonesSystem(), pageSize: 2);

        var relatedQueries = provider.ExecutedCommandTexts.Count(text => text.Contains("EMPLOYEE_PHONES", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(relatedQueries, Is.EqualTo(2), "Two pages carried rows, and the empty third page has no anchors to gather against.");
            Assert.That(Attribute(ObjectWithAnchor(run, 1), "PhoneNumbers").StringValues, Is.EqualTo(new[] { "0100" }));
            Assert.That(Attribute(ObjectWithAnchor(run, 4), "PhoneNumbers").StringValues, Is.EqualTo(new[] { "0400" }));
        });
    }

    [Test]
    public async Task ImportAsync_RelatedTableThatReferencesAnObjectType_CarriesItsValuesAsReferenceAnchors()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] },
                {
                  "name": "Group",
                  "schema": "HR",
                  "table": "GROUPS",
                  "anchorColumns": [ "GROUP_ID" ],
                  "relatedTables": [
                    {
                      "attributeName": "Members",
                      "schema": "HR",
                      "table": "GROUP_MEMBERS",
                      "valueColumn": "EMPLOYEE_ID",
                      "joinColumns": [ "GROUP_ID" ],
                      "referencesObjectType": "Person"
                    }
                  ]
                }
              ]
            }
            """;

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1], [2]);
        provider.Catalogue.AddRows("HR", "GROUPS", ["GROUP_ID"], [10]);
        provider.Catalogue.AddRows("HR", "GROUP_MEMBERS", ["GROUP_ID", "EMPLOYEE_ID"], [10, 1], [10, 2]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person", Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true)),
                ObjectType("Group",
                    Attribute("GROUP_ID", AttributeDataType.Number, isExternalId: true),
                    Attribute("Members", AttributeDataType.Reference, plurality: AttributePlurality.MultiValued))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem, pageSize: 10);
        var group = run.ImportObjects.Single(importObject => importObject.ObjectType == "Group");

        Assert.That(Attribute(group, "Members").ReferenceValues, Is.EquivalentTo(new[] { "1", "2" }),
            "Group membership is a multi-valued reference, and JIM resolves it from the referenced objects' anchors.");
    }

    #endregion

    #region Error handling

    [Test]
    public async Task ImportAsync_ValueThatCannotBeConverted_ErrorsThatObjectAloneRatherThanTheRun()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "HEADCOUNT"],
            [1, 42], [2, "not a number"], [3, 7]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person",
                    Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                    Attribute("HEADCOUNT", AttributeDataType.Number))
            ]
        };

        var run = await RunImportAsync(provider, PersonDocument, connectedSystem, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(run.ImportObjects, Has.Count.EqualTo(3), "One bad value must not cost the whole page.");

            var errored = ObjectWithAnchor(run, 2);
            Assert.That(errored.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(errored.ErrorMessage, Does.Contain("HEADCOUNT"), "The administrator needs to know which column to look at.");
            Assert.That(Attribute(errored, "EMPLOYEE_ID").IntValues, Is.EqualTo(new[] { 2 }),
                "An errored object still carries its anchor, or the error cannot be attributed to an object.");

            Assert.That(ObjectWithAnchor(run, 1).ErrorType, Is.Null);
            Assert.That(ObjectWithAnchor(run, 3).ErrorType, Is.Null);
        });
    }

    [Test]
    public void ImportAsync_AnchorColumnMissingFromTheSchema_FailsTheRun()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes = [ObjectType("Person", Attribute("DISPLAY_NAME", AttributeDataType.Text))]
        };

        Assert.That(async () => await RunImportAsync(provider, PersonDocument, connectedSystem, pageSize: 10),
            Throws.TypeOf<SqlSchemaConfigurationException>(),
            "A configuration that cannot identify an object is not something to work around per object.");
    }

    [Test]
    public void ImportAsync_TableThatHasVanished_FailsTheRun()
    {
        // Nothing registered in the stand-in database, which is what a dropped table looks like here.
        var provider = new FakeSqlProvider();

        Assert.That(async () => await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 10),
            Throws.InstanceOf<System.Data.Common.DbException>(),
            "A source that is no longer there is a run failure, not a per-object one.");
    }

    #endregion

    #region Progress

    [Test]
    public async Task ImportAsync_FirstCall_CountsTheRowsSoTheActivityCanShowAPercentage()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID", "DISPLAY_NAME"],
            [1, "Ada"], [2, "Grace"], [3, "Katherine"], [4, "Dorothy"], [5, "Mary"]);

        var run = await RunImportAsync(provider, PersonDocument, PersonSystem(), pageSize: 2);

        Assert.Multiple(() =>
        {
            Assert.That(run.Progress.ExpectedObjectCounts, Is.EqualTo(new[] { 5 }),
                "A database states a result-set size cheaply, so the expected count is reported once, on the first call.");
            Assert.That(run.Progress.PhaseKeys.First(), Is.EqualTo(SqlConnectorPhases.Count));
            Assert.That(run.Progress.PhaseKeys, Does.Contain(SqlConnectorPhases.Fetch));
            Assert.That(run.Progress.PhaseKeys.Count(key => key == SqlConnectorPhases.Count), Is.EqualTo(1),
                "Counting again on every page would make an expensive query the price of paging.");
        });
    }

    [Test]
    public async Task ImportAsync_OneCallDrainingSeveralObjectTypes_ReportsObjectsReadAsItGoes()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] },
                { "name": "Group", "schema": "HR", "table": "GROUPS", "anchorColumns": [ "GROUP_ID" ] }
              ]
            }
            """;

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1], [2]);
        provider.Catalogue.AddRows("HR", "GROUPS", ["GROUP_ID"], [10]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person", Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true)),
                ObjectType("Group", Attribute("GROUP_ID", AttributeDataType.Number, isExternalId: true))
            ]
        };

        var run = await RunImportAsync(provider, document, connectedSystem, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(run.Progress.ExpectedObjectCounts, Is.EqualTo(new[] { 3 }), "Both object types are counted, because both are part of the run.");
            Assert.That(run.Pages[0].ImportObjects, Has.Count.EqualTo(3));
            Assert.That(run.Progress.ObjectsRead, Is.EqualTo(new[] { 2, 3 }),
                "One call drained a page of each object type, so the counters move while the call is still in flight.");
        });
    }

    #endregion

    #region Cancellation and connection lifecycle

    [Test]
    public async Task ImportAsync_CancellationRequestedBetweenPages_StopsWithoutReadingTheNextObjectType()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] },
                { "name": "Group", "schema": "HR", "table": "GROUPS", "anchorColumns": [ "GROUP_ID" ] }
              ]
            }
            """;

        var provider = new FakeSqlProvider();
        provider.Catalogue.AddRows("HR", "EMPLOYEES", ["EMPLOYEE_ID"], [1], [2]);
        provider.Catalogue.AddRows("HR", "GROUPS", ["GROUP_ID"], [10]);

        var connectedSystem = new ConnectedSystem
        {
            Name = "HR Database",
            ObjectTypes =
            [
                ObjectType("Person", Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true)),
                ObjectType("Group", Attribute("GROUP_ID", AttributeDataType.Number, isExternalId: true))
            ]
        };

        using var cancellation = new CancellationTokenSource();

        // Cancelled once the first object type's page has been drained and counted, which is exactly the
        // page boundary the next object type is reached from.
        var progress = new RecordingConnectorProgress(onObjectsRead: _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        var connector = new SqlConnector { ProviderFactory = _ => provider };
        try
        {
            connector.OpenImportConnection(SettingValues(connector, document), null, _logger);

            var result = await connector.ImportAsync(connectedSystem, RunProfile(10), [], null, _logger, cancellation.Token, progress);

            Assert.Multiple(() =>
            {
                Assert.That(result.ImportObjects.Select(importObject => importObject.ObjectType), Is.All.EqualTo("Person"),
                    "A cancelled run stops at the next page boundary rather than reading everything it was asked for.");
                Assert.That(provider.ExecutedCommandTexts.Any(text => text.Contains("[GROUPS]", StringComparison.Ordinal) && !text.StartsWith("SELECT COUNT", StringComparison.Ordinal)), Is.False);
            });
        }
        finally
        {
            connector.CloseImportConnection();
            connector.Dispose();
        }
    }

    [Test]
    public void CloseImportConnection_AfterAFailedImport_ReleasesTheConnectionAndLeavesPersistedStateAlone()
    {
        var provider = new FakeSqlProvider();
        var connector = new SqlConnector { ProviderFactory = _ => provider };

        try
        {
            connector.OpenImportConnection(SettingValues(connector, PersonDocument), null, _logger);

            // Nothing registered in the stand-in database, so the import fails the way a dropped table does.
            Assert.That(async () => await connector.ImportAsync(PersonSystem(), RunProfile(10), [], null, _logger, CancellationToken.None, new RecordingConnectorProgress()),
                Throws.InstanceOf<Exception>());

            Assert.Multiple(() =>
            {
                Assert.That(connector.CloseImportConnection(), Is.Null,
                    "Nothing needs persisting for a Full Import, and a non-null return would override state JIM already holds.");
                Assert.That(provider.OpenConnections.All(connection => connection.State == System.Data.ConnectionState.Closed), Is.True,
                    "A failed import must still release its connection, or a run leaves a session open on the customer's database.");
            });
        }
        finally
        {
            connector.Dispose();
        }
    }

    #endregion

    #region Test helpers

    private const string PhonesDocument = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "EMPLOYEES",
              "anchorColumns": [ "EMPLOYEE_ID" ],
              "relatedTables": [
                {
                  "attributeName": "PhoneNumbers",
                  "schema": "HR",
                  "table": "EMPLOYEE_PHONES",
                  "valueColumn": "PHONE_NUMBER",
                  "joinColumns": [ "EMPLOYEE_ID" ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Everything one Full Import produced: each call's result in order, and what it narrated.
    /// </summary>
    private sealed record SqlImportRun(List<ConnectedSystemImportResult> Pages, RecordingConnectorProgress Progress)
    {
        internal List<ConnectedSystemImportObject> ImportObjects => [.. Pages.SelectMany(page => page.ImportObjects)];
    }

    /// <summary>
    /// Drives a Full Import to completion the way the Worker does: open once, call until no pagination
    /// tokens come back, then close.
    /// </summary>
    private async Task<SqlImportRun> RunImportAsync(
        FakeSqlProvider provider,
        string objectTypesDocument,
        ConnectedSystem connectedSystem,
        int pageSize,
        string databaseTimeZone = SqlConnectorConstants.DefaultDatabaseTimeZone,
        int callLimit = 25)
    {
        var progress = new RecordingConnectorProgress();
        var pages = new List<ConnectedSystemImportResult>();
        var connector = new SqlConnector { ProviderFactory = _ => provider };

        try
        {
            connector.OpenImportConnection(SettingValues(connector, objectTypesDocument, databaseTimeZone), null, _logger);

            var runProfile = RunProfile(pageSize);
            var paginationTokens = new List<ConnectedSystemPaginationToken>();
            var initialPage = true;

            while (initialPage || paginationTokens.Count > 0)
            {
                initialPage = false;

                var result = await connector.ImportAsync(connectedSystem, runProfile, paginationTokens, null, _logger, CancellationToken.None, progress);
                pages.Add(result);
                paginationTokens = result.PaginationTokens;

                Assert.That(pages, Has.Count.LessThanOrEqualTo(callLimit),
                    "The import never stopped returning pagination tokens, which is an infinite import.");
            }
        }
        finally
        {
            connector.CloseImportConnection();
            connector.Dispose();
        }

        return new SqlImportRun(pages, progress);
    }

    private static List<ConnectedSystemSettingValue> SettingValues(SqlConnector connector, string objectTypesDocument, string databaseTimeZone = SqlConnectorConstants.DefaultDatabaseTimeZone)
    {
        var settingValues = SqlConnectorSettingValues.CreateSqlServer(connector);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, objectTypesDocument);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingDatabaseTimeZone, databaseTimeZone);
        return settingValues;
    }

    private static ConnectedSystemRunProfile RunProfile(int pageSize) =>
        new() { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = pageSize };

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

    private static ConnectedSystem DateSystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType("Person",
                Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                Attribute("STARTED", AttributeDataType.DateTime))
        ]
    };

    private static ConnectedSystem PhonesSystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType("Person",
                Attribute("EMPLOYEE_ID", AttributeDataType.Number, isExternalId: true),
                Attribute("PhoneNumbers", AttributeDataType.Text, plurality: AttributePlurality.MultiValued))
        ]
    };

    private static ConnectedSystemObjectType ObjectType(string name, params ConnectedSystemObjectTypeAttribute[] attributes) =>
        new() { Name = name, Selected = true, Attributes = [.. attributes] };

    private static ConnectedSystemObjectTypeAttribute Attribute(
        string name,
        AttributeDataType type,
        bool isExternalId = false,
        AttributePlurality plurality = AttributePlurality.SingleValued) =>
        new() { Name = name, Type = type, Selected = true, IsExternalId = isExternalId, AttributePlurality = plurality };

    private static ConnectedSystemImportObjectAttribute Attribute(ConnectedSystemImportObject importObject, string name) =>
        importObject.Attributes.Single(attribute => attribute.Name == name);

    private static ConnectedSystemImportObject ObjectWithAnchor(SqlImportRun run, int anchor) =>
        run.ImportObjects.Single(importObject => Attribute(importObject, "EMPLOYEE_ID").IntValues.Single() == anchor);

    private static List<int> AnchorValues(SqlImportRun run) =>
        [.. run.ImportObjects.Select(importObject => Attribute(importObject, "EMPLOYEE_ID").IntValues.Single())];

    #endregion
}
