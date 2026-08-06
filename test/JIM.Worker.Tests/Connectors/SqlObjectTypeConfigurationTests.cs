// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers the Object Types document: the structured JSON an administrator supplies to say which tables,
/// views or queries this Connected System's object types come from.
/// <para>
/// Every malformed case is asserted on its message rather than only on the fact that it was refused. The
/// document is hand-written, so the message is the whole of the administrator's feedback loop, and a
/// half-configured Connected System is precisely what strict parsing exists to prevent.
/// </para>
/// </summary>
[TestFixture]
public class SqlObjectTypeConfigurationTests
{
    #region Valid documents

    [Test]
    public void Parse_ATableBackedObjectType_ReadsEveryField()
    {
        var configuration = SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "schema": "HR",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ]
                }
              ]
            }
            """);

        var objectType = configuration.ObjectTypes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(objectType.Name, Is.EqualTo("Person"));
            Assert.That(objectType.SchemaName, Is.EqualTo("HR"));
            Assert.That(objectType.TableName, Is.EqualTo("EMPLOYEES"));
            Assert.That(objectType.SelectStatement, Is.Null);
            Assert.That(objectType.IsCustomSelect, Is.False);
            Assert.That(objectType.AnchorColumns, Is.EqualTo(new[] { "EMPLOYEE_ID" }));
        });
    }

    [Test]
    public void Parse_ASelectBackedObjectType_ReadsTheStatement()
    {
        var configuration = SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "select": "SELECT EMPLOYEE_ID, GIVEN_NAME FROM HR.EMPLOYEES WHERE ACTIVE = 1",
                  "anchorColumns": [ "EMPLOYEE_ID" ]
                }
              ]
            }
            """);

        var objectType = configuration.ObjectTypes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(objectType.IsCustomSelect, Is.True);
            Assert.That(objectType.SelectStatement, Does.StartWith("SELECT EMPLOYEE_ID"));
            Assert.That(objectType.TableName, Is.Null);
        });
    }

    [Test]
    public void Parse_ARelatedTable_ReadsEveryField()
    {
        var configuration = SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
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
            """);

        var relatedTable = configuration.ObjectTypes.Single().RelatedTables.Single();
        Assert.Multiple(() =>
        {
            Assert.That(relatedTable.AttributeName, Is.EqualTo("PhoneNumbers"));
            Assert.That(relatedTable.SchemaName, Is.EqualTo("HR"));
            Assert.That(relatedTable.TableName, Is.EqualTo("EMPLOYEE_PHONES"));
            Assert.That(relatedTable.ValueColumn, Is.EqualTo("PHONE_NUMBER"));
            Assert.That(relatedTable.JoinColumns, Is.EqualTo(new[] { "EMPLOYEE_ID" }));
            Assert.That(relatedTable.ReferencesObjectType, Is.Null);
        });
    }

    [Test]
    public void Parse_AReferenceColumn_NamesTheObjectTypeItPointsAt()
    {
        var configuration = SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "columns": [ { "name": "MANAGER_EMPLOYEE_ID", "referencesObjectType": "Person" } ]
                }
              ]
            }
            """);

        var column = configuration.ObjectTypes.Single().Columns.Single();
        Assert.Multiple(() =>
        {
            Assert.That(column.Name, Is.EqualTo("MANAGER_EMPLOYEE_ID"));
            Assert.That(column.ReferencesObjectType, Is.EqualTo("Person"));
        });
    }

    [Test]
    public void Parse_TheExampleInTheSettingsDescription_IsAcceptedVerbatim()
    {
        // The Description is the administrator's copy-paste starting point, so a document that does not
        // parse would be the worst possible first experience of this setting.
        Assert.DoesNotThrow(() => SqlSchemaConfiguration.Parse(SqlConnectorConstants.ObjectTypesExample));
    }

    [Test]
    public void Parse_ACompositeAnchor_KeepsTheColumnsInTheDeclaredOrder()
    {
        // The order is the key order, and it is what makes a keyset page boundary reproducible, so it is
        // never sorted or de-duplicated into a set.
        var configuration = SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Enrolment",
                  "table": "ENROLMENTS",
                  "anchorColumns": [ "STUDENT_ID", "COURSE_ID" ]
                }
              ]
            }
            """);

        Assert.That(configuration.ObjectTypes.Single().AnchorColumns, Is.EqualTo(new[] { "STUDENT_ID", "COURSE_ID" }));
    }

    #endregion

    #region Malformed documents

    [Test]
    public void Parse_NoDocumentSupplied_ExplainsThatObjectTypesAreRequired()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("   "));

        Assert.That(exception!.Message, Does.Contain(SqlConnectorConstants.SettingObjectTypes));
    }

    [Test]
    public void Parse_NotJson_ReportsWhereTheDocumentStoppedMakingSense()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("Person = EMPLOYEES"));

        Assert.That(exception!.Message, Does.Contain(SqlConnectorConstants.SettingObjectTypes));
        Assert.That(exception.InnerException, Is.Not.Null, "The parser's own account of where the document broke is what locates the typo.");
    }

    [Test]
    public void Parse_AnEmptyObjectTypeList_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""{ "objectTypes": [] }"""));

        Assert.That(exception!.Message, Does.Contain("at least one"));
    }

    [Test]
    public void Parse_AnUnknownField_NamesIt()
    {
        // A typo in a field name is the most likely mistake in a hand-written document, and silently
        // ignoring it would leave the Connected System configured differently from how it reads.
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "anchorColumn": "EMPLOYEE_ID" }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("anchorColumn"));
    }

    [Test]
    public void Parse_AnObjectTypeWithNoName_SaysWhichPositionItIsIn()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [ { "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] } ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("1"), "With no name to quote, the position in the list is the only way to point at it.");
        Assert.That(exception.Message, Does.Contain("name"));
    }

    [Test]
    public void Parse_TwoObjectTypesWithTheSameName_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] },
                { "name": "person", "table": "CONTRACTORS", "anchorColumns": [ "CONTRACTOR_ID" ] }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("Person").IgnoreCase);
    }

    [Test]
    public void Parse_AnObjectTypeWithNeitherTableNorSelect_NamesTheObjectTypeAndBothFields()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [ { "name": "Person", "anchorColumns": [ "EMPLOYEE_ID" ] } ]
            }
            """));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Person"));
            Assert.That(exception.Message, Does.Contain("table"));
            Assert.That(exception.Message, Does.Contain("select"));
        });
    }

    [Test]
    public void Parse_AnObjectTypeWithBothTableAndSelect_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                { "name": "Person", "table": "EMPLOYEES", "select": "SELECT * FROM EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("Person"));
    }

    [Test]
    public void Parse_ASelectWithASchema_IsRefused()
    {
        // A schema qualifies a table name; a statement carries its own qualification, so accepting both
        // would leave an administrator believing the schema is doing something it is not.
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "select": "SELECT EMPLOYEE_ID FROM HR.EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("schema"));
    }

    [TestCase("UPDATE EMPLOYEES SET ACTIVE = 0", TestName = "Parse_ASelectThatIsNotASelect_IsRefused(update)")]
    [TestCase("SELECT 1 FROM DUAL; DROP TABLE EMPLOYEES", TestName = "Parse_ASelectThatIsNotASelect_IsRefused(batch)")]
    public void Parse_ASelectThatIsNotASelect_IsRefused(string statement)
    {
        // Connector configuration is privileged administrator input, so this is not a defence against a
        // hostile administrator: it catches the accidental paste, and keeps one statement one statement.
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse($$"""
            {
              "objectTypes": [ { "name": "Person", "select": "{{statement}}", "anchorColumns": [ "EMPLOYEE_ID" ] } ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("Person"));
    }

    [Test]
    public void Parse_AnObjectTypeWithNoAnchorColumns_NamesTheObjectTypeAndTheField()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [ { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [] } ]
            }
            """));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Person"));
            Assert.That(exception.Message, Does.Contain("anchorColumns"));
        });
    }

    [Test]
    public void Parse_AnAnchorColumnListedTwice_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [ { "name": "Person", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID", "EMPLOYEE_ID" ] } ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("EMPLOYEE_ID"));
    }

    [TestCase("EMPLOYEES ", TestName = "Parse_AnIdentifierNoDatabaseCouldHold_IsRefused(trailing space)")]
    [TestCase(" EMPLOYEES", TestName = "Parse_AnIdentifierNoDatabaseCouldHold_IsRefused(leading space)")]
    public void Parse_AnIdentifierNoDatabaseCouldHold_IsRefused(string tableName)
    {
        // Identifiers are interpolated into command text after quoting, so they are validated before
        // anything is ever built from them. A pasted-in space is the case that would otherwise surface
        // much later as a table the database account cannot see: quoting it names a different object.
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse($$"""
            {
              "objectTypes": [ { "name": "Person", "table": "{{tableName}}", "anchorColumns": [ "EMPLOYEE_ID" ] } ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("Person"));
    }

    [Test]
    public void Parse_ARelatedTableWithNoAttributeName_NamesTheObjectType()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "relatedTables": [ { "table": "EMPLOYEE_PHONES", "valueColumn": "PHONE_NUMBER", "joinColumns": [ "EMPLOYEE_ID" ] } ]
                }
              ]
            }
            """));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Person"));
            Assert.That(exception.Message, Does.Contain("attributeName"));
        });
    }

    [Test]
    public void Parse_ARelatedTableJoiningOnFewerColumnsThanTheAnchorHas_IsRefused()
    {
        // A join on part of a composite anchor gathers another object's values onto this one, silently.
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Enrolment",
                  "table": "ENROLMENTS",
                  "anchorColumns": [ "STUDENT_ID", "COURSE_ID" ],
                  "relatedTables": [
                    { "attributeName": "Grades", "table": "ENROLMENT_GRADES", "valueColumn": "GRADE", "joinColumns": [ "STUDENT_ID" ] }
                  ]
                }
              ]
            }
            """));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Enrolment"));
            Assert.That(exception.Message, Does.Contain("Grades"));
        });
    }

    [Test]
    public void Parse_TwoRelatedTablesWithTheSameAttributeName_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "relatedTables": [
                    { "attributeName": "PhoneNumbers", "table": "EMPLOYEE_PHONES", "valueColumn": "PHONE_NUMBER", "joinColumns": [ "EMPLOYEE_ID" ] },
                    { "attributeName": "PhoneNumbers", "table": "EMPLOYEE_MOBILES", "valueColumn": "MOBILE_NUMBER", "joinColumns": [ "EMPLOYEE_ID" ] }
                  ]
                }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("PhoneNumbers"));
    }

    [Test]
    public void Parse_AReferenceToAnObjectTypeThatIsNotConfigured_NamesBoth()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
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
            """));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("DEPARTMENT_ID"));
            Assert.That(exception.Message, Does.Contain("Department"));
        });
    }

    [Test]
    public void Parse_ARelatedTableReferencingAnObjectTypeThatIsNotConfigured_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Group",
                  "table": "GROUPS",
                  "anchorColumns": [ "GROUP_ID" ],
                  "relatedTables": [
                    { "attributeName": "Members", "table": "GROUP_MEMBERS", "valueColumn": "MEMBER_ID", "joinColumns": [ "GROUP_ID" ], "referencesObjectType": "Person" }
                  ]
                }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("Person"));
    }

    [Test]
    public void Parse_TheSameColumnConfiguredTwice_IsRefused()
    {
        var exception = Assert.Throws<SqlSchemaConfigurationException>(() => SqlSchemaConfiguration.Parse("""
            {
              "objectTypes": [
                {
                  "name": "Person",
                  "table": "EMPLOYEES",
                  "anchorColumns": [ "EMPLOYEE_ID" ],
                  "columns": [
                    { "name": "MANAGER_EMPLOYEE_ID", "referencesObjectType": "Person" },
                    { "name": "MANAGER_EMPLOYEE_ID", "referencesObjectType": "Person" }
                  ]
                }
              ]
            }
            """));

        Assert.That(exception!.Message, Does.Contain("MANAGER_EMPLOYEE_ID"));
    }

    #endregion
}
