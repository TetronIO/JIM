// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Connectors.Sql;
using JIM.Models.Staging;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Pages.Admin.Components.Sql;
using JIM.Web.Services;
using MudBlazor;
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
    public void RemovingABlankObjectType_RemovesItWithoutAsking()
    {
        const string withABlankObjectType = """
            { "objectTypes": [ { "name": "Person", "table": "EMPLOYEES", "anchorColumns": ["EMPLOYEE_ID"] }, { "name": "Group", "table": "GROUPS", "anchorColumns": ["GROUP_ID"] }, {} ] }
            """;
        var cut = Render<SqlObjectTypesEditor>(p => p.Add(c => c.SettingValue, ObjectTypesSetting(withABlankObjectType)));
        cut.FindAll("[data-testid=jim-sql-object-type-card-header]")[2].Click();

        cut.Find("[data-testid=jim-sql-object-type-remove]").Click();

        Assert.That(cut.FindComponents<SqlObjectTypeCard>(), Has.Count.EqualTo(2), "a card with nothing on it loses nothing, so there is nothing to confirm");
    }

    [Test]
    public void EveryColumnOnTheCard_SaysNothingAboutOtherColumns()
    {
        var catalogue = new FakeCatalogueSource();
        catalogue.Tables["HR.EMPLOYEES"] = new ConnectorCatalogueObjectDetail
        {
            SchemaName = "HR",
            Name = "EMPLOYEES",
            Kind = ConnectorCatalogueObjectKind.Table,
            Columns = [Column("EMPLOYEE_ID")],
            PrimaryKey = ["EMPLOYEE_ID"]
        };

        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonOnly))
            .Add(c => c.Catalogue, catalogue));

        Assert.That(cut.FindAll("[data-testid=jim-sql-other-columns]"), Is.Empty, "there are no other columns to tell the administrator about");
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

    #region Add Object Type dialog

    [Test]
    public void AddObjectType_TheFirstOfAConnectedSystem_WritesTheDocumentAHandWrittenOneWouldBe()
    {
        // PRD Scenario 1: choose HR.EMPLOYEES, accept the primary key JIM preselects, finish.
        var catalogue = new FakeCatalogueSource();
        catalogue.Tables["HR.EMPLOYEES"] = Employees();
        var settingValue = ObjectTypesSetting("");
        var provider = Render<MudDialogProvider>();
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, settingValue)
            .Add(c => c.Catalogue, catalogue));

        cut.Find("[data-testid=jim-sql-object-types-add]").Click();
        provider.WaitForElement("[data-testid=jim-sql-add-next]");
        SetText(provider, "jim-sql-add-name", "Person");
        Choose(provider, "jim-sql-add-schema", "HR");
        Choose(provider, "jim-sql-add-table", "EMPLOYEES");
        provider.Find("[data-testid=jim-sql-add-next]").Click();

        provider.WaitForAssertion(() => Assert.That(provider.Find("[data-testid=jim-sql-add-anchor-reason]").TextContent, Does.Contain("primary key")));
        provider.Find("[data-testid=jim-sql-add-finish]").Click();

        var handWritten = SqlObjectTypesDocument.FromJson(
            """{"objectTypes":[{"name":"Person","schema":"HR","table":"EMPLOYEES","anchorColumns":["EMPLOYEE_ID"]}]}""").ToJson();
        cut.WaitForAssertion(() => Assert.That(settingValue.StringValue, Is.EqualTo(handWritten)));
        Assert.That(cut.FindComponents<SqlObjectTypeCard>().Single().Instance.Expanded, Is.True, "the new Object Type opens, ready for its Delta Import fields");
    }

    [Test]
    public void AddObjectType_OpenedWhileTheCatalogueIsStillLoading_ReadsTheTableOnceItHasLoaded()
    {
        var catalogue = new FakeCatalogueSource { ObjectsGate = new TaskCompletionSource() };
        catalogue.Tables["HR.EMPLOYEES"] = Employees();
        var provider = Render<MudDialogProvider>();
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(""))
            .Add(c => c.Catalogue, catalogue));
        cut.WaitForElement("[data-testid=jim-sql-catalogue-loading]");

        cut.Find("[data-testid=jim-sql-object-types-add]").Click();
        provider.WaitForElement("[data-testid=jim-sql-add-next]");
        cut.InvokeAsync(() => catalogue.ObjectsGate.SetResult());
        SetText(provider, "jim-sql-add-name", "Person");
        Choose(provider, "jim-sql-add-schema", "HR");
        Choose(provider, "jim-sql-add-table", "EMPLOYEES");
        provider.Find("[data-testid=jim-sql-add-next]").Click();

        provider.WaitForAssertion(() => Assert.That(provider.Find("[data-testid=jim-sql-add-anchor-reason]").TextContent, Does.Contain("primary key"),
            "a dialog opened a moment too soon must not leave the administrator to type every column name"));
    }

    [Test]
    public void AddObjectType_Added_IsTheOnlyCardLeftOpen()
    {
        var catalogue = new FakeCatalogueSource();
        catalogue.Tables["HR.DEPARTMENTS"] = new ConnectorCatalogueObjectDetail
        {
            SchemaName = "HR",
            Name = "DEPARTMENTS",
            Kind = ConnectorCatalogueObjectKind.Table,
            Columns = [Column("DEPARTMENT_ID")],
            PrimaryKey = ["DEPARTMENT_ID"]
        };
        var provider = Render<MudDialogProvider>();
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonOnly))
            .Add(c => c.Catalogue, catalogue));
        Assert.That(cut.FindComponents<SqlObjectTypeCard>().Single().Instance.Expanded, Is.True, "a lone Object Type opens by itself");

        cut.Find("[data-testid=jim-sql-object-types-add]").Click();
        provider.WaitForElement("[data-testid=jim-sql-add-next]");
        SetText(provider, "jim-sql-add-name", "Department");
        Choose(provider, "jim-sql-add-schema", "HR");
        Choose(provider, "jim-sql-add-table", "DEPARTMENTS");
        provider.Find("[data-testid=jim-sql-add-next]").Click();
        provider.WaitForElement("[data-testid=jim-sql-add-anchor-reason]");
        provider.Find("[data-testid=jim-sql-add-finish]").Click();

        cut.WaitForAssertion(() => Assert.That(
            cut.FindComponents<SqlObjectTypeCard>().Select(c => c.Instance.Expanded),
            Is.EqualTo(new[] { false, true }),
            "the new card is where the administrator's eye should land, not a scroll past the one already open"));
    }

    [Test]
    public void AddObjectType_Cancelled_AddsNothing()
    {
        var settingValue = ObjectTypesSetting(PersonOnly);
        var provider = Render<MudDialogProvider>();
        var cut = Render<SqlObjectTypesEditor>(p => p.Add(c => c.SettingValue, settingValue));

        cut.Find("[data-testid=jim-sql-object-types-add]").Click();
        provider.WaitForElement("[data-testid=jim-sql-add-cancel]").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<SqlObjectTypeCard>(), Has.Count.EqualTo(1));
            Assert.That(settingValue.StringValue, Is.EqualTo(PersonOnly));
        }
    }

    [Test]
    public void AForeignKeyToATableNoObjectTypeReads_OpensAddObjectTypeWithThatTableChosen()
    {
        var catalogue = new FakeCatalogueSource();
        var employees = Employees();
        employees.ForeignKeys =
        [
            new ConnectorCatalogueForeignKey
            {
                ConstraintName = "FK_EMP_DEPT",
                Columns = ["DEPARTMENT_ID"],
                ReferencedSchemaName = "HR",
                ReferencedObjectName = "DEPARTMENTS",
                ReferencedColumns = ["DEPARTMENT_ID"]
            }
        ];
        catalogue.Tables["HR.EMPLOYEES"] = employees;
        var provider = Render<MudDialogProvider>();
        var cut = Render<SqlObjectTypesEditor>(p => p
            .Add(c => c.SettingValue, ObjectTypesSetting(PersonOnly))
            .Add(c => c.Catalogue, catalogue));

        cut.WaitForElement("[data-testid=jim-sql-add-object-type-suggestion-accept]").Click();

        provider.WaitForElement("[data-testid=jim-sql-add-next]");
        var table = provider.FindComponents<SqlCatalogueNameField>().Single(f => f.Instance.TestId == "jim-sql-add-table");
        var schema = provider.FindComponents<SqlCatalogueNameField>().Single(f => f.Instance.TestId == "jim-sql-add-schema");
        Assert.That((schema.Instance.Value, table.Instance.Value), Is.EqualTo(("HR", "DEPARTMENTS")));
    }

    #endregion

    private static ConnectorCatalogueObjectDetail Employees() => new()
    {
        SchemaName = "HR",
        Name = "EMPLOYEES",
        Kind = ConnectorCatalogueObjectKind.Table,
        Columns = [Column("EMPLOYEE_ID"), Column("GIVEN_NAME"), Column("DEPARTMENT_ID")],
        PrimaryKey = ["EMPLOYEE_ID"]
    };

    /// <summary>Types into a text field the way a blur commits it.</summary>
    private static void SetText(IRenderedComponent<MudDialogProvider> provider, string testId, string text)
    {
        var field = provider.FindComponents<MudTextField<string>>().Single(f => f.Instance.UserAttributes.TryGetValue("data-testid", out var id) && (string?)id == testId);
        provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(text)).Wait();
    }

    /// <summary>Chooses a name in a catalogue picker, as selecting it from the list does.</summary>
    private static void Choose(IRenderedComponent<MudDialogProvider> provider, string testId, string name)
    {
        var field = provider.FindComponents<SqlCatalogueNameField>().Single(f => f.Instance.TestId == testId);
        provider.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(name)).Wait();
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

        /// <summary>When set, the list of tables is not answered until it completes, as a slow database's is not.</summary>
        public TaskCompletionSource? ObjectsGate { get; init; }

        public async Task<ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>> GetObjectsAsync(CancellationToken cancellationToken = default)
        {
            ObjectReads++;
            if (ObjectsGate != null)
                await ObjectsGate.Task;

            IReadOnlyList<ConnectorCatalogueObject> objects = [.. Tables.Values.Select(t => new ConnectorCatalogueObject { SchemaName = t.SchemaName, Name = t.Name!, Kind = t.Kind })];
            return ObjectsFailure == null
                ? new ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>(objects, null)
                : new ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>(null, ObjectsFailure);
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
