// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the SQL Connector Object Types editor's model (#2570): what it reads and writes, what a collapsed card
/// summarises, which problems it attaches to which card, which foreign keys it suggests as references, and what
/// renaming or removing an Object Type does to the references other Object Types hold to it. The document it
/// writes is checked with the Connector's own parser, so the editor can never produce something saving refuses
/// for a reason the editor did not show.
/// </summary>
[TestFixture]
public class SqlObjectTypesEditorModelTests
{
    private const string PersonAndGroup = """
        {
          "objectTypes": [
            {
              "name": "Person",
              "schema": "HR",
              "table": "EMPLOYEES",
              "anchorColumns": ["EMPLOYEE_ID"],
              "columns": [ { "name": "MANAGER_ID", "referencesObjectType": "Person" } ]
            },
            {
              "name": "Group",
              "schema": "HR",
              "table": "GROUPS",
              "anchorColumns": ["GROUP_ID"],
              "relatedTables": [
                {
                  "attributeName": "Members",
                  "schema": "HR",
                  "table": "GROUP_MEMBERS",
                  "valueColumn": "EMPLOYEE_ID",
                  "joinColumns": ["GROUP_ID"],
                  "referencesObjectType": "Person"
                }
              ]
            }
          ]
        }
        """;

    #region Loading and writing

    [Test]
    public void TryLoad_AValidDocument_WritesItBackAsADocumentTheParserAccepts()
    {
        Assert.That(SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _), Is.True);

