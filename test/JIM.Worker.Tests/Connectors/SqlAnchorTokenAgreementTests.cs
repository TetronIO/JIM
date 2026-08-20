// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the one invariant that ties the JIM SQL Connector's export to its import: the external ID an
/// export composes for a row it created is byte-for-byte the one an import of that same row composes.
/// </summary>
/// <remarks>
/// <para>
/// This Connector confirms its exports automatically, so nothing reads the row back to check. If the two
/// sides render an anchor differently, JIM records a Connected System Object under an external ID no
/// import ever produces: the next import sees an object it has never met, provisioning runs again, and
/// the pair never converges. Nothing raises an error at any point.
/// </para>
/// <para>
/// An Oracle table keyed on <c>RAW(16) DEFAULT SYS_GUID()</c> is where the two genuinely disagreed: the
/// driver hands back bytes on both sides, and only the anchor's attribute type says whether they are a
/// GUID or a digest.
/// </para>
/// </remarks>
[TestFixture]
public class SqlAnchorTokenAgreementTests
{
    private const string GuidAnchorDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "STAFF_GUID" ] }
          ]
        }
        """;

    private const string CompositeGuidAnchorDocument = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "COMPANY_ID", "STAFF_GUID" ] }
          ]
        }
        """;

    private static readonly Guid Identifier = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

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

    [Test]
    public async Task AnOracleRaw16GeneratedKey_ComposesTheSameExternalIdOnExportAndOnImport()
    {
        var raw16 = IdentifierParser.ToRfc4122Bytes(Identifier);

        var exported = await ExportACreateAsync(SqlDatabaseType.Oracle, GuidAnchorDocument, OracleTable(),
            generatedKey: raw16,
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var imported = await ImportTheRowAsync(SqlDatabaseType.Oracle, GuidAnchorDocument, OracleTable(),
            GuidAnchorSystem(),
            ["STAFF_GUID", "DISPLAY_NAME"],
            [raw16, "Ada"]);

        var importedIdentifier = imported.Attributes.Single(attribute => attribute.Name == "STAFF_GUID").GuidValues.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importedIdentifier, Is.EqualTo(Identifier), "The row's key is the identifier the bytes mean in Oracle's own byte order.");
            Assert.That(exported, Is.EqualTo(importedIdentifier.ToString("D")),
                "JIM caches a Connected System Object under the external ID string the export returned, and looks it up by the one the import composes; two forms of the same key means the object is never found again.");
        }
    }

    [Test]
    public async Task AnOracleCompositeAnchorEndingInARaw16_ComposesTheSameExternalIdOnExportAndOnImport()
    {
        var raw16 = IdentifierParser.ToRfc4122Bytes(Identifier);

        var exported = await ExportACreateAsync(SqlDatabaseType.Oracle, CompositeGuidAnchorDocument, OracleTable(),
            generatedKey: null,
            Change("COMPANY_ID", AttributeDataType.Number, number: 7),
            Change("STAFF_GUID", AttributeDataType.Guid, guid: Identifier),
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var imported = await ImportTheRowAsync(SqlDatabaseType.Oracle, CompositeGuidAnchorDocument, OracleTable(),
            CompositeGuidAnchorSystem(),
            ["COMPANY_ID", "STAFF_GUID", "DISPLAY_NAME"],
            [7, raw16, "Ada"]);

        var composed = imported.Attributes.Single(attribute => attribute.Name == "COMPANY_ID+STAFF_GUID").StringValues.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(composed, Is.EqualTo($"7+{Identifier:D}"));
            Assert.That(exported, Is.EqualTo(composed),
                "A composed external ID is stored as text and matched as text, so every part of it has to be rendered the same way on both sides.");
        }
    }

    [Test]
    public async Task ASqlServerUniqueidentifierGeneratedKey_ComposesTheSameExternalIdOnExportAndOnImport()
    {
        // The regression guard for the dialect this Connector already worked against: SqlClient returns
        // a Guid on both sides, and neither side's rendering may move.
        var exported = await ExportACreateAsync(SqlDatabaseType.SqlServer, GuidAnchorDocument, SqlServerTable(),
            generatedKey: Identifier,
            Change("DISPLAY_NAME", AttributeDataType.Text, text: "Ada"));

        var imported = await ImportTheRowAsync(SqlDatabaseType.SqlServer, GuidAnchorDocument, SqlServerTable(),
            GuidAnchorSystem(),
            ["STAFF_GUID", "DISPLAY_NAME"],
            [Identifier, "Ada"]);

        var importedIdentifier = imported.Attributes.Single(attribute => attribute.Name == "STAFF_GUID").GuidValues.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exported, Is.EqualTo("550e8400-e29b-41d4-a716-446655440000"), "The hyphenated form is what this dialect has always composed.");
            Assert.That(exported, Is.EqualTo(importedIdentifier.ToString("D")));
        }
    }

    #region Driving the Connector

    /// <summary>
    /// Applies one create and answers with the external ID it composed for the new row.
    /// </summary>
    /// <param name="generatedKey">The key the database generates, or null where the create supplies its own anchor.</param>
    private static async Task<string?> ExportACreateAsync(
        SqlDatabaseType dialect,
        string objectTypesDocument,
        FakeCatalogueColumn[] columns,
        object? generatedKey,
        params PendingExportAttributeValueChange[] changes)
    {
        var provider = new FakeSqlProvider
        {
            DialectUnderTest = dialect,

            // Oracle's mechanism, so that a RAW(16) key comes back the way its driver returns one.
            GeneratedKeyRetrievalMode = dialect == SqlDatabaseType.Oracle ? SqlGeneratedKeyRetrieval.OutputParameter : SqlGeneratedKeyRetrieval.ResultSet,
            GeneratedKey = generatedKey
        };

        provider.Catalogue.AddTable("HR", "EMPLOYEES", columns);

        using var connector = new SqlConnector { ProviderFactory = _ => provider };
        connector.OpenExportConnection(SettingValues(connector, objectTypesDocument), null);

        try
        {
            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportChangeType.Create,
                ConnectedSystemObject = new ConnectedSystemObject
                {
                    Id = Guid.NewGuid(),
                    Type = new ConnectedSystemObjectType { Id = 1, Name = "Person" },
                    TypeId = 1
                },
                AttributeValueChanges = [.. changes]
            };

            var results = await connector.ExportAsync([pendingExport], CancellationToken.None, new RecordingConnectorProgress());

            Assert.That(results[0].Success, Is.True, results[0].ErrorMessage);
            return results[0].ExternalId;
        }
        finally
        {
            connector.CloseExportConnection();
        }
    }

    /// <summary>
    /// Imports the one row a table holds and answers with the Connected System Import Object for it.
    /// </summary>
    private async Task<ConnectedSystemImportObject> ImportTheRowAsync(
        SqlDatabaseType dialect,
        string objectTypesDocument,
        FakeCatalogueColumn[] columns,
        ConnectedSystem connectedSystem,
        string[] rowColumns,
        object?[] row)
    {
        var provider = new FakeSqlProvider { DialectUnderTest = dialect };
        provider.Catalogue.AddTable("HR", "EMPLOYEES", columns);
        provider.Catalogue.AddRows("HR", "EMPLOYEES", rowColumns, row);

        using var connector = new SqlConnector { ProviderFactory = _ => provider };
        connector.OpenImportConnection(SettingValues(connector, objectTypesDocument), null, _logger);

        var runProfile = new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = 10 };
        var result = await connector.ImportAsync(connectedSystem, runProfile, [], null, _logger, CancellationToken.None, new RecordingConnectorProgress());

        return result.ImportObjects.Single();
    }

    private static List<ConnectedSystemSettingValue> SettingValues(SqlConnector connector, string objectTypesDocument)
    {
        var settingValues = SqlConnectorSettingValues.CreateSqlServer(connector);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, objectTypesDocument);

        // RAW(16) is as commonly a digest as a GUID, so reading one as a GUID is an administrator's
        // opt-in that schema discovery, import and export all have to honour identically.
        SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, true);
        return settingValues;
    }

    #endregion

    #region Schema

    private static FakeCatalogueColumn[] OracleTable() =>
    [
        new FakeCatalogueColumn("COMPANY_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
        new FakeCatalogueColumn("STAFF_GUID", "RAW", MaxLength: 16, IsNullable: false),
        new FakeCatalogueColumn("DISPLAY_NAME", "NVARCHAR2", MaxLength: 200)
    ];

    private static FakeCatalogueColumn[] SqlServerTable() =>
    [
        new FakeCatalogueColumn("COMPANY_ID", "int", IsNullable: false),
        new FakeCatalogueColumn("STAFF_GUID", "uniqueidentifier", IsNullable: false),
        new FakeCatalogueColumn("DISPLAY_NAME", "nvarchar", MaxLength: 200)
    ];

    private static ConnectedSystem GuidAnchorSystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType(
                Attribute("STAFF_GUID", AttributeDataType.Guid, isExternalId: true),
                Attribute("DISPLAY_NAME", AttributeDataType.Text))
        ]
    };

    private static ConnectedSystem CompositeGuidAnchorSystem() => new()
    {
        Name = "HR Database",
        ObjectTypes =
        [
            ObjectType(
                Attribute("COMPANY_ID", AttributeDataType.Number),
                Attribute("STAFF_GUID", AttributeDataType.Guid),
                Attribute("DISPLAY_NAME", AttributeDataType.Text),
                Attribute("COMPANY_ID+STAFF_GUID", AttributeDataType.Text, isExternalId: true))
        ]
    };

    private static ConnectedSystemObjectType ObjectType(params ConnectedSystemObjectTypeAttribute[] attributes) =>
        new() { Name = "Person", Selected = true, Attributes = [.. attributes] };

    private static ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, bool isExternalId = false) =>
        new() { Name = name, Type = type, Selected = true, IsExternalId = isExternalId };

    private static int _attributeId;

    private static PendingExportAttributeValueChange Change(
        string name,
        AttributeDataType type,
        string? text = null,
        int? number = null,
        Guid? guid = null)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = ++_attributeId, Name = name, Type = type };

        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = attribute,
            AttributeId = attribute.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = text,
            IntValue = number,
            GuidValue = guid
        };
    }

    #endregion
}
