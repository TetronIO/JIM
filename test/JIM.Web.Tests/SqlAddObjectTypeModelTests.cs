// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the step logic of the SQL Connector's Add Object Type dialog (#2570): which steps must be completed and
/// which may be skipped, the anchor preselected from a primary key and the reason given when there is none, the
/// references suggested from foreign keys, and that finishing produces exactly the Object Type an administrator
/// writing the document by hand would have written.
/// </summary>
[TestFixture]
public class SqlAddObjectTypeModelTests
{
    private static readonly SqlObjectTypeDocument Group = new()
    {
        Name = "Group",
        Schema = "HR",
        Table = "GROUPS",
        AnchorColumns = ["GROUP_ID"]
    };

    #region Steps

    [Test]
    public void Source_NeedsANameAndATableBeforeContinuing()
    {
        var model = new SqlAddObjectTypeModel([]);

        Assert.That(model.CanContinue, Is.False);
        model.ObjectType.Name = "Person";
        Assert.That(model.CanContinue, Is.False, "a name alone says nothing about where the objects come from");
        model.ObjectType.Table = "EMPLOYEES";
        Assert.That(model.CanContinue, Is.True);
    }

    [Test]
    public void Source_AStatementInPlaceOfATable_CanContinue()
    {
        var model = new SqlAddObjectTypeModel([]) { ReadsFromStatement = true };
        model.ObjectType.Name = "Person";
        model.ObjectType.Select = "SELECT EMPLOYEE_ID FROM HR.EMPLOYEES";

        Assert.That(model.CanContinue, Is.True);
    }