        var written = model!.ToJson();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SqlObjectTypesDocument.Validate(written), Is.Empty);
            Assert.That(SqlObjectTypesDocument.FromJson(written).ObjectTypes!.Select(o => o.Name), Is.EqualTo(new[] { "Person", "Group" }));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void TryLoad_NoDocumentYet_StartsWithNoObjectTypes(string? json)
    {
        Assert.That(SqlObjectTypesEditorModel.TryLoad(json, out var model, out _), Is.True);

        Assert.That(model!.ObjectTypes, Is.Empty);
    }

    [Test]
    public void TryLoad_TextThatIsNotADocument_RefusesAndSaysWhy()
    {
        var loaded = SqlObjectTypesEditorModel.TryLoad("{ \"objectTypes\": [ { \"anchorColumn\": [\"ID\"] } ] }", out var model, out var failure);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.False, "an unrecognised field cannot be represented, so the editor must not load and then drop it");
            Assert.That(model, Is.Null);
            Assert.That(failure, Does.Contain("anchorColumns"), "the parser's own nearest-field suggestion is passed on");
        }
    }

    [Test]
    public void TryLoad_ADocumentWithProblems_StillLoadsSoTheyCanBeFixedInTheEditor()
    {
        var loaded = SqlObjectTypesEditorModel.TryLoad("{ \"objectTypes\": [ { \"name\": \"Person\", \"table\": \"EMPLOYEES\" } ] }", out var model, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded, Is.True);
            Assert.That(model!.ProblemsFor(0), Is.Not.Empty, "an Object Type with no anchor is a problem on its card, not a reason to refuse the editor");
        }
    }

    #endregion

    #region Summaries

    [Test]
    public void SummaryOf_AnObjectTypeWithAnAnchorAndAReference_NamesBoth()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        var chips = SqlObjectTypesEditorModel.SummaryOf(model!.ObjectTypes[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips.Single(c => c.Kind == SqlObjectTypeSummaryChipKind.Anchor).Text, Does.Contain("EMPLOYEE_ID"));
            var reference = chips.Single(c => c.Kind == SqlObjectTypeSummaryChipKind.Reference).Text;
            Assert.That(reference, Does.Contain("MANAGER_ID").And.Contain("Person"));
        }
    }

    [Test]
    public void SummaryOf_ARelatedTableReferencingAnObjectType_NamesTheAttributeAndTheObjectType()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        var chip = SqlObjectTypesEditorModel.SummaryOf(model!.ObjectTypes[1]).Single(c => c.Kind == SqlObjectTypeSummaryChipKind.MultiValued);

        Assert.That(chip.Text, Does.Contain("Members").And.Contain("Person"));
    }

    [Test]
    public void SummaryOf_AColumnConfiguredWithoutAReference_IsNotSummarisedAsOne()
    {
        var objectType = new SqlObjectTypeDocument { Name = "Person", Table = "EMPLOYEES", AnchorColumns = ["ID"], Columns = [new SqlColumnDocument { Name = "NOTES" }] };

        Assert.That(SqlObjectTypesEditorModel.SummaryOf(objectType).Any(c => c.Kind == SqlObjectTypeSummaryChipKind.Reference), Is.False);
    }

    #endregion

    #region Problems

    [Test]
    public void ProblemsFor_AnObjectTypeWithAProblem_IsAttachedToThatObjectTypeOnly()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.ObjectTypes[1].AnchorColumns = [];
        model.Revalidate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ProblemsFor(0), Is.Empty);
            Assert.That(model.ProblemsFor(1), Is.Not.Empty);
        }
    }

    [Test]
    public void ProblemsFor_WatermarkModeAndASelectedObjectTypeWithNoWatermark_IsAProblemOnThatCard()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.DeltaImportMode = SqlConnectorConstants.DeltaImportModeWatermarkColumn;
        model.SelectedObjectTypeNames = new HashSet<string>(["person"], StringComparer.OrdinalIgnoreCase);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ProblemsFor(0), Has.Some.Contains(SqlConnectorConstants.DeltaImportModeWatermarkColumn),
                "saving the Schema tab refuses a selected Object Type with no watermark column, so the card says so first");
            Assert.That(model.ProblemsFor(1), Is.Empty, "Group is not selected for synchronisation, so a Delta Import never reads it");
        }
    }

    [Test]
    public void ProblemsFor_WatermarkModeAndARelatedTableWithNoWatermark_IsAProblemOnItsObjectType()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.ObjectTypes[1].WatermarkColumn = "LAST_MODIFIED";
        model.DeltaImportMode = SqlConnectorConstants.DeltaImportModeWatermarkColumn;
        model.SelectedObjectTypeNames = new HashSet<string>(["Group"], StringComparer.OrdinalIgnoreCase);

        Assert.That(model.ProblemsFor(1), Has.Some.Contains("Members"));
    }

    [Test]
    public void ProblemsFor_ChangeLogModeAndASelectedObjectTypeWithNoChangeLog_IsAProblemOnThatCard()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.DeltaImportMode = SqlConnectorConstants.DeltaImportModeChangeLogTable;
        model.SelectedObjectTypeNames = new HashSet<string>(["Person"], StringComparer.OrdinalIgnoreCase);

        Assert.That(model.ProblemsFor(0), Has.Some.Contains(SqlConnectorConstants.DeltaImportModeChangeLogTable));
    }

    [Test]
    public void ProblemsFor_NoDeltaImportMode_AsksForNothingExtra()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.SelectedObjectTypeNames = new HashSet<string>(["Person", "Group"], StringComparer.OrdinalIgnoreCase);

        Assert.That(model.ProblemsFor(0).Concat(model.ProblemsFor(1)), Is.Empty);
    }

    #endregion

    #region Suggestions

    [Test]
    public void SuggestionsFor_AForeignKeyToAnotherObjectTypesAnchor_SuggestsTheReference()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.ObjectTypes[1].Columns = null;
        var groups = Detail("HR", "GROUPS", ["GROUP_ID"], ForeignKey("FK_GROUP_OWNER", "OWNER_EMPLOYEE_ID", "HR", "EMPLOYEES", "EMPLOYEE_ID"));

        var suggestion = model.SuggestionsFor(1, groups).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggestion.Kind, Is.EqualTo(SqlObjectTypeSuggestionKind.Reference));
            Assert.That(suggestion.ColumnName, Is.EqualTo("OWNER_EMPLOYEE_ID"));
            Assert.That(suggestion.ReferencedObjectTypeName, Is.EqualTo("Person"));
        }
    }

    [Test]
    public void SuggestionsFor_AnObjectTypeWithNoSchema_MatchesItsTableByNameAlone()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        model!.ObjectTypes[0].Schema = null;
        var groups = Detail("HR", "GROUPS", ["GROUP_ID"], ForeignKey("FK_GROUP_OWNER", "OWNER_EMPLOYEE_ID", "HR", "employees", "employee_id"));

        Assert.That(model.SuggestionsFor(1, groups).Single().ReferencedObjectTypeName, Is.EqualTo("Person"));
    }

    [Test]
    public void SuggestionsFor_AColumnAlreadyAReference_SuggestsNothing()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var employees = Detail("HR", "EMPLOYEES", ["EMPLOYEE_ID"], ForeignKey("FK_EMP_MANAGER", "MANAGER_ID", "HR", "EMPLOYEES", "EMPLOYEE_ID"));

        Assert.That(model!.SuggestionsFor(0, employees), Is.Empty);
    }

    [Test]
    public void SuggestionsFor_AForeignKeyToATableNoObjectTypeReadsFrom_OffersToAddOne()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var employees = Detail("HR", "EMPLOYEES", ["EMPLOYEE_ID"],
            ForeignKey("FK_EMP_DEPT", "DEPARTMENT_ID", "HR", "DEPARTMENTS", "DEPARTMENT_ID"),
            ForeignKey("FK_EMP_DEPT_AGAIN", "COST_CENTRE_DEPARTMENT_ID", "HR", "DEPARTMENTS", "DEPARTMENT_ID"));

        var suggestion = model!.SuggestionsFor(0, employees).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggestion.Kind, Is.EqualTo(SqlObjectTypeSuggestionKind.AddObjectType));
            Assert.That(suggestion.ReferencedSchemaName, Is.EqualTo("HR"));
            Assert.That(suggestion.ReferencedObjectName, Is.EqualTo("DEPARTMENTS"), "one offer per table, however many keys point at it");
        }
    }

    [Test]
    public void SuggestionsFor_AForeignKeyToATableWhoseObjectTypeHasAnotherAnchor_SuggestsNothing()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var groups = Detail("HR", "GROUPS", ["GROUP_ID"], ForeignKey("FK_GROUP_OWNER_BADGE", "OWNER_BADGE", "HR", "EMPLOYEES", "BADGE_NUMBER"));

        Assert.That(model!.SuggestionsFor(1, groups), Is.Empty, "the values would not hold Person's anchor, so they could never resolve");
    }

    [Test]
    public void SuggestionsFor_ACompositeForeignKey_SuggestsNothing()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var groups = Detail("HR", "GROUPS", ["GROUP_ID"], new ConnectorCatalogueForeignKey
        {
            ConstraintName = "FK_COMPOSITE",
            Columns = ["A", "B"],
            ReferencedSchemaName = "HR",
            ReferencedObjectName = "ASSIGNMENTS",
            ReferencedColumns = ["A", "B"]
        });

        Assert.That(model!.SuggestionsFor(1, groups), Is.Empty, "a reference column holds one value, so a key of two columns cannot be one");
    }

    [Test]
    public void AcceptReference_AddsTheColumnTheParserReadsAsAReference()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var groups = Detail("HR", "GROUPS", ["GROUP_ID"], ForeignKey("FK_GROUP_OWNER", "OWNER_EMPLOYEE_ID", "HR", "EMPLOYEES", "EMPLOYEE_ID"));
        var suggestion = model!.SuggestionsFor(1, groups).Single();

        model.AcceptReference(1, suggestion);

        var group = Accepted(model.ToJson()).ObjectTypes![1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.Columns!.Single().Name, Is.EqualTo("OWNER_EMPLOYEE_ID"));
            Assert.That(group.Columns!.Single().ReferencesObjectType, Is.EqualTo("Person"));
            Assert.That(model.SuggestionsFor(1, groups), Is.Empty, "an accepted suggestion is not offered again");
        }
    }

    [Test]
    public void Dismiss_ASuggestion_IsNotOfferedAgain()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var groups = Detail("HR", "GROUPS", ["GROUP_ID"], ForeignKey("FK_GROUP_OWNER", "OWNER_EMPLOYEE_ID", "HR", "EMPLOYEES", "EMPLOYEE_ID"));

        model!.Dismiss(1, model.SuggestionsFor(1, groups).Single());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.SuggestionsFor(1, groups), Is.Empty);
            Assert.That(model.ObjectTypes[1].Columns, Is.Null.Or.Empty, "dismissing changes nothing in the document");
        }
    }

    [Test]
    public void PrimaryKeyAnchor_AnObjectTypeWithNoAnchorOnATableWithAPrimaryKey_IsTheKeyInKeyOrder()
    {
        var objectType = new SqlObjectTypeDocument { Name = "Assignment", Schema = "HR", Table = "ASSIGNMENTS" };

        var anchor = SqlObjectTypesEditorModel.PrimaryKeyAnchor(objectType, Detail("HR", "ASSIGNMENTS", ["ROLE_CODE", "EMPLOYEE_ID"]));

        Assert.That(anchor, Is.EqualTo(new[] { "ROLE_CODE", "EMPLOYEE_ID" }));
    }

    [Test]
    public void PrimaryKeyAnchor_AnObjectTypeThatHasAnAnchor_IsNothing()
    {
        var objectType = new SqlObjectTypeDocument { Name = "Person", Schema = "HR", Table = "EMPLOYEES", AnchorColumns = ["BADGE_NUMBER"] };

        Assert.That(SqlObjectTypesEditorModel.PrimaryKeyAnchor(objectType, Detail("HR", "EMPLOYEES", ["EMPLOYEE_ID"])), Is.Empty,
            "the editor suggests; it never second-guesses an anchor the administrator chose");
    }

    #endregion

    #region Adding, renaming and removing

    [Test]
    public void AddObjectType_FromATable_ReadsFromThatTableWithTheAnchorGiven()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        var index = model!.AddObjectType("HR", "DEPARTMENTS", ["DEPARTMENT_ID"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index, Is.EqualTo(2));
            Assert.That(model.ObjectTypes[2].Table, Is.EqualTo("DEPARTMENTS"));
            Assert.That(model.ObjectTypes[2].AnchorColumns, Is.EqualTo(new[] { "DEPARTMENT_ID" }));
        }
    }

    [Test]
    public void RenameObjectType_ReferencesToItFollowTheNewName()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        model!.RenameObjectType(0, "Employee");

        var parsed = Accepted(model.ToJson()).ObjectTypes!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed[0].Columns!.Single().ReferencesObjectType, Is.EqualTo("Employee"), "its own self-reference");
            Assert.That(parsed[1].RelatedTables!.Single().ReferencesObjectType, Is.EqualTo("Employee"), "Group's members");
        }
    }

    [Test]
    public void RenameObjectType_ToTheNameOfAnotherObjectType_LeavesReferencesWhereTheyWere()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        model!.RenameObjectType(1, "Person");
        model.RenameObjectType(1, "Team");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectTypes[0].Columns!.Single().ReferencesObjectType, Is.EqualTo("Person"),
                "passing through a name another Object Type holds must not capture that Object Type's references");
            Assert.That(model.ObjectTypes[1].Name, Is.EqualTo("Team"));
        }
    }

    [Test]
    public void ReferencesTo_AnObjectTypeOthersReference_NamesEachReference()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        var references = model!.ReferencesTo(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(references, Has.Count.EqualTo(1), "Person's own MANAGER_ID goes with it, so it is not a reference to warn about");
            Assert.That(references.Single(), Does.Contain("Group").And.Contain("Members"));
        }
    }

    [Test]
    public void RemoveObjectType_ClearsTheReferencesOthersHeldToIt()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);

        model!.RemoveObjectType(0);

        var parsed = Accepted(model.ToJson()).ObjectTypes!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed.Select(o => o.Name), Is.EqualTo(new[] { "Group" }));
            Assert.That(parsed[0].RelatedTables!.Single().ReferencesObjectType, Is.Null,
                "the multi-valued attribute stays; only the reference to an Object Type that no longer exists goes");
        }
    }

    [Test]
    public void OtherColumnCount_CountsTheColumnsTheCardDoesNotMention()
    {
        SqlObjectTypesEditorModel.TryLoad(PersonAndGroup, out var model, out _);
        var employees = Detail("HR", "EMPLOYEES", ["EMPLOYEE_ID"]);
        employees.Columns = [Column("EMPLOYEE_ID"), Column("MANAGER_ID"), Column("GIVEN_NAME"), Column("SURNAME")];

        Assert.That(SqlObjectTypesEditorModel.OtherColumnCount(model!.ObjectTypes[0], employees), Is.EqualTo(2));
    }

    #endregion

    /// <summary>
    /// The document as the Connector would read it, having first checked the Connector accepts it.
    /// </summary>
    private static SqlObjectTypesDocument Accepted(string json)
    {
        Assert.That(SqlObjectTypesDocument.Validate(json), Is.Empty, "the editor must only ever write documents saving accepts");
        return SqlObjectTypesDocument.FromJson(json);
    }

    private static ConnectorCatalogueObjectDetail Detail(string schema, string name, List<string> primaryKey, params ConnectorCatalogueForeignKey[] foreignKeys) => new()
    {
        SchemaName = schema,
        Name = name,
        Kind = ConnectorCatalogueObjectKind.Table,
        PrimaryKey = primaryKey,
        ForeignKeys = [.. foreignKeys]
    };

    private static ConnectorCatalogueForeignKey ForeignKey(string constraint, string column, string referencedSchema, string referencedTable, string referencedColumn) => new()
    {
        ConstraintName = constraint,
        Columns = [column],
        ReferencedSchemaName = referencedSchema,
        ReferencedObjectName = referencedTable,
        ReferencedColumns = [referencedColumn]
    };

    private static ConnectorCatalogueColumn Column(string name) => new() { Name = name, DataTypeName = "nvarchar" };
}
