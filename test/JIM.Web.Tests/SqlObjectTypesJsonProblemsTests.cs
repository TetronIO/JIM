// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers where the SQL Object Types JSON view marks each problem (#2570): a fault in the JSON itself on the line the
/// parser stopped at, and a problem with what the document says on the line of the field it concerns, or of the
/// Object Type when that field is missing. Messages are the parser's own, so the JSON view and saving agree.
/// </summary>
[TestFixture]
public class SqlObjectTypesJsonProblemsTests
{
    [Test]
    public void Locate_AMisspeltField_IsOnItsLineAndSuggestsTheFieldMeant()
    {
        // PRD Scenario 4: a pasted document with 'anchorColumn' on line 7.
        const string pasted = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "schema": "HR",
                  "table": "EMPLOYEES",
                  "anchorColumn": [ "EMPLOYEE_ID" ]
                }
              ]
            }
            """;

        var problem = SqlObjectTypesJsonProblems.Locate(pasted).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Line, Is.EqualTo(7));
            Assert.That(problem.Message, Does.Contain("anchorColumn").And.Contain("Did you mean 'anchorColumns'?"));
        }
    }

    [Test]
    public void Locate_TextThatIsNotJson_IsOnTheLineTheParserStoppedAt()
    {
        const string broken = """
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] }
                { "name": "Group" }
              ]
            }
            """;

        Assert.That(SqlObjectTypesJsonProblems.Locate(broken).Single().Line, Is.EqualTo(4));
    }

    [Test]
    public void Locate_AFieldWithAProblem_IsOnThatFieldsLine()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "select": "SELECT EMPLOYEE_ID FROM HR.EMPLOYEES; DROP TABLE HR.EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ]
                }
              ]
            }
            """;

        Assert.That(SqlObjectTypesJsonProblems.Locate(document).Single().Line, Is.EqualTo(5));
    }

    [Test]
    public void Locate_AMissingField_IsOnTheLineItsObjectTypeOpens()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] },
                {
                  "name": "Group",
                  "table": "GROUPS"
                }
              ]
            }
            """;

        var problem = SqlObjectTypesJsonProblems.Locate(document).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Line, Is.EqualTo(4), "there is no 'anchorColumns' line to mark, so the Object Type that lacks one is marked");
            Assert.That(problem.Message, Does.Contain("anchorColumns"));
        }
    }

    [Test]
    public void Locate_AReferenceToAnUndeclaredObjectType_IsOnTheLineOfTheColumnsThatHoldIt()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "columns": [ { "name": "DEPARTMENT_ID", "referencesObjectType": "Department" } ]
                }
              ]
            }
            """;

        Assert.That(SqlObjectTypesJsonProblems.Locate(document).Single().Line, Is.EqualTo(7));
    }

    [Test]
    public void Locate_AnEmptyDocument_IsAProblemWithNoLine()
    {
        var problem = SqlObjectTypesJsonProblems.Locate("").Single();

        Assert.That(problem.Line, Is.Null, "there is no line to mark in an empty document");
    }

    [Test]
    public void Locate_ADocumentTheConnectorAccepts_HasNoProblems()
    {
        Assert.That(SqlObjectTypesJsonProblems.Locate("""{ "objectTypes": [ { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] } ] }"""),
            Is.Empty);
    }

    [Test]
    public void Locate_ProblemsAreInLineOrder()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Group", "table": "GROUPS" },
                { "name": "Person", "table": "EMPLOYEES" }
              ]
            }
            """;

        Assert.That(SqlObjectTypesJsonProblems.Locate(document).Select(p => p.Line), Is.EqualTo(new int?[] { 3, 4 }));
    }
}