    [Test]
    public void ReadsFromStatement_Switched_ClearsTheOtherSource()
    {
        var model = new SqlAddObjectTypeModel([], "HR", "EMPLOYEES");

        model.ReadsFromStatement = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectType.Table, Is.Null, "the document holds exactly one of a table and a statement");
            Assert.That(model.ObjectType.Schema, Is.Null);
        }
    }

    [Test]
    public void Anchor_NeedsAColumnBeforeContinuingOrFinishing()
    {
        var model = AtAnchorStep();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.CanContinue, Is.False);
            Assert.That(model.CanFinish, Is.False, "an Object Type with no anchor is one the Connector refuses");
        }

        model.ObjectType.AnchorColumns = ["EMPLOYEE_ID"];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.CanContinue, Is.True);
            Assert.That(model.CanFinish, Is.True, "references and multi-valued attributes are optional, so they can be skipped");
        }
    }

    [Test]
    public void OptionalSteps_CanAlwaysContinue()
    {
        var model = AtAnchorStep();
        model.ObjectType.AnchorColumns = ["EMPLOYEE_ID"];

        model.Next();
        Assert.That((model.Step, model.CanContinue), Is.EqualTo((SqlAddObjectTypeStep.References, true)));
        model.Next();
        Assert.That((model.Step, model.CanFinish), Is.EqualTo((SqlAddObjectTypeStep.MultiValuedAttributes, true)));
    }

    [Test]
    public void Next_FromAnIncompleteStep_StaysWhereItIs()
    {
        var model = new SqlAddObjectTypeModel([]);

        model.Next();

        Assert.That(model.Step, Is.EqualTo(SqlAddObjectTypeStep.Source));
    }

    [Test]
    public void Back_ReturnsToThePreviousStep()
    {
        var model = AtAnchorStep();

        model.Back();

        Assert.That(model.Step, Is.EqualTo(SqlAddObjectTypeStep.Source));
    }

    [Test]
    public void Constructor_WithATable_StartsWithThatTableChosen()
    {
        var model = new SqlAddObjectTypeModel([], "HR", "DEPARTMENTS");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectType.Schema, Is.EqualTo("HR"));
            Assert.That(model.ObjectType.Table, Is.EqualTo("DEPARTMENTS"));
            Assert.That(model.Step, Is.EqualTo(SqlAddObjectTypeStep.Source), "the name is still the administrator's to give");
        }
    }

    #endregion

    #region Anchor preselection

    [Test]
    public void SourceRead_ATableWithAPrimaryKey_PreselectsItInKeyOrderAndSaysWhy()
    {
        var model = AtAnchorStep();

        model.SourceRead(Table("ASSIGNMENTS", primaryKey: ["ROLE_CODE", "EMPLOYEE_ID"]), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectType.AnchorColumns, Is.EqualTo(new[] { "ROLE_CODE", "EMPLOYEE_ID" }));
            Assert.That(model.AnchorOrigin, Is.EqualTo(SqlAnchorOrigin.PrimaryKey));
        }
    }

    [TestCase(ConnectorCatalogueObjectKind.View, SqlAnchorOrigin.ViewDeclaresNoKeys)]
    [TestCase(ConnectorCatalogueObjectKind.Table, SqlAnchorOrigin.TableHasNoPrimaryKey)]
    [TestCase(ConnectorCatalogueObjectKind.Statement, SqlAnchorOrigin.StatementDeclaresNoKeys)]
    public void SourceRead_NoPrimaryKey_PreselectsNothingAndSaysWhy(ConnectorCatalogueObjectKind kind, SqlAnchorOrigin expected)
    {
        var model = AtAnchorStep();
        var source = Table("V_EMPLOYEES", primaryKey: []);
        source.Kind = kind;

        model.SourceRead(source, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectType.AnchorColumns, Is.Null.Or.Empty);
            Assert.That(model.AnchorOrigin, Is.EqualTo(expected));
        }
    }

    [Test]
    public void SourceRead_ColumnsUnreadable_SaysSo()
    {
        var model = AtAnchorStep();

        model.SourceRead(null, "Login failed for user 'jim'.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.AnchorOrigin, Is.EqualTo(SqlAnchorOrigin.ColumnsUnreadable));
            Assert.That(model.SourceFailure, Is.EqualTo("Login failed for user 'jim'."));
        }
    }

    [Test]
    public void SourceRead_AnotherTableAfterAPreselection_PreselectsTheNewTablesKey()
    {
        var model = AtAnchorStep();
        model.SourceRead(Table("EMPLOYEES", primaryKey: ["EMPLOYEE_ID"]), null);

        model.SourceRead(Table("ASSIGNMENTS", primaryKey: ["ROLE_CODE", "EMPLOYEE_ID"]), null);

        Assert.That(model.ObjectType.AnchorColumns, Is.EqualTo(new[] { "ROLE_CODE", "EMPLOYEE_ID" }),
            "an anchor JIM chose belongs to the table it was chosen from");
    }

    [Test]
    public void SourceRead_AfterTheAdministratorChoseTheAnchor_KeepsTheirChoice()
    {
        var model = AtAnchorStep();
        model.SourceRead(Table("EMPLOYEES", primaryKey: ["EMPLOYEE_ID"]), null);
        model.ObjectType.AnchorColumns = ["BADGE_NUMBER"];

        model.SourceRead(Table("EMPLOYEES", primaryKey: ["EMPLOYEE_ID"]), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectType.AnchorColumns, Is.EqualTo(new[] { "BADGE_NUMBER" }), "JIM suggests; it never overrides an administrator's choice");
            Assert.That(model.AnchorOrigin, Is.EqualTo(SqlAnchorOrigin.Chosen));
        }
    }

    #endregion

    #region References

    [Test]
    public void ReferenceSuggestions_ForeignKeysToAnotherObjectTypeAndToItself_SuggestsBoth()
    {
        var model = new SqlAddObjectTypeModel([Group], "HR", "EMPLOYEES");
        model.ObjectType.Name = "Person";
        var employees = Table("EMPLOYEES", primaryKey: ["EMPLOYEE_ID"],
            ForeignKey("FK_EMP_MANAGER", "MANAGER_ID", "EMPLOYEES", "EMPLOYEE_ID"),
            ForeignKey("FK_EMP_GROUP", "PRIMARY_GROUP_ID", "GROUPS", "GROUP_ID"),
            ForeignKey("FK_EMP_DEPT", "DEPARTMENT_ID", "DEPARTMENTS", "DEPARTMENT_ID"));
        model.Next();
        model.SourceRead(employees, null);

        var suggestions = model.ReferenceSuggestions;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggestions.Select(s => (s.ColumnName, s.ReferencedObjectTypeName)), Is.EquivalentTo(new[]
            {
                ("MANAGER_ID", (string?)"Person"),
                ("PRIMARY_GROUP_ID", (string?)"Group")
            }), "a manager is a reference to the Object Type being added, so it counts as one of the document's");
            Assert.That(suggestions.All(s => s.Kind == SqlObjectTypeSuggestionKind.Reference), Is.True,
                "offering another Object Type from inside this dialog would be a dialog within a dialog");
        }
    }

    [Test]
    public void AcceptReference_AddsTheReferenceAndStopsSuggestingIt()
    {
        var model = new SqlAddObjectTypeModel([Group], "HR", "EMPLOYEES");
        model.ObjectType.Name = "Person";
        model.Next();
        model.SourceRead(Table("EMPLOYEES", primaryKey: ["EMPLOYEE_ID"], ForeignKey("FK_EMP_GROUP", "PRIMARY_GROUP_ID", "GROUPS", "GROUP_ID")), null);

        model.AcceptReference(model.ReferenceSuggestions.Single());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(model.ObjectType.Columns!.Single().ReferencesObjectType, Is.EqualTo("Group"));
            Assert.That(model.ReferenceSuggestions, Is.Empty);
        }
    }

    #endregion

    #region Finishing

    [Test]
    public void Build_TheFirstObjectTypeOfAConnectedSystem_IsTheDocumentAHandWrittenOneWouldBe()
    {
        // PRD Scenario 1: choose HR.EMPLOYEES, accept the preselected key, finish.
        var model = new SqlAddObjectTypeModel([]);
        model.ObjectType.Name = "Person";
        model.ObjectType.Schema = "HR";
        model.ObjectType.Table = "EMPLOYEES";
        model.Next();
        model.SourceRead(Table("EMPLOYEES", primaryKey: ["EMPLOYEE_ID"]), null);

        var written = new SqlObjectTypesDocument { ObjectTypes = [model.Build()] }.ToJson();
        var handWritten = SqlObjectTypesDocument.FromJson(
            """{"objectTypes":[{"name":"Person","schema":"HR","table":"EMPLOYEES","anchorColumns":["EMPLOYEE_ID"]}]}""").ToJson();

        Assert.That(written, Is.EqualTo(handWritten));
    }

    [Test]
    public void Build_LeavesOutRowsLeftEmpty()
    {
        var model = AtAnchorStep();
        model.ObjectType.AnchorColumns = ["EMPLOYEE_ID", ""];
        model.ObjectType.Columns = [new SqlColumnDocument()];
        model.ObjectType.RelatedTables = [];

        var built = model.Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built.AnchorColumns, Is.EqualTo(new[] { "EMPLOYEE_ID" }));
            Assert.That(built.Columns, Is.Null, "a reference row added and never filled in is not a reference");
            Assert.That(built.RelatedTables, Is.Null);
        }
    }

    #endregion

    private static SqlAddObjectTypeModel AtAnchorStep()
    {
        var model = new SqlAddObjectTypeModel([], "HR", "EMPLOYEES");
        model.ObjectType.Name = "Person";
        model.Next();
        Assert.That(model.Step, Is.EqualTo(SqlAddObjectTypeStep.Anchor));
        return model;
    }

    private static ConnectorCatalogueObjectDetail Table(string name, List<string> primaryKey, params ConnectorCatalogueForeignKey[] foreignKeys) => new()
    {
        SchemaName = "HR",
        Name = name,
        Kind = ConnectorCatalogueObjectKind.Table,
        Columns = [new ConnectorCatalogueColumn { Name = "EMPLOYEE_ID", DataTypeName = "int" }],
        PrimaryKey = primaryKey,
        ForeignKeys = [.. foreignKeys]
    };

    private static ConnectorCatalogueForeignKey ForeignKey(string constraint, string column, string referencedTable, string referencedColumn) => new()
    {
        ConstraintName = constraint,
        Columns = [column],
        ReferencedSchemaName = "HR",
        ReferencedObjectName = referencedTable,
        ReferencedColumns = [referencedColumn]
    };
}
