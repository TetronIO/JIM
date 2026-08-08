// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using NUnit.Framework;
using Oracle.ManagedDataAccess.Types;
using Serilog;
using Serilog.Core;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the JIM SQL Connector's export: what it writes for a create, an update and a delete, how it
/// keeps one object's writes together in a transaction of their own, what it does with a statement the
/// database applied to no row, and how a value crosses back out of JIM into a database column. No test
/// here touches a database server; the dialect seam and its connection, command and transaction are
/// substituted instead.
/// </summary>
/// <remarks>
/// Not parallelisable: these tests stand in for Serilog's static logger so that what the Connector told
/// the administrator can be asserted on.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class SqlConnectorExportTests
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

    /// <summary>
    /// The same object type with a multi-valued attribute in a related table, which is what an export
    /// has to maintain alongside the parent row.
    /// </summary>
    private const string PersonWithPhonesDocument = """
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

    private const string CompositeAnchorDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "COMPANY_ID", "EMPLOYEE_ID" ] }
          ]
        }
        """;

    /// <summary>
    /// An object type identified by a GUID column, which is what an Oracle table keyed on
    /// <c>RAW(16) DEFAULT SYS_GUID()</c> and a Microsoft SQL Server table keyed on a
    /// <c>uniqueidentifier</c> both look like.
    /// </summary>
    private const string GuidAnchorDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "STAFF_GUID" ] }
          ]
        }
        """;

    /// <summary>
    /// An object type identified by a character column, which is what an Oracle table keyed on a
    /// <c>VARCHAR2</c> filled by a trigger looks like.
    /// </summary>
    private const string TextAnchorDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "STAFF_CODE" ] }
          ]
        }
        """;

    /// <summary>
    /// A composite anchor one of whose parts is a GUID column, which is the case a part bound as text
    /// cannot survive: no dialect implicitly converts a string to a uniqueidentifier or a RAW(16).
    /// </summary>
    private const string CompositeGuidAnchorDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "COMPANY_ID", "STAFF_GUID" ] }
          ]
        }
        """;

    private const string ManagerReferenceDocument = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "EMPLOYEES",
              "anchorColumns": [ "EMPLOYEE_ID" ],
              "columns": [ { "name": "MANAGER_EMPLOYEE_ID", "referencesObjectType": "Person" } ]
            }
          ]
        }
        """;

    /// <summary>
    /// The same reference, but pointing at an Object Type whose anchor is a GUID column, so the value
    /// the reference carries has to reach the database as a GUID.
    /// </summary>
    private const string ManagerGuidReferenceDocument = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "EMPLOYEES",
              "anchorColumns": [ "STAFF_GUID" ],
              "columns": [ { "name": "MANAGER_STAFF_GUID", "referencesObjectType": "Person" } ]
            }
          ]
        }
        """;

    private CapturedLogSink _capturedLog = new();
    private Logger? _testLogger;
    private ILogger _originalLogger = Logger.None;

    /// <summary>
    /// The Connector logs through Serilog's static logger, so capturing what it told the administrator
    /// means standing in for that logger and putting the original back afterwards.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _capturedLog = new CapturedLogSink();
        _originalLogger = Log.Logger;
        _testLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(_capturedLog).CreateLogger();
        Log.Logger = _testLogger;
    }

    [TearDown]
    public void TearDown()
    {
        Log.Logger = _originalLogger;
        _testLogger?.Dispose();
        _testLogger = null;
    }

    #region Create

    [Test]
    public async Task ExportAsync_ACreateAgainstAGeneratedKey_ReturnsTheGeneratedValueAsTheExternalId()
    {
        var provider = new FakeSqlProvider { GeneratedKey = 4711 };
        var pendingExport = Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("4711"),
                "A database-generated key is the new object's external ID; without it JIM cannot find the row it just created.");
            Assert.That(provider.ExecutedStatementTexts.Single(), Does.StartWith("INSERT INTO [HR].[EMPLOYEES]"));
        }
    }

    [Test]
    public async Task ExportAsync_ACreateAgainstAGeneratedKey_ReadsTheKeyFromABoundOutputParameterWhereTheDialectReturnsItThatWay()
    {
        // Oracle's mechanism: RETURNING ... INTO writes the key into a bound output parameter rather
        // than returning a result set, so the Connector has to read it from a different place entirely.
        var provider = new FakeSqlProvider
        {
            GeneratedKeyRetrievalMode = SqlGeneratedKeyRetrieval.OutputParameter,
            GeneratedKey = 4711m
        };

        var results = await ExportAsync(provider, PersonDocument, [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("4711"));
            Assert.That(provider.ExecutedStatementTexts.Single(), Does.Contain("RETURNING [EMPLOYEE_ID] INTO"));
        }
    }

    [Test]
    public async Task ExportAsync_ACreateWithTheAnchorSupplied_InsertsItDirectlyAndReturnsItAsTheExternalId()
    {
        // A user-selected external ID: JIM already knows what identifies the object, so there is nothing
        // for the database to generate and nothing to read back.
        var provider = new FakeSqlProvider();
        var pendingExport = Create(
            Change("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("4711"));
            Assert.That(provider.ExecutedStatementTexts.Single(), Does.Not.Contain("OUTPUT"),
                "The anchor was supplied, so there is no generated key to ask the database for.");
            Assert.That(provider.ExecutedStatements.Single().Parameters.Values, Does.Contain(4711).And.Contain("Ada"));
        }
    }

    [Test]
    public async Task ExportAsync_ACreateSupplyingACompositeNaturalKey_ComposesTheExternalIdAnImportOfTheSameRowWouldCompose()
    {
        // Both parts of the key are authored by Synchronisation Rules, which is the only way an Object
        // Type identified by several columns can be provisioned: a database generates one key per row,
        // never a composite one.
        var provider = new FakeSqlProvider();
        var pendingExport = Create(
            Change("COMPANY_ID", AttributeDataType.Number, number: 7),
            Change("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var results = await ExportAsync(provider, CompositeAnchorDocument, [pendingExport]);

        var insert = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo("7+4711"),
                "An import of the same row composes its external ID from the same parts, separated the same way; a different composition would make the object unfindable.");
            Assert.That(insert.CommandText, Does.StartWith("INSERT INTO [HR].[EMPLOYEES]"));
            Assert.That(insert.CommandText, Does.Contain("[COMPANY_ID]").And.Contain("[EMPLOYEE_ID]"),
                "A natural key is written as part of the row, not asked of the database.");
            Assert.That(insert.Parameters.Values, Does.Contain(7).And.Contain(4711));
        }
    }

    [Test]
    public async Task ExportAsync_ACreateAgainstAnOracleRaw16GeneratedKey_ComposesTheExternalIdAnImportOfTheSameRowWouldCompose()
    {
        // RAW(16) DEFAULT SYS_GUID() is how an Oracle table generates a GUID key. The driver returns
        // bytes, so an export that rendered what it was handed composed hex, while an import of the same
        // row composed the hyphenated form: JIM would never find the object it had just created.
        var identifier = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var provider = new FakeSqlProvider
        {
            DialectUnderTest = SqlDatabaseType.Oracle,
            GeneratedKeyRetrievalMode = SqlGeneratedKeyRetrieval.OutputParameter,
            GeneratedKey = IdentifierParser.ToRfc4122Bytes(identifier)
        };

        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("STAFF_GUID", "RAW", MaxLength: 16, IsNullable: false),
            new FakeCatalogueColumn("DISPLAY_NAME", "NVARCHAR2", MaxLength: 200));

        var results = await ExportAsync(provider, GuidAnchorDocument,
            [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))],
            configureSettings: settingValues => SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo(identifier.ToString("D")),
                "The external ID is what the confirming import has to match the row by, so it must be the same string that import composes.");
        }
    }

    [Test]
    public async Task ExportAsync_ACreateSupplyingAGuidAnchor_ComposesTheExternalIdInTheFormAnImportWould()
    {
        var identifier = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = new FakeSqlProvider();

        var results = await ExportAsync(provider, GuidAnchorDocument,
            [Create(Change("STAFF_GUID", AttributeDataType.Guid, guid: identifier), Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo(identifier.ToString("D")));
            Assert.That(provider.ExecutedStatements.Single().Parameters.Values, Does.Contain(provider.ConvertFromGuid(identifier)),
                "The value written to the column still crosses the seam through the provider, whatever the external ID reads as.");
        }
    }

    [Test]
    public async Task ExportAsync_ACreateSupplyingACompositeAnchorMixingAGuidAndANumber_ComposesBothPartsByTheirOwnType()
    {
        var identifier = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("COMPANY_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("STAFF_GUID", "RAW", MaxLength: 16, IsNullable: false),
            new FakeCatalogueColumn("DISPLAY_NAME", "NVARCHAR2", MaxLength: 200));

        var results = await ExportAsync(provider, CompositeGuidAnchorDocument,
        [
            Create(
                Change("COMPANY_ID", AttributeDataType.Number, number: 7),
                Change("STAFF_GUID", AttributeDataType.Guid, guid: identifier),
                Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))
        ],
            configureSettings: settingValues => SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[0].ExternalId, Is.EqualTo($"7+{identifier:D}"),
                "Each part of a composed external ID is rendered by its own column's type, exactly as an import of the same row renders it.");
        }
    }

    [Test]
    public async Task ExportAsync_ACreateAgainstAnAnchorColumnJimHasNoTypeFor_FailsThatObjectRatherThanGuessingItsExternalId()
    {
        // The external ID has to be composed by the anchor column's own type, so a column JIM cannot type
        // fails the object naming it. Assuming an exact numeric because identities and sequences usually
        // generate one is what let a GUID key be recorded as hex.
        var provider = new FakeSqlProvider { GeneratedKey = 4711 };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "geography", IsNullable: false),
            new FakeCatalogueColumn("DISPLAY_NAME", "nvarchar", MaxLength: 200));

        var results = await ExportAsync(provider, PersonDocument, [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("EMPLOYEE_ID").And.Contain("geography"),
                "The administrator has to be told which column identifies the object type and what type it is.");
            Assert.That(provider.ExecutedStatements, Is.Empty, "Nothing is written for an object JIM could never find again.");
        }
    }

    [Test]
    public async Task ExportAsync_ACreateSupplyingOnlyPartOfACompositeAnchor_FailsThatObjectRatherThanHalfWritingIt()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Create(
            Change("COMPANY_ID", AttributeDataType.Number, number: 7),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var results = await ExportAsync(provider, CompositeAnchorDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False,
                "A partly supplied composite anchor is neither JIM's to compose nor the database's to generate.");
            Assert.That(results[0].ErrorMessage, Does.Contain("Person"));
            Assert.That(provider.ExecutedStatements, Is.Empty, "Nothing is written for an object JIM could not identify.");
            Assert.That(provider.Transactions.Single().Committed, Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_ACreateSupplyingAnAnchorColumnWithNoValue_FailsThatObjectRatherThanRecordingAnEmptyExternalId()
    {
        // A supplied anchor holding nothing would compose to an empty external ID, and JIM would record
        // a Connected System Object it could never find the row for again.
        var provider = new FakeSqlProvider();
        var pendingExport = Create(
            Change("EMPLOYEE_ID", AttributeDataType.Number),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("EMPLOYEE_ID"));
            Assert.That(provider.ExecutedStatements, Is.Empty);
            Assert.That(provider.Transactions.Single().Committed, Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_ACreateWithMultiValuedValues_WritesTheRelatedRowsAgainstTheGeneratedKey()
    {
        var provider = new FakeSqlProvider { GeneratedKey = 4711 };
        var pendingExport = Create(
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Add));

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        var relatedInsert = provider.ExecutedStatements.Single(command => command.CommandText.Contains("EMPLOYEE_PHONES", StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(relatedInsert.CommandText, Does.StartWith("INSERT INTO [HR].[EMPLOYEE_PHONES]"));
            Assert.That(relatedInsert.Parameters.Values, Does.Contain("555-0100"));
            Assert.That(relatedInsert.Parameters.Values.Select(value => value?.ToString()), Does.Contain("4711"),
                "A related row belongs to the parent the database has just generated a key for, so it can only be written after the parent row.");
        }
    }

    #endregion

    #region Transactions

    [Test]
    public async Task ExportAsync_ASuccessfulObject_CommitsItsOwnTransactionWithEveryWriteInsideIt()
    {
        var provider = new FakeSqlProvider { GeneratedKey = 4711 };
        var pendingExport = Create(
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Add));

        await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        var transaction = provider.Transactions.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Committed, Is.True);
            Assert.That(transaction.RolledBack, Is.False);
            Assert.That(provider.ExecutedStatements.Select(command => command.Transaction), Is.All.SameAs(transaction),
                "The parent row and its related rows go in together or not at all, so every statement runs in the object's own transaction.");
        }
    }

    [Test]
    public async Task ExportAsync_AnObjectWhoseRelatedWriteFails_RollsBackTheParentRowWithIt()
    {
        var provider = new FakeSqlProvider { GeneratedKey = 4711, FailWhenCommandTextContains = "EMPLOYEE_PHONES" };
        var pendingExport = Create(
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Add));

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        var transaction = provider.Transactions.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(transaction.RolledBack, Is.True,
                "A half-written object is worse than an unwritten one: the parent row must not survive its related rows failing.");
            Assert.That(transaction.Committed, Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_OneObjectFails_TheRestOfTheBatchStillSucceedsAndResultsStayPositional()
    {
        var provider = new FakeSqlProvider { GeneratedKey = 4711, FailWhenCommandTextContains = "Grace" };

        // The failing object is named in the statement only because this stand-in matches on text; what
        // matters is that exactly one of the three writes is refused.
        var results = await ExportAsync(provider, PersonDocument,
        [
            Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada")),
            Create(Change("Grace", AttributeDataType.Text, text: "Grace")),
            Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Katherine"))
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(3), "JIM matches results to Pending Exports by position, so there is exactly one result per object.");
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[1].Success, Is.False, "The middle object is the one the database refused.");
            Assert.That(results[1].ErrorMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(results[2].Success, Is.True, "A failed object must not poison the batch it arrived in.");
            Assert.That(provider.Transactions.Count(transaction => transaction.Committed), Is.EqualTo(2));
            Assert.That(provider.Transactions.Count(transaction => transaction.RolledBack), Is.EqualTo(1));
        }
    }

    #endregion

    #region Update

    [Test]
    public async Task ExportAsync_AnUpdate_WritesOnlyTheChangedColumnsKeyedOnTheAnchor()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada Lovelace", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        var update = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(update.CommandText, Does.StartWith("UPDATE [HR].[EMPLOYEES] SET [DISPLAY_NAME] = @"));
            Assert.That(update.CommandText, Does.Contain("WHERE [EMPLOYEE_ID] = @"));
            Assert.That(update.Parameters.Values, Does.Contain("Ada Lovelace").And.Contain(4711));
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateRemovingASingleValuedValue_WritesNull()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Remove));

        await ExportAsync(provider, PersonDocument, [pendingExport]);

        var update = provider.ExecutedStatements.Single();
        var displayNameParameter = ParameterFor(update, "DISPLAY_NAME");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(update.CommandText, Does.Contain("SET [DISPLAY_NAME] = @"));
            Assert.That(update.Parameters[displayNameParameter], Is.Null,
                "A single-valued attribute has no value to remove from; removing its value means the column holds nothing.");
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateWhoseChangesIncludeAnAnchorColumn_FailsThatObjectRatherThanRewritingThePrimaryKey()
    {
        // The engine already keeps a Writable On Create attribute out of an Update Pending Export. This
        // is the Connector's own guard behind it, because rewriting a primary key severs the link
        // between the Connected System Object and its row without raising anything.
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("EMPLOYEE_ID", AttributeDataType.Number, number: 9000, changeType: PendingExportAttributeChangeType.Update),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        var transaction = provider.Transactions.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("Person").And.Contain("EMPLOYEE_ID"),
                "The administrator has to be told which Object Type and which column the update would have rewritten.");
            Assert.That(provider.ExecutedStatements, Is.Empty, "The whole object is refused, not just the offending column.");
            Assert.That(transaction.RolledBack, Is.True);
            Assert.That(transaction.Committed, Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateWhoseChangesIncludeAnAnchorColumn_LeavesTheRestOfTheBatchSucceedingAndItsResultsPositional()
    {
        var provider = new FakeSqlProvider();

        var results = await ExportAsync(provider, PersonDocument,
        [
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
                Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update)),
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4712),
                Change("EMPLOYEE_ID", AttributeDataType.Number, number: 9000, changeType: PendingExportAttributeChangeType.Update)),
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4713),
                Change("DISPLAY_NAME", AttributeDataType.Text, text: "Katherine", changeType: PendingExportAttributeChangeType.Update))
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(3), "JIM matches results to Pending Exports by position, so there is exactly one result per object.");
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[1].Success, Is.False, "The middle object is the one that would have rewritten its own anchor.");
            Assert.That(results[2].Success, Is.True, "A refused object must not poison the batch it arrived in.");
            Assert.That(provider.Transactions.Count(transaction => transaction.Committed), Is.EqualTo(2));
            Assert.That(provider.Transactions.Count(transaction => transaction.RolledBack), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateAddingAMultiValuedValue_InsertsARelatedRowInTheParentTransaction()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Add));

        await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        var insert = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(insert.CommandText, Does.StartWith("INSERT INTO [HR].[EMPLOYEE_PHONES] ([EMPLOYEE_ID], [PHONE_NUMBER])"));
            Assert.That(insert.Parameters.Values, Does.Contain("555-0100").And.Contain(4711));
            Assert.That(insert.Transaction, Is.SameAs(provider.Transactions.Single()));
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateRemovingAMultiValuedValue_DeletesTheRelatedRowInTheParentTransaction()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Remove));

        await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        var delete = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delete.CommandText, Does.StartWith("DELETE FROM [HR].[EMPLOYEE_PHONES] WHERE [EMPLOYEE_ID] = @"));
            Assert.That(delete.CommandText, Does.Contain("[PHONE_NUMBER] = @"),
                "Removing one value of a multi-valued attribute removes one related row, never every row the parent has.");
            Assert.That(delete.Parameters.Values, Does.Contain("555-0100").And.Contain(4711));
            Assert.That(delete.Transaction, Is.SameAs(provider.Transactions.Single()));
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateClearingAMultiValuedAttribute_DeletesEveryRelatedRowForThatParentOnly()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("PhoneNumbers", AttributeDataType.Text, plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.RemoveAll));

        await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        var delete = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delete.CommandText, Is.EqualTo("DELETE FROM [HR].[EMPLOYEE_PHONES] WHERE [EMPLOYEE_ID] = @exAnchor0"));
            Assert.That(delete.Parameters.Values, Does.Contain(4711));
        }
    }

    #endregion

    #region Delete

    [Test]
    public async Task ExportAsync_ADelete_RemovesTheRelatedRowsBeforeTheParentRowInOneTransaction()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Delete(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711));

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(provider.ExecutedStatementTexts, Is.EqualTo(new[]
            {
                "DELETE FROM [HR].[EMPLOYEE_PHONES] WHERE [EMPLOYEE_ID] = @exAnchor0",
                "DELETE FROM [HR].[EMPLOYEES] WHERE [EMPLOYEE_ID] = @exAnchor0"
            }), "A related row referencing its parent cannot outlive it, so the children go first.");

            Assert.That(provider.ExecutedStatements.Select(command => command.Transaction), Is.All.SameAs(provider.Transactions.Single()));
        }
    }

    #endregion

    #region Rows affected

    [Test]
    public async Task ExportAsync_AnUpdateMatchingNoRow_FailsTheObjectRatherThanConfirmingValuesTheDatabaseNeverTook()
    {
        // The Connector confirms an export's values without a confirming import, so an UPDATE that
        // matched nothing would have JIM record attribute values against a row that does not exist.
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "UPDATE [HR].[EMPLOYEES]" };
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada Lovelace", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        var transaction = provider.Transactions.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False,
                "A statement that matched no row is the one failure a driver never raises, so the row count is all that stands between JIM and a confirmed write that never happened.");
            Assert.That(results[0].ErrorMessage, Does.Contain("HR.EMPLOYEES"), "The administrator has to be told which table was not written to.");
            Assert.That(results[0].ErrorMessage, Does.Contain("Full Import"), "The Connected System Object is stale, and the message has to say what reconciles it.");
            Assert.That(transaction.RolledBack, Is.True);
            Assert.That(transaction.Committed, Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_AnUpdateMatchingNoRow_LeavesTheRestOfTheBatchSucceedingAndItsResultsPositional()
    {
        // The middle object's statement is the only one naming this column, which is how this stand-in
        // singles it out; what matters is that exactly one of the three matches no row.
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "[Grace]" };

        var results = await ExportAsync(provider, PersonDocument,
        [
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
                Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update)),
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4712),
                Change("Grace", AttributeDataType.Text, text: "Grace", changeType: PendingExportAttributeChangeType.Update)),
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4713),
                Change("DISPLAY_NAME", AttributeDataType.Text, text: "Katherine", changeType: PendingExportAttributeChangeType.Update))
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(3), "JIM matches results to Pending Exports by position, so there is exactly one result per object.");
            Assert.That(results[0].Success, Is.True);
            Assert.That(results[1].Success, Is.False, "The middle object is the one whose row is no longer there.");
            Assert.That(results[2].Success, Is.True, "An object whose row went missing must not poison the batch it arrived in.");
            Assert.That(provider.Transactions.Count(transaction => transaction.Committed), Is.EqualTo(2));
            Assert.That(provider.Transactions.Count(transaction => transaction.RolledBack), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ExportAsync_ADeleteMatchingNoRow_SucceedsAndWarnsBecauseTheRowIsAlreadyGone()
    {
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "DELETE FROM [HR].[EMPLOYEES]" };

        var results = await ExportAsync(provider, PersonDocument, [Delete(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True,
                "The end state a delete asks for is a row that is not there, which is already the case; failing would retry an object that can never succeed.");
            Assert.That(provider.Transactions.Single().Committed, Is.True);
            Assert.That(_capturedLog.Warnings, Has.Some.Contains("HR.EMPLOYEES"),
                "A row that went missing before JIM removed it still says something about the Connected System, so it is warned about rather than passed over.");
        }
    }

    [Test]
    public async Task ExportAsync_ADeleteWhoseRelatedRowsAreAlreadyGone_SucceedsWithoutWarning()
    {
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "DELETE FROM [HR].[EMPLOYEE_PHONES]" };

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [Delete(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(provider.Transactions.Single().Committed, Is.True);
            Assert.That(_capturedLog.Warnings, Is.Empty,
                "An object with no related rows to remove is the ordinary case, not something to tell the administrator about.");
        }
    }

    [Test]
    public async Task ExportAsync_RemovingAMultiValuedValueThatTheTableDoesNotHold_Succeeds()
    {
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "DELETE FROM [HR].[EMPLOYEE_PHONES]" };
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Remove));

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, "Removing a value the table does not hold has reached exactly the end state it asked for.");
            Assert.That(provider.Transactions.Single().Committed, Is.True);
        }
    }

    [Test]
    public async Task ExportAsync_ClearingAMultiValuedAttributeThatIsAlreadyEmpty_Succeeds()
    {
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "DELETE FROM [HR].[EMPLOYEE_PHONES]" };
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("PhoneNumbers", AttributeDataType.Text, plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.RemoveAll));

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(provider.Transactions.Single().Committed, Is.True);
        }
    }

    [Test]
    public async Task ExportAsync_ARelatedRowInsertMatchingNoRow_FailsTheObject()
    {
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "INSERT INTO [HR].[EMPLOYEE_PHONES]" };
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("PhoneNumbers", AttributeDataType.Text, text: "555-0100", plurality: AttributePlurality.MultiValued, changeType: PendingExportAttributeChangeType.Add));

        var results = await ExportAsync(provider, PersonWithPhonesDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False,
                "An insert that wrote nothing and raised nothing is a trigger or a rule discarding the write; confirming the value would be a lie.");
            Assert.That(results[0].ErrorMessage, Does.Contain("PhoneNumbers"));
            Assert.That(provider.Transactions.Single().RolledBack, Is.True);
        }
    }

    [Test]
    public async Task ExportAsync_ACreateWhoseInsertMatchesNoRow_FailsTheObject()
    {
        var provider = new FakeSqlProvider { AffectsNoRowsWhenCommandTextContains = "INSERT INTO [HR].[EMPLOYEES]" };
        var pendingExport = Create(
            Change("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False, "JIM would otherwise record a Connected System Object for a row the database never took.");
            Assert.That(results[0].ErrorMessage, Does.Contain("HR.EMPLOYEES"));
            Assert.That(provider.Transactions.Single().RolledBack, Is.True);
        }
    }

    [Test]
    public async Task ExportAsync_ACreateAgainstAGeneratedKeyWhoseInsertMatchesNoRow_FailsTheObject()
    {
        // The dialects that return a generated key through a bound parameter can hand one back from a
        // statement that wrote no row at all, so the key coming back is not on its own proof of a write.
        var provider = new FakeSqlProvider
        {
            GeneratedKeyRetrievalMode = SqlGeneratedKeyRetrieval.OutputParameter,
            GeneratedKey = 4711m,
            AffectsNoRowsWhenCommandTextContains = "INSERT INTO [HR].[EMPLOYEES]"
        };

        var results = await ExportAsync(provider, PersonDocument, [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(provider.Transactions.Single().RolledBack, Is.True);
        }
    }

    #endregion

    #region Oracle driver values

    /// <summary>
    /// A sequence-backed <c>NUMBER</c> primary key, which is the ordinary way an Oracle table generates
    /// one. ODP.NET never hands a CLR primitive back through an output parameter, so every create
    /// against one failed on a cast the driver's own wrapper struct could not satisfy.
    /// </summary>
    [Test]
    public async Task ExportAsync_AnOracleNumberKeyReturnedAsAnOracleDecimal_ComposesTheExternalIdAnImportOfTheSameRowWouldCompose()
    {
        var provider = OracleGeneratedKeyProvider(new OracleDecimal(4711));

        var results = await ExportAsync(provider, PersonDocument, [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].ExternalId, Is.EqualTo("4711"),
                "The driver's own numeric wrapper has to be unwrapped before the external ID is composed, or the object is never findable again.");
            Assert.That(provider.Transactions.Single().Committed, Is.True);
        }
    }

    /// <summary>
    /// <c>RAW(16) DEFAULT SYS_GUID()</c>, whose key ODP.NET returns as an <c>OracleBinary</c> rather
    /// than as the <c>byte[]</c> the same column reads back as through a data reader.
    /// </summary>
    [Test]
    public async Task ExportAsync_AnOracleRaw16KeyReturnedAsAnOracleBinary_ComposesTheGuidInOraclesOwnByteOrder()
    {
        var identifier = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var provider = OracleGeneratedKeyProvider(new OracleBinary(IdentifierParser.ToRfc4122Bytes(identifier)));

        var results = await ExportAsync(provider, GuidAnchorDocument,
            [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))],
            configureSettings: settingValues => SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].ExternalId, Is.EqualTo(identifier.ToString("D")),
                "Oracle stores a GUID big-endian, so the unwrapped bytes still have to cross the dialect seam before they are rendered.");
        }
    }

    /// <summary>
    /// A text key, which happened to survive because <see cref="Convert.ToString(object?, IFormatProvider?)"/>
    /// falls back to a wrapper's own <c>ToString</c>. It is covered so the unwrapping cannot regress it.
    /// </summary>
    [Test]
    public async Task ExportAsync_AnOracleTextKeyReturnedAsAnOracleString_ComposesTheExternalIdFromTheTextItHolds()
    {
        var provider = OracleGeneratedKeyProvider(new OracleString("EMP-4711"));

        var results = await ExportAsync(provider, TextAnchorDocument, [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            Assert.That(results[0].ExternalId, Is.EqualTo("EMP-4711"));
        }
    }

    /// <summary>
    /// ODP.NET expresses "no value" with a null sentinel of the wrapper's own type, which is not
    /// <see cref="DBNull"/>: a null <c>OracleDecimal</c> is a perfectly ordinary boxed struct. Read
    /// without unwrapping, an <c>OracleString.Null</c> composes an external ID out of the wrapper's
    /// <c>ToString</c>, which is worse than the failure it should have been.
    /// </summary>
    [TestCaseSource(nameof(OracleNullSentinels))]
    public async Task ExportAsync_AnOracleKeyThatCameBackNull_FailsTheObjectAndRollsItBack(object nullSentinel, string objectTypesDocument, string anchorColumn)
    {
        var provider = OracleGeneratedKeyProvider(nullSentinel);

        var results = await ExportAsync(provider, objectTypesDocument,
            [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"))],
            configureSettings: settingValues => SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False, "Nothing would identify the new object, so the create cannot be confirmed.");
            Assert.That(results[0].ErrorMessage, Does.Contain($"returned no value for its anchor column '{anchorColumn}'"));
            Assert.That(provider.Transactions.Single().RolledBack, Is.True);
        }
    }

    /// <summary>
    /// The null sentinel of every wrapper this Connector can bind a generated key as, each against the
    /// kind of anchor column that wrapper actually comes back from.
    /// </summary>
    private static IEnumerable<TestCaseData> OracleNullSentinels()
    {
        yield return new TestCaseData(OracleDecimal.Null, PersonDocument, "EMPLOYEE_ID").SetArgDisplayNames("OracleDecimal.Null");
        yield return new TestCaseData(OracleString.Null, TextAnchorDocument, "STAFF_CODE").SetArgDisplayNames("OracleString.Null");
        yield return new TestCaseData(OracleBinary.Null, GuidAnchorDocument, "STAFF_GUID").SetArgDisplayNames("OracleBinary.Null");
    }

    /// <summary>
    /// A stand-in Oracle Database that generates the given key and hands it back the way ODP.NET does:
    /// through a bound output parameter, as the driver's own wrapper rather than as a CLR value.
    /// </summary>
    private static FakeSqlProvider OracleGeneratedKeyProvider(object? generatedKey)
    {
        var provider = new FakeSqlProvider
        {
            DialectUnderTest = SqlDatabaseType.Oracle,
            GeneratedKeyRetrievalMode = SqlGeneratedKeyRetrieval.OutputParameter,
            GeneratedKey = generatedKey
        };

        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("STAFF_CODE", "VARCHAR2", MaxLength: 30, IsNullable: false),
            new FakeCatalogueColumn("STAFF_GUID", "RAW", MaxLength: 16, IsNullable: false),
            new FakeCatalogueColumn("DISPLAY_NAME", "NVARCHAR2", MaxLength: 200));

        return provider;
    }

    #endregion

    #region Composite anchors

    [Test]
    public async Task ExportAsync_ACompositeAnchor_KeysTheUpdateOnEveryAnchorColumn()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("COMPANY_ID+EMPLOYEE_ID", AttributeDataType.Text, text: "7+4711"),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, CompositeAnchorDocument, [pendingExport]);

        var update = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(update.CommandText, Does.Contain("WHERE [COMPANY_ID] = @exAnchor0 AND [EMPLOYEE_ID] = @exAnchor1"),
                "Keying on part of a composite anchor updates another object's row, silently.");
            Assert.That(update.Parameters["exAnchor0"], Is.EqualTo(7),
                "A part of a composed external ID is text in JIM and an integer in the table, so it is bound as the column's own type.");
            Assert.That(update.Parameters["exAnchor1"], Is.EqualTo(4711));
        }
    }

    [Test]
    public async Task ExportAsync_ACompositeAnchorPartAgainstAGuidColumn_BindsAGuidRatherThanItsText()
    {
        // The case a part bound as text cannot survive: no dialect implicitly converts a string to a
        // uniqueidentifier, so the statement is refused and every object of the type fails.
        var identifier = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("COMPANY_ID+STAFF_GUID", AttributeDataType.Text, text: $"7+{identifier:D}"),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, CompositeGuidAnchorDocument, [pendingExport]);

        var update = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(update.Parameters["exAnchor0"], Is.EqualTo(7));
            Assert.That(update.Parameters["exAnchor1"], Is.EqualTo(provider.ConvertFromGuid(identifier)),
                "Byte order is dialect-specific, so an anchor part crosses the seam through the provider or it is silently transposed.");
        }
    }

    [Test]
    public async Task ExportAsync_ACompositeAnchorPartThatIsNotTheColumnsType_FailsThatObjectNamingTheColumnAndItsType()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("COMPANY_ID+STAFF_GUID", AttributeDataType.Text, text: "7+not-a-guid"),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, CompositeGuidAnchorDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("STAFF_GUID").And.Contain("uniqueidentifier"),
                "The administrator has to be told which column would not take the value, and what type it is.");
            Assert.That(provider.ExecutedStatements, Is.Empty, "Nothing is written against an anchor the column cannot hold.");
        }
    }

    [Test]
    public async Task ExportAsync_ACompositeAnchor_KeysTheDeleteOnEveryAnchorColumn()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Delete(Anchor("COMPANY_ID+EMPLOYEE_ID", AttributeDataType.Text, text: "7+4711"));

        await ExportAsync(provider, CompositeAnchorDocument, [pendingExport]);

        Assert.That(provider.ExecutedStatementTexts.Single(),
            Is.EqualTo("DELETE FROM [HR].[EMPLOYEES] WHERE [COMPANY_ID] = @exAnchor0 AND [EMPLOYEE_ID] = @exAnchor1"));
    }

    [Test]
    public async Task ExportAsync_ACompositeAnchorWithTheWrongNumberOfParts_FailsThatObjectRatherThanGuessing()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("COMPANY_ID+EMPLOYEE_ID", AttributeDataType.Text, text: "4711"),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, CompositeAnchorDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(provider.ExecutedStatements, Is.Empty, "Nothing is written against an anchor JIM cannot read.");
        }
    }

    #endregion

    #region Values

    [Test]
    public async Task ExportAsync_ADecimalValue_BindsTheExactDecimalRatherThanAFloatingPointApproximation()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("FTE", AttributeDataType.Decimal, dec: 0.875m, changeType: PendingExportAttributeChangeType.Update));

        await ExportAsync(provider, PersonDocument, [pendingExport]);

        var bound = provider.ExecutedStatements.Single().Parameters[ParameterFor(provider.ExecutedStatements.Single(), "FTE")];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bound, Is.TypeOf<decimal>(), "Routing an exact numeric through a floating point type drops digits without any error.");
            Assert.That(bound, Is.EqualTo(0.875m));
        }
    }

    [Test]
    public async Task ExportAsync_ADecimalValueHeldAsItsCanonicalString_ParsesItBackToTheSameDecimal()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("FTE", AttributeDataType.Decimal, text: DecimalAttributeValue.ToCanonicalString(0.875m), changeType: PendingExportAttributeChangeType.Update));

        await ExportAsync(provider, PersonDocument, [pendingExport]);

        var bound = provider.ExecutedStatements.Single().Parameters[ParameterFor(provider.ExecutedStatements.Single(), "FTE")];

        Assert.That(bound, Is.EqualTo(0.875m));
    }

    [Test]
    public async Task ExportAsync_AGuidValue_BindsItThroughTheProvidersOwnConversion()
    {
        var identifier = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("STAFF_GUID", AttributeDataType.Guid, guid: identifier, changeType: PendingExportAttributeChangeType.Update));

        await ExportAsync(provider, PersonDocument, [pendingExport]);

        var bound = provider.ExecutedStatements.Single().Parameters[ParameterFor(provider.ExecutedStatements.Single(), "STAFF_GUID")];

        Assert.That(bound, Is.EqualTo(provider.ConvertFromGuid(identifier)),
            "Byte order is dialect-specific, so a GUID crosses the seam through the provider or it is silently transposed.");
    }

    [Test]
    public async Task ExportAsync_AZonelessDateTimeColumn_WritesLocalWallClockTimeInTheConfiguredZone()
    {
        // JIM holds every date and time in UTC; a column carrying no offset holds wall-clock time in
        // whichever zone the administrator declared, so export has to invert exactly what import applied.
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("START_DATE", AttributeDataType.DateTime, dateTime: new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), changeType: PendingExportAttributeChangeType.Update));

        await ExportAsync(provider, PersonDocument, [pendingExport], databaseTimeZone: "Europe/London");

        var bound = provider.ExecutedStatements.Single().Parameters[ParameterFor(provider.ExecutedStatements.Single(), "START_DATE")];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bound, Is.EqualTo(new DateTime(2026, 7, 1, 13, 0, 0)), "British Summer Time is one hour ahead of UTC on that date.");
            Assert.That(((DateTime)bound!).Kind, Is.EqualTo(DateTimeKind.Unspecified), "The column carries no offset, so neither does the value written into it.");
        }
    }

    [Test]
    public async Task ExportAsync_AnOffsetCarryingDateTimeColumn_WritesTheUtcInstantWithoutApplyingTheConfiguredZone()
    {
        // The Database Time Zone setting exists to interpret columns that carry no offset. A column that
        // carries one needs no interpreting, and applying the zone to it moves the instant by the zone's
        // offset without any error (PRD requirement 9).
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("LAST_REVIEWED", AttributeDataType.DateTime, dateTime: new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), changeType: PendingExportAttributeChangeType.Update));

        await ExportAsync(provider, PersonDocument, [pendingExport], databaseTimeZone: "Europe/London");

        var bound = provider.ExecutedStatements.Single().Parameters[ParameterFor(provider.ExecutedStatements.Single(), "LAST_REVIEWED")];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bound, Is.EqualTo(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)),
                "An offset-carrying column takes the instant JIM holds; shifting it by the Connected System's zone would write a different moment in time.");
            Assert.That(((DateTimeOffset)bound!).Offset, Is.EqualTo(TimeSpan.Zero), "JIM holds every value in UTC, so that is the offset it states.");
        }
    }

    [Test]
    public async Task ExportAsync_ADateTimeInAUtcDatabase_WritesTheSameInstantToBothKindsOfColumn()
    {
        var provider = new FakeSqlProvider();
        var instant = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("START_DATE", AttributeDataType.DateTime, dateTime: instant, changeType: PendingExportAttributeChangeType.Update),
            Change("LAST_REVIEWED", AttributeDataType.DateTime, dateTime: instant, changeType: PendingExportAttributeChangeType.Update));

        await ExportAsync(provider, PersonDocument, [pendingExport]);

        var update = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(update.Parameters[ParameterFor(update, "START_DATE")], Is.EqualTo(new DateTime(2026, 7, 1, 12, 0, 0)));
            Assert.That(update.Parameters[ParameterFor(update, "LAST_REVIEWED")], Is.EqualTo(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)),
                "At the UTC default neither kind of column is shifted, so the two carry the same instant.");
        }
    }

    [Test]
    public async Task ExportAsync_AResolvedReferenceToANumericColumn_WritesTheReferencedObjectsAnchorAsANumber()
    {
        var provider = new FakeSqlProvider();
        var reference = Change("MANAGER_EMPLOYEE_ID", AttributeDataType.Reference, text: "1234", changeType: PendingExportAttributeChangeType.Update);
        reference.ResolvedReferenceCsoId = Guid.NewGuid();

        var results = await ExportAsync(provider, ManagerReferenceDocument,
            [Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711), reference)]);

        var bound = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(bound.Parameters[ParameterFor(bound, "MANAGER_EMPLOYEE_ID")], Is.EqualTo(1234),
                "A reference carries the referenced object's anchor as text; the column it goes into is an integer, and that is what is bound.");
        }
    }

    [Test]
    public async Task ExportAsync_AResolvedReferenceToAGuidColumn_BindsAGuidRatherThanItsText()
    {
        // A uniqueidentifier column takes no implicit conversion from a string, so a reference bound as
        // text fails the write outright; the same is true of an Oracle RAW(16).
        var identifier = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = new FakeSqlProvider();
        var reference = Change("MANAGER_STAFF_GUID", AttributeDataType.Reference, text: identifier.ToString("D"), changeType: PendingExportAttributeChangeType.Update);
        reference.ResolvedReferenceCsoId = Guid.NewGuid();

        var results = await ExportAsync(provider, ManagerGuidReferenceDocument,
            [Update(Anchor("STAFF_GUID", AttributeDataType.Guid, guid: Guid.NewGuid()), reference)]);

        var bound = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(bound.Parameters[ParameterFor(bound, "MANAGER_STAFF_GUID")], Is.EqualTo(provider.ConvertFromGuid(identifier)),
                "Byte order is dialect-specific, so a reference crosses the seam through the provider or it is silently transposed.");
        }
    }

    [Test]
    public async Task ExportAsync_AResolvedReferenceToAnOracleRaw16Column_BindsAGuidWhereTheAdministratorDeclaredThatColumnAGuid()
    {
        // RAW(16) is as commonly a digest as a GUID, so the reinterpretation is an opt-in the export has
        // to honour exactly as discovery and import do.
        var identifier = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("STAFF_GUID", "RAW", MaxLength: 16, IsNullable: false),
            new FakeCatalogueColumn("MANAGER_STAFF_GUID", "RAW", MaxLength: 16));

        var reference = Change("MANAGER_STAFF_GUID", AttributeDataType.Reference, text: identifier.ToString("D"), changeType: PendingExportAttributeChangeType.Update);
        reference.ResolvedReferenceCsoId = Guid.NewGuid();

        var results = await ExportAsync(provider, ManagerGuidReferenceDocument,
            [Update(Anchor("STAFF_GUID", AttributeDataType.Guid, guid: Guid.NewGuid()), reference)],
            configureSettings: settingValues => SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, true));

        var bound = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.True);
            Assert.That(bound.Parameters[ParameterFor(bound, "MANAGER_STAFF_GUID")], Is.EqualTo(provider.ConvertFromGuid(identifier)));
        }
    }

    [Test]
    public async Task ExportAsync_AReferenceThatIsNotTheColumnsType_FailsThatObjectAndLeavesTheBatchAlone()
    {
        var provider = new FakeSqlProvider();
        var unusable = Change("MANAGER_STAFF_GUID", AttributeDataType.Reference, text: "not-a-guid", changeType: PendingExportAttributeChangeType.Update);
        unusable.ResolvedReferenceCsoId = Guid.NewGuid();

        var usable = Change("MANAGER_STAFF_GUID", AttributeDataType.Reference, text: Guid.NewGuid().ToString("D"), changeType: PendingExportAttributeChangeType.Update);
        usable.ResolvedReferenceCsoId = Guid.NewGuid();

        var results = await ExportAsync(provider, ManagerGuidReferenceDocument,
        [
            Update(Anchor("STAFF_GUID", AttributeDataType.Guid, guid: Guid.NewGuid()), unusable),
            Update(Anchor("STAFF_GUID", AttributeDataType.Guid, guid: Guid.NewGuid()), usable)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("MANAGER_STAFF_GUID").And.Contain("uniqueidentifier"),
                "The administrator has to be told which attribute, which column and which type would not take the value.");
            Assert.That(results[1].Success, Is.True, "A value one object cannot write must not poison the batch it arrived in.");
            Assert.That(provider.ExecutedStatements, Has.Count.EqualTo(1), "Only the object whose value converts is written.");
        }
    }

    [Test]
    public async Task ExportAsync_AColumnTheCatalogueDoesNotDescribe_FailsThatObjectAskingForASchemaImport()
    {
        // The table changed under the Object Types document. Binding the value as text anyway would have
        // the database decide what it meant, which is exactly what reading the catalogue is here to stop.
        var provider = new FakeSqlProvider();
        var pendingExport = Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711),
            Change("RETIRED_COLUMN", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update));

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("RETIRED_COLUMN").And.Contain("HR.EMPLOYEES"));
            Assert.That(results[0].ErrorMessage, Does.Contain("Import the schema"),
                "The Object Types document and the table have diverged, and the message has to say what reconciles them.");
            Assert.That(provider.ExecutedStatements, Is.Empty);
        }
    }

    [Test]
    public async Task ExportAsync_ManyObjectsOfOneObjectType_ReadsTheColumnCatalogueOncePerObjectTypeRatherThanPerObject()
    {
        var provider = new FakeSqlProvider();

        await ExportAsync(provider, PersonWithPhonesDocument,
        [
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711), Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada", changeType: PendingExportAttributeChangeType.Update)),
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4712), Change("DISPLAY_NAME", AttributeDataType.Text, text: "Grace", changeType: PendingExportAttributeChangeType.Update)),
            Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4713), Change("DISPLAY_NAME", AttributeDataType.Text, text: "Katherine", changeType: PendingExportAttributeChangeType.Update))
        ]);

        Assert.That(provider.ColumnCatalogueReadCount, Is.EqualTo(2),
            "One read for the parent table and one for its related table, for the whole batch: a catalogue read per object would be a round trip per object.");
    }

    [Test]
    public async Task ExportAsync_AnUnresolvedReference_FailsThatObjectRatherThanWritingAWrongValue()
    {
        var provider = new FakeSqlProvider();
        var reference = Change("MANAGER_EMPLOYEE_ID", AttributeDataType.Reference, changeType: PendingExportAttributeChangeType.Update);
        reference.UnresolvedReferenceValue = Guid.NewGuid().ToString();

        var results = await ExportAsync(provider, ManagerReferenceDocument,
            [Update(Anchor("EMPLOYEE_ID", AttributeDataType.Number, number: 4711), reference)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("MANAGER_EMPLOYEE_ID"));
            Assert.That(provider.ExecutedStatements, Is.Empty,
                "A reference JIM has not resolved yet has no anchor to write; writing anything else would point the row at the wrong object.");
            Assert.That(provider.Transactions.Single().Committed, Is.False);
        }
    }

    [Test]
    public async Task ExportAsync_AnyStatement_BindsEveryValueRatherThanInterpolatingIt()
    {
        var provider = new FakeSqlProvider { GeneratedKey = 4711 };

        // A value that would end the statement early if it were interpolated rather than bound.
        const string hostileValue = "Ada'); DROP TABLE [HR].[EMPLOYEES]; --";

        await ExportAsync(provider, PersonDocument, [Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: hostileValue))]);

        var insert = provider.ExecutedStatements.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(insert.CommandText, Does.Not.Contain(hostileValue), "A value in the statement text is a value JIM failed to parameterise.");
            Assert.That(insert.CommandText, Does.Not.Contain("DROP"));
            Assert.That(insert.Parameters.Values, Does.Contain(hostileValue));
        }
    }

    #endregion

    #region Configuration and lifecycle

    [Test]
    public async Task ExportAsync_AnObjectTypeTheDocumentDoesNotDeclare_FailsThatObjectWithAMessageNamingIt()
    {
        var provider = new FakeSqlProvider();
        var pendingExport = Create(Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));
        pendingExport.ConnectedSystemObject!.Type = new ConnectedSystemObjectType { Id = 99, Name = "Contractor" };

        var results = await ExportAsync(provider, PersonDocument, [pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Success, Is.False);
            Assert.That(results[0].ErrorMessage, Does.Contain("Contractor"));
            Assert.That(provider.ExecutedStatements, Is.Empty);
        }
    }

    [Test]
    public async Task ExportAsync_NoPendingExports_ReturnsAnEmptyListWithoutTouchingTheDatabase()
    {
        var provider = new FakeSqlProvider();

        var results = await ExportAsync(provider, PersonDocument, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.Empty);
            Assert.That(provider.Transactions, Is.Empty);
        }
    }

    [Test]
    public void ExportAsync_WithoutOpeningTheExportConnectionFirst_Throws()
    {
        using var connector = new SqlConnector { ProviderFactory = _ => new FakeSqlProvider() };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connector.ExportAsync([], CancellationToken.None, new RecordingConnectorProgress()));
    }

    [Test]
    public void CloseExportConnection_AfterAnExport_ReleasesTheConnectionAndLeavesPersistedStateAlone()
    {
        var provider = new FakeSqlProvider();
        using var connector = new SqlConnector { ProviderFactory = _ => provider };
        connector.OpenExportConnection(SettingValues(connector, PersonDocument), null);

        var persistedConnectorData = connector.CloseExportConnection();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedConnectorData, Is.Null, "An export carries no state between runs, so it must never overwrite what an import persisted.");
            Assert.That(provider.OpenConnections, Is.All.Matches<FakeDbConnection>(connection => connection.State == System.Data.ConnectionState.Closed),
                "A connection left open outlives the run on the customer's database.");
        }
    }

    [Test]
    public void GetPhases_ForAnExport_DeclaresNothingBecausePerItemCountsSayMore()
    {
        using var connector = new SqlConnector();

        var phases = connector.GetPhases(new ConnectedSystem { Name = "HR Database" },
            new ConnectedSystemRunProfile { Name = "Export", RunType = ConnectedSystemRunType.Export });

        Assert.That(phases, Is.Empty);
    }

    #endregion

    #region Helpers

    private static async Task<List<ConnectedSystemExportResult>> ExportAsync(
        FakeSqlProvider provider,
        string objectTypesDocument,
        IList<PendingExport> pendingExports,
        string databaseTimeZone = SqlConnectorConstants.DefaultDatabaseTimeZone,
        Action<List<ConnectedSystemSettingValue>>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // An export asks the database what its columns are typed as, so every test needs a catalogue to
        // answer from. A test that declared one of its own keeps it; the rest get the shape below.
        if (provider.Catalogue.Tables.Count == 0)
            DeclareTheUsualTables(provider);

        using var connector = new SqlConnector { ProviderFactory = _ => provider };
        var settingValues = SettingValues(connector, objectTypesDocument, databaseTimeZone);
        configureSettings?.Invoke(settingValues);
        connector.OpenExportConnection(settingValues, null);

        try
        {
            return await connector.ExportAsync(pendingExports, CancellationToken.None, new RecordingConnectorProgress());
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    /// <summary>
    /// The tables these tests write to, as a Microsoft SQL Server catalogue would report them. Both
    /// kinds of date and time column are present, and both a numeric and a GUID reference target, so a
    /// test picks the column whose type it is about rather than declaring a catalogue of its own.
    /// </summary>
    private static void DeclareTheUsualTables(FakeSqlProvider provider)
    {
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("COMPANY_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("DISPLAY_NAME", "nvarchar", MaxLength: 200),
            new FakeCatalogueColumn("Grace", "nvarchar", MaxLength: 200),
            new FakeCatalogueColumn("FTE", "decimal", Precision: 5, Scale: 3),
            new FakeCatalogueColumn("STAFF_GUID", "uniqueidentifier"),
            new FakeCatalogueColumn("START_DATE", "datetime2"),
            new FakeCatalogueColumn("LAST_REVIEWED", "datetimeoffset"),
            new FakeCatalogueColumn("MANAGER_EMPLOYEE_ID", "int"),
            new FakeCatalogueColumn("MANAGER_STAFF_GUID", "uniqueidentifier"));

        provider.Catalogue.AddTable("HR", "EMPLOYEE_PHONES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("PHONE_NUMBER", "nvarchar", MaxLength: 30));
    }

    private static List<ConnectedSystemSettingValue> SettingValues(SqlConnector connector, string objectTypesDocument, string databaseTimeZone = SqlConnectorConstants.DefaultDatabaseTimeZone)
    {
        var settingValues = SqlConnectorSettingValues.CreateSqlServer(connector);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, objectTypesDocument);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingDatabaseTimeZone, databaseTimeZone);
        return settingValues;
    }

    /// <summary>
    /// The name of the parameter a column's value was bound to, read back out of the statement the
    /// Connector generated, so a test asserts on the value rather than on the parameter numbering.
    /// </summary>
    private static string ParameterFor(FakeExecutedCommand command, string columnName)
    {
        var marker = $"[{columnName}] = @";
        var index = command.CommandText.IndexOf(marker, StringComparison.Ordinal);

        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"The statement does not write column '{columnName}': {command.CommandText}");

        var name = command.CommandText[(index + marker.Length)..];
        var end = name.IndexOfAny([',', ' ', ')']);
        return end < 0 ? name : name[..end];
    }

    #endregion

    #region Pending Export construction

    private static int _attributeId;

    private static PendingExport Create(params PendingExportAttributeValueChange[] changes) =>
        PendingExportFor(PendingExportChangeType.Create, externalId: null, changes);

    private static PendingExport Update(ConnectedSystemObjectAttributeValue anchor, params PendingExportAttributeValueChange[] changes) =>
        PendingExportFor(PendingExportChangeType.Update, anchor, changes);

    private static PendingExport Delete(ConnectedSystemObjectAttributeValue anchor) =>
        PendingExportFor(PendingExportChangeType.Delete, anchor, []);

    /// <summary>
    /// A Pending Export as JIM hands one to a Connector: the Connected System Object it applies to (a
    /// provisioning placeholder for a create), and the attribute changes to write.
    /// </summary>
    private static PendingExport PendingExportFor(
        PendingExportChangeType changeType,
        ConnectedSystemObjectAttributeValue? externalId,
        IReadOnlyList<PendingExportAttributeValueChange> changes)
    {
        var connectedSystemObject = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = new ConnectedSystemObjectType { Id = 1, Name = "Person" },
            TypeId = 1
        };

        if (externalId != null)
        {
            connectedSystemObject.ExternalIdAttributeId = externalId.AttributeId;
            connectedSystemObject.AttributeValues.Add(externalId);
        }

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = changeType,
            ConnectedSystemObject = connectedSystemObject,
            ConnectedSystemObjectId = connectedSystemObject.Id,
            AttributeValueChanges = [.. changes]
        };
    }

    /// <summary>
    /// The Connected System Object's external ID value, which is what an update and a delete are keyed on.
    /// </summary>
    private static ConnectedSystemObjectAttributeValue Anchor(string name, AttributeDataType type, string? text = null, int? number = null, Guid? guid = null)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = ++_attributeId, Name = name, Type = type, IsExternalId = true };

        return new ConnectedSystemObjectAttributeValue
        {
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = text,
            IntValue = number,
            GuidValue = guid
        };
    }

    private static PendingExportAttributeValueChange Change(
        string name,
        AttributeDataType type,
        string? text = null,
        int? number = null,
        decimal? dec = null,
        Guid? guid = null,
        DateTime? dateTime = null,
        AttributePlurality plurality = AttributePlurality.SingleValued,
        PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Update)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = ++_attributeId,
            Name = name,
            Type = type,
            AttributePlurality = plurality
        };

        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = attribute,
            AttributeId = attribute.Id,
            ChangeType = changeType,
            StringValue = text,
            IntValue = number,
            DecimalValue = dec,
            GuidValue = guid,
            DateTimeValue = dateTime
        };
    }

    #endregion
}
