// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Connectors.Sql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the public face of the Object Types document: the model an editor builds and writes back, and
/// validation that reports every problem with where it is, rather than only the first.
/// </summary>
/// <remarks>
/// The document is still parsed by <see cref="SqlSchemaConfiguration.Parse"/>; these tests pin that the
/// model and the parser agree, so an editor built on the model can never write a document the parser
/// rejects, and that validation reports exactly what saving would refuse.
/// </remarks>
[TestFixture]
public class SqlObjectTypesDocumentTests
{
    #region Validate

    [Test]
    public void Validate_TheShippedExamples_ReportNoProblems()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SqlObjectTypesDocument.Validate(SqlConnectorConstants.ObjectTypesExample), Is.Empty);
            Assert.That(SqlObjectTypesDocument.Validate(SqlConnectorConstants.DeltaConfigurationExample), Is.Empty);
        }
    }

    [Test]
    public void Validate_NoDocument_ReportsOneProblem()
    {
        var problems = SqlObjectTypesDocument.Validate("  ");

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0].Message, Does.Contain(SqlConnectorConstants.SettingObjectTypes));
    }

    [Test]
    public void Validate_AMisspeltField_SuggestsTheIntendedFieldOnItsLine()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumn": [ "EMPLOYEE_ID" ]
                }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Message, Does.Contain("'anchorColumn'"));
            Assert.That(problem.Message, Does.Contain("Did you mean 'anchorColumns'?"));
            Assert.That(problem.Message, Does.Not.Contain("JIM.Connectors"), "the administrator should never be shown a .NET type name");
            Assert.That(problem.LineNumber, Is.EqualTo(6));
            Assert.That(problem.ObjectTypeIndex, Is.Zero);
            Assert.That(problem.Field, Is.EqualTo("anchorColumn"));
        }
    }

    [Test]
    public void Validate_AMisspeltFieldInARelatedTable_SuggestsFromThatLevel()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Group",
                  "table": "GROUPS",
                  "anchorColumns": [ "GROUP_ID" ],
                  "relatedTables": [
                    { "attributeName": "Members", "table": "GROUP_MEMBERS", "valueColum": "MEMBER_ID", "joinColumns": [ "GROUP_ID" ] }
                  ]
                }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        Assert.That(problem.Message, Does.Contain("Did you mean 'valueColumn'?"));
    }

    [Test]
    public void Validate_AnUnknownFieldLikeNoKnownOne_NamesItWithoutASuggestion()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ], "colour": "blue" }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Message, Does.Contain("'colour'"));
            Assert.That(problem.Message, Does.Not.Contain("Did you mean"));
        }
    }

    [Test]
    public void Validate_NotJson_ReportsTheLineWhereItStoppedMakingSense()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES" "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        Assert.That(problem.LineNumber, Is.EqualTo(3));
    }

    [Test]
    public void Validate_TwoObjectTypesEachWithAFault_ReportsBothAgainstTheirObjectTypes()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES" },
                { "name": "Group", "anchorColumns": [ "GROUP_ID" ] }
              ]
            }
            """;

        var problems = SqlObjectTypesDocument.Validate(document);

        Assert.That(problems, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(problems[0].ObjectTypeIndex, Is.EqualTo(0));
            Assert.That(problems[0].ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(problems[0].Field, Is.EqualTo("anchorColumns"));
            Assert.That(problems[1].ObjectTypeIndex, Is.EqualTo(1));
            Assert.That(problems[1].ObjectTypeName, Is.EqualTo("Group"));
        }
    }

    [Test]
    public void Validate_AReferenceToAnObjectTypeWithAFaultOfItsOwn_ReportsOnlyThatFault()
    {
        // Group is declared, just faulty; telling the administrator Person points at an undeclared Object Type
        // as well would be wrong, and would send them looking for a problem that is not there.
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "columns": [ { "name": "PRIMARY_GROUP_ID", "referencesObjectType": "Group" } ]
                },
                { "name": "Group", "table": "GROUPS" }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        Assert.That(problem.ObjectTypeName, Is.EqualTo("Group"));
    }

    [Test]
    public void Validate_AReferenceToAnUndeclaredObjectType_IsReportedAgainstTheReferringObjectType()
    {
        const string document = """
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "columns": [ { "name": "PRIMARY_GROUP_ID", "referencesObjectType": "Group" } ]
                }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(problem.Message, Does.Contain("Group"));
        }
    }

    [Test]
    public void Validate_TheSameNameTwice_IsReportedAgainstTheSecond()
    {
        const string document = """
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] },
                { "name": "person", "table": "CONTRACTORS", "anchorColumns": [ "CONTRACTOR_ID" ] }
              ]
            }
            """;

        var problem = SqlObjectTypesDocument.Validate(document).Single();

        Assert.That(problem.ObjectTypeIndex, Is.EqualTo(1));
    }

    #endregion

    #region Model round trip

    [Test]
    public void ToJson_TheShippedExamples_RoundTripToTheSameContent()
    {
        foreach (var example in new[] { SqlConnectorConstants.ObjectTypesExample, SqlConnectorConstants.DeltaConfigurationExample })
        {
            var written = SqlObjectTypesDocument.FromJson(example).ToJson();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(JsonNode.DeepEquals(JsonNode.Parse(written), JsonNode.Parse(example)), Is.True,
                    "the model must carry every field the parser accepts, or an editor would drop configuration");
                Assert.That(SqlObjectTypesDocument.Validate(written), Is.Empty);
            }
        }
    }

    [Test]
    public void ToJson_WrittenTwice_IsIdentical()
    {
        var once = SqlObjectTypesDocument.FromJson(SqlConnectorConstants.DeltaConfigurationExample).ToJson();
        var twice = SqlObjectTypesDocument.FromJson(once).ToJson();

        Assert.That(twice, Is.EqualTo(once), "a canonical layout changes a hand-formatted document once, never again");
    }

    [Test]
    public void ToJson_ABuiltDocument_IsAcceptedByTheParser()
    {
        var document = new SqlObjectTypesDocument
        {
            ObjectTypes =
            [
                new SqlObjectTypeDocument { Name = "Person", Schema = "HR", Table = "EMPLOYEES", AnchorColumns = ["EMPLOYEE_ID"] }
            ]
        };

        var configuration = SqlSchemaConfiguration.Parse(document.ToJson());

        Assert.That(configuration.ObjectTypes.Single().TableName, Is.EqualTo("EMPLOYEES"));
    }

    [Test]
    public void ToJson_UnsetFields_AreLeftOut()
    {
        var document = new SqlObjectTypesDocument
        {
            ObjectTypes = [new SqlObjectTypeDocument { Name = "Person", Table = "EMPLOYEES", AnchorColumns = ["EMPLOYEE_ID"] }]
        };

        var written = document.ToJson();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Does.Not.Contain("null"));
            Assert.That(written, Does.Not.Contain("\"select\""));
            Assert.That(written, Does.Contain("\"anchorColumns\""), "fields are written in the document's own camel case");
        }
    }

    #endregion
}
