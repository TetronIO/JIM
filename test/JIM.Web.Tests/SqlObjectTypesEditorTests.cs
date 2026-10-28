// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Connectors.Sql;
using JIM.Models.Staging;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Pages.Admin.Components.Sql;
using JIM.Web.Services;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the SQL Connector's Object Types editor as rendered (#2570): an edit made on a card reaches the setting
/// as a document the Connector accepts, a database JIM cannot read leaves the configuration editable and says why,
/// the Delta Import fields follow the Delta Import Mode on screen, and a document the cards cannot show opens as
/// text rather than being simplified. The rules behind each card are covered by
/// <see cref="SqlObjectTypesEditorModelTests"/>.
/// </summary>
[TestFixture]
public class SqlObjectTypesEditorTests : JimComponentTestContext
{
    private const string PersonAndGroup = """
        {
          "objectTypes": [
            { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": ["EMPLOYEE_ID"] },
            { "name": "Group", "schema": "HR", "table": "GROUPS", "anchorColumns": ["GROUP_ID"] }
          ]
        }
        """;

    private const string PersonOnly = """
        { "objectTypes": [ { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": ["EMPLOYEE_ID"] } ] }
        """;

    [Test]
    public void AcceptingAForeignKeySuggestion_WritesADocumentTheConnectorAcceptsToTheSetting()
    {
        var catalogue = new FakeCatalogueSource();
        catalogue.Tables["HR.GROUPS"] = new ConnectorCatalogueObjectDetail
        {
            SchemaName = "HR",
            Name = "GROUPS",
            Kind = ConnectorCatalogueObjectKind.Table,
            Columns = [Column("GROUP_ID"), Column("OWNER_EMPLOYEE_ID")],
            PrimaryKey = ["GROUP_ID"],
            ForeignKeys =
            [
                new ConnectorCatalogueForeignKey
                {
                    ConstraintName = "FK_GROUP_OWNER",
                    Columns = ["OWNER_EMPLOYEE_ID"],
                    ReferencedSchemaName = "HR",
                    ReferencedObjectName = "EMPLOYEES",
                    ReferencedColumns = ["EMPLOYEE_ID"]
                }
            ]
        };
        var settingValue = ObjectTypesSetting(PersonAndGroup);
        var changes = 0;
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, settingValue)
            .Add(c => c.Catalogue, catalogue)
            .Add(c => c.ValueChanged, () => changes++));

        cut.FindAll("[data-testid=jim-sql-object-type-card-header]")[1].Click();
        cut.WaitForElement("[data-testid=jim-sql-reference-suggestion-accept]").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SqlObjectTypesDocument.Validate(settingValue.StringValue), Is.Empty);
            var group = SqlObjectTypesDocument.FromJson(settingValue.StringValue!).ObjectTypes![1];
            Assert.That(group.Columns!.Single().Name, Is.EqualTo("OWNER_EMPLOYEE_ID"));
            Assert.That(group.Columns!.Single().ReferencesObjectType, Is.EqualTo("Person"));
            Assert.That(changes, Is.EqualTo(1), "the settings tab is told, so its save gate follows the change");
        }
    }

    [Test]
    public void OpeningTheEditor_LeavesTheStoredDocumentAsItWasWritten()
    {
        var settingValue = ObjectTypesSetting(PersonAndGroup);

        Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, settingValue)
            .Add(c => c.Catalogue, new FakeCatalogueSource()));

        Assert.That(settingValue.StringValue, Is.EqualTo(PersonAndGroup), "a document is only rewritten when the administrator changes it");
    }

    [Test]
    public void CatalogueUnreadable_SaysWhyAndKeepsEveryObjectTypeEditable()
    {
        var catalogue = new FakeCatalogueSource { ObjectsFailure = "Login failed for user 'jim'." };

        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonAndGroup))
            .Add(c => c.Catalogue, catalogue));

        var banner = cut.WaitForElement("[data-testid=jim-sql-catalogue-unavailable]");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(banner.TextContent, Does.Contain("Login failed for user 'jim'."));
            Assert.That(cut.FindComponents<SqlObjectTypeCard>(), Has.Count.EqualTo(2), "the configuration is still there to view and change");
        }

        cut.Find("[data-testid=jim-sql-catalogue-retry]").Click();

        Assert.That(catalogue.ObjectReads, Is.EqualTo(2), "Retry reads the catalogue again");
    }

    [Test]
    public void CatalogueUnreadable_ExpandingACard_DoesNotTryToReadItsTableAgain()
    {
        var catalogue = new FakeCatalogueSource { ObjectsFailure = "Login failed for user 'jim'." };
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonAndGroup))
            .Add(c => c.Catalogue, catalogue));
        cut.WaitForElement("[data-testid=jim-sql-catalogue-unavailable]");

        cut.FindAll("[data-testid=jim-sql-object-type-card-header]")[0].Click();

        Assert.That(catalogue.TableReads, Is.Zero, "each read would wait out the same connection timeout to report the same failure");
    }

    [TestCase(SqlConnectorConstants.DeltaImportModeWatermarkColumn, true, false)]
    [TestCase(SqlConnectorConstants.DeltaImportModeChangeLogTable, false, true)]
    [TestCase(null, false, false)]
    public void DeltaImportFields_FollowTheDeltaImportModeOnScreen(string? mode, bool expectWatermark, bool expectChangeLog)
    {
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonOnly))
            .Add(c => c.SettingValues, [DeltaImportModeSetting(mode)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<SqlCatalogueNameField>().Any(f => f.Instance.TestId == "jim-sql-watermark-column"), Is.EqualTo(expectWatermark));
            Assert.That(cut.FindAll("[data-testid=jim-sql-change-log]"), Has.Count.EqualTo(expectChangeLog ? 1 : 0));
        }
    }

    [Test]
    public void DeltaImportMode_ChangedOnScreen_SwapsTheFieldsWithoutSaving()
    {
        var mode = DeltaImportModeSetting(SqlConnectorConstants.DeltaImportModeWatermarkColumn);
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonOnly))
            .Add(c => c.SettingValues, [mode]));

        mode.StringValue = SqlConnectorConstants.DeltaImportModeChangeLogTable;
        cut.Render();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<SqlCatalogueNameField>().Any(f => f.Instance.TestId == "jim-sql-watermark-column"), Is.False);
            Assert.That(cut.FindAll("[data-testid=jim-sql-change-log]"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void ADocumentTheCardsCannotShow_OpensAsTextSayingWhy()
    {
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting("{ \"objectTypes\": [ { \"anchorColumn\": [\"ID\"] } ] }")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid=jim-sql-object-types-unloadable]").TextContent, Does.Contain("anchorColumns"));
            Assert.That(cut.HasComponent<ConnectedSystemSettingField>(), Is.True, "the text stays editable, so the problem can be fixed");
            Assert.That(cut.HasComponent<SqlObjectTypeCard>(), Is.False);
        }
    }

    private static ConnectedSystemSettingValue ObjectTypesSetting(string json) => new()
    {
        Setting = new ConnectorDefinitionSetting
        {
            Id = 10,
            Name = SqlConnectorConstants.SettingObjectTypes,
            Type = ConnectedSystemSettingType.Text,
            Category = ConnectedSystemSettingCategory.Schema,
            Required = true
        },
        StringValue = json
    };

    private static ConnectedSystemSettingValue DeltaImportModeSetting(string? mode) => new()
    {
        Setting = new ConnectorDefinitionSetting
        {
            Id = 11,
            Name = SqlConnectorConstants.SettingDeltaImportMode,
            Type = ConnectedSystemSettingType.DropDown,
            Category = ConnectedSystemSettingCategory.Schema
        },
        StringValue = mode
    };

    private static ConnectorCatalogueColumn Column(string name) => new() { Name = name, DataTypeName = "int" };

    /// <summary>A catalogue answered from memory, counting the reads made of it.</summary>
    private sealed class FakeCatalogueSource : IConnectedSystemCatalogueSource
    {
        public string? ObjectsFailure { get; init; }

        public Dictionary<string, ConnectorCatalogueObjectDetail> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ObjectReads { get; private set; }

        public Task<ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>> GetObjectsAsync(CancellationToken cancellationToken = default)
        {
            ObjectReads++;
            IReadOnlyList<ConnectorCatalogueObject> objects = [.. Tables.Values.Select(t => new ConnectorCatalogueObject { SchemaName = t.SchemaName, Name = t.Name!, Kind = t.Kind })];
            return Task.FromResult(ObjectsFailure == null
                ? new ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>(objects, null)
                : new ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>(null, ObjectsFailure));
        }

        public int TableReads { get; private set; }

        public Task<ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>> GetObjectAsync(string? schemaName, string objectName, CancellationToken cancellationToken = default)
        {
            TableReads++;
            return Task.FromResult(new ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>(Tables.GetValueOrDefault($"{schemaName}.{objectName}"), null));
        }

        public Task<ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>> DescribeStatementAsync(string statement, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>(null, "Not described in this test."));
    }
}
