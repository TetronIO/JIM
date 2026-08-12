// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Covers what the JIM SQL Connector discovers: one Connected System Object Type per configured object
/// type, its columns typed by the dialect's own mapping, its related tables as multi-valued attributes,
/// and its anchor as the recommended external ID. No test here touches a database server; the dialect
/// seam answers catalogue queries from a stand-in catalogue instead.
/// </summary>
[TestFixture]
public class SqlConnectorSchemaTests
{
    private SqlConnector _connector = null!;
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new SqlConnector();
        _logger = new LoggerConfiguration().CreateLogger();
    }

    [TearDown]
    public void TearDown()
    {
        _connector.Dispose();
        (_logger as IDisposable)?.Dispose();
    }

    #region Sources

    [Test]
    public async Task GetSchemaAsync_ATableBackedObjectType_EmitsItsColumnsTypedByTheDialectAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64),
            new FakeCatalogueColumn("STARTED_ON", "datetime2"),
            new FakeCatalogueColumn("FULL_TIME_EQUIVALENT", "decimal", Precision: 5, Scale: 2));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Name, Is.EqualTo("Person"));
            Assert.That(AttributeType(objectType, "EMPLOYEE_ID"), Is.EqualTo(AttributeDataType.Number));
            Assert.That(AttributeType(objectType, "GIVEN_NAME"), Is.EqualTo(AttributeDataType.Text));
            Assert.That(AttributeType(objectType, "STARTED_ON"), Is.EqualTo(AttributeDataType.DateTime));
            Assert.That(AttributeType(objectType, "FULL_TIME_EQUIVALENT"), Is.EqualTo(AttributeDataType.Decimal));
            Assert.That(schema.Warnings, Is.Empty);
        }
    }

    [Test]
    public async Task GetSchemaAsync_ATableBackedObjectType_ReadsTheCataloguesTheDialectDeclaresAsync()
    {
        // Discovery must ask the provider for its catalogue queries rather than writing SQL of its own,
        // which is the whole point of the dialect seam.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));

        await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        Assert.That(provider.ExecutedCommandTexts, Is.SupersetOf(new[]
        {
            provider.TablesCommandText,
            provider.ViewsCommandText,
            provider.ColumnsCommandText
        }));
    }

    [Test]
    public async Task GetSchemaAsync_AViewBackedObjectType_IsDiscoveredAndItsAttributesAreReadOnlyAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddView("HR", "V_EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "V_EMPLOYEES"));

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Attributes, Has.Count.EqualTo(2));
            Assert.That(objectType.Attributes.Select(a => a.Writability), Has.All.EqualTo(AttributeWritability.ReadOnly),
                "Only a table is guaranteed to accept an INSERT, UPDATE or DELETE, so a view-backed object type is not offered as an export target.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_ACustomSelectObjectType_ReadsItsColumnsFromTheStatementAsync()
    {
        const string statement = "SELECT EMPLOYEE_ID, GIVEN_NAME FROM HR.EMPLOYEES WHERE ACTIVE = 1";
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddSelectStatement(statement,
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64));

        var schema = await GetSchemaAsync(provider, $$"""
            {
              "objectTypes": [
                { "name": "Person", "select": "{{statement}}", "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """);

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Attributes.Select(a => a.Name), Is.EquivalentTo(new[] { "EMPLOYEE_ID", "GIVEN_NAME" }));
            Assert.That(AttributeType(objectType, "GIVEN_NAME"), Is.EqualTo(AttributeDataType.Text));
            Assert.That(objectType.RecommendedExternalIdAttribute.Name, Is.EqualTo("EMPLOYEE_ID"));
        }
    }

    [Test]
    public void GetSchemaAsync_ASourceTheAccountCannotSee_ThrowsNamingTheObjectTypeAndTheTable()
    {
        // A table that does not appear in the catalogue is either misspelled or not granted, and both
        // read the same way to JIM. Discovering an object type with no columns instead would present as
        // a successful refresh that quietly unmapped every Attribute Flow.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEE")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("Person"));
            Assert.That(exception.Message, Does.Contain("EMPLOYEE"));
        }
    }

    [Test]
    public async Task GetSchemaAsync_ASourceWithNoSchemaNamed_ResolvesItFromTheCatalogueAsync()
    {
        // Schema qualification is optional, because a least-privilege account usually sees exactly one
        // object of a given name and asking for a schema it does not know is friction for nothing.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument(null, "EMPLOYEES"));

        Assert.That(schema.ObjectTypes.Single().Attributes, Has.Count.EqualTo(1));
    }

    [Test]
    public void GetSchemaAsync_AnUnqualifiedSourceNamingSeveralSchemasObjects_AsksForQualification()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));
        provider.Catalogue.AddTable("PAYROLL", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int"));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, ObjectTypesDocument(null, "EMPLOYEES")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("HR"));
            Assert.That(exception.Message, Does.Contain("PAYROLL"));
        }
    }

    [Test]
    public void GetSchemaAsync_NoObjectTypesConfigured_ThrowsRatherThanDiscoveringNothing()
    {
        var provider = new FakeSqlProvider();

        Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () => await GetSchemaAsync(provider, null));
    }

    #endregion

    #region Anchors

    [Test]
    public async Task GetSchemaAsync_ASingleAnchorColumn_BecomesTheRecommendedExternalIdAttributeAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.RecommendedExternalIdAttribute.Name, Is.EqualTo("EMPLOYEE_ID"));
            Assert.That(objectType.RecommendedExternalIdAttribute, Is.SameAs(objectType.Attributes.Single(a => a.Name == "EMPLOYEE_ID")),
                "The recommendation names one of the object type's own attributes, not a copy of it.");
            Assert.That(objectType.RecommendedSecondaryExternalIdAttribute, Is.Null,
                "A row's anchor is its only identifier; there is no second one to resolve references with.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_ACompositeAnchor_RecommendsASynthesisedSingleValuedAnchorAttributeAsync()
    {
        // JIM identifies a Connected System Object by one attribute value, so a composite key is
        // projected as one attribute composed of its parts. The parts remain individually available.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "ENROLMENTS",
            new FakeCatalogueColumn("STUDENT_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("COURSE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GRADE", "nvarchar", MaxLength: 2));

        var schema = await GetSchemaAsync(provider, """
            {
              "objectTypes": [
                {
                  "name": "Enrolment",
                  "schema": "HR",
                  "table": "ENROLMENTS",
                  "anchorColumns": [ "STUDENT_ID", "COURSE_ID" ]
                }
              ]
            }
            """);

        var objectType = schema.ObjectTypes.Single();
        var anchor = objectType.RecommendedExternalIdAttribute;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.Name, Is.EqualTo("STUDENT_ID+COURSE_ID"));
            Assert.That(anchor.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(anchor.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(anchor.Writability, Is.EqualTo(AttributeWritability.ReadOnly), "A composed anchor is JIM's own projection, so nothing can be written to it.");
            Assert.That(objectType.Attributes.Select(a => a.Name), Does.Contain("STUDENT_ID").And.Contain("COURSE_ID"));
        }
    }

    [Test]
    public async Task GetSchemaAsync_ATableBackedObjectTypesAnchor_IsWritableOnCreateSoTheTableCanBeProvisionedIntoAsync()
    {
        // A table whose primary key is a natural identifier can only be provisioned into if a
        // Synchronisation Rule may author the key. Marking the anchor Read-Only refuses that Attribute
        // Flow outright, which makes provisioning impossible; marking it Writable would let an Update
        // Pending Export rewrite the key and orphan the Connected System Object.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Writability(objectType, "EMPLOYEE_ID"), Is.EqualTo(AttributeWritability.WritableOnCreate),
                "JIM supplies a natural primary key when it inserts the row, and never rewrites it afterwards.");
            Assert.That(Writability(objectType, "GIVEN_NAME"), Is.EqualTo(AttributeWritability.Writable),
                "An ordinary column of a table is writable whenever the object exists.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_AViewBackedObjectTypesAnchor_StaysReadOnlyBecauseNothingAboutThatSourceIsWritableAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddView("HR", "V_EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "V_EMPLOYEES"));

        Assert.That(Writability(schema.ObjectTypes.Single(), "EMPLOYEE_ID"), Is.EqualTo(AttributeWritability.ReadOnly),
            "A view is not an export target at all, so there is no create for its anchor to be writable on.");
    }

    [Test]
    public async Task GetSchemaAsync_ASelectBackedObjectTypesAnchor_StaysReadOnlyAsync()
    {
        const string statement = "SELECT EMPLOYEE_ID, GIVEN_NAME FROM HR.EMPLOYEES WHERE ACTIVE = 1";
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddSelectStatement(statement,
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GIVEN_NAME", "nvarchar", MaxLength: 64));

        var schema = await GetSchemaAsync(provider, $$"""
            {
              "objectTypes": [
                { "name": "Person", "select": "{{statement}}", "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """);

        Assert.That(Writability(schema.ObjectTypes.Single(), "EMPLOYEE_ID"), Is.EqualTo(AttributeWritability.ReadOnly),
            "A SELECT statement is not something JIM can write to, so nothing it exposes is writable.");
    }

    [Test]
    public async Task GetSchemaAsync_ACompositeAnchorOnATable_LeavesTheComposedAttributeReadOnlyAndEachAnchorColumnWritableOnCreateAsync()
    {
        // The composed attribute is JIM's own projection rather than a column, so nothing is ever
        // written to it; the columns it is composed from are real columns of the table, and a composite
        // natural key would be unprovisionable if they were not individually writable on create.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "ENROLMENTS",
            new FakeCatalogueColumn("STUDENT_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("COURSE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("GRADE", "nvarchar", MaxLength: 2));

        var schema = await GetSchemaAsync(provider, """
            {
              "objectTypes": [
                {
                  "name": "Enrolment",
                  "schema": "HR",
                  "table": "ENROLMENTS",
                  "anchorColumns": [ "STUDENT_ID", "COURSE_ID" ]
                }
              ]
            }
            """);

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.RecommendedExternalIdAttribute.Writability, Is.EqualTo(AttributeWritability.ReadOnly),
                "A composed anchor is JIM's own projection, so nothing can be written to it.");
            Assert.That(Writability(objectType, "STUDENT_ID"), Is.EqualTo(AttributeWritability.WritableOnCreate));
            Assert.That(Writability(objectType, "COURSE_ID"), Is.EqualTo(AttributeWritability.WritableOnCreate));
            Assert.That(Writability(objectType, "GRADE"), Is.EqualTo(AttributeWritability.Writable));
        }
    }

    [Test]
    public void GetSchemaAsync_AnAnchorColumnTheSourceDoesNotHave_ThrowsNamingBoth()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("STAFF_NUMBER", "int"));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("Person"));
            Assert.That(exception.Message, Does.Contain("EMPLOYEE_ID"));
        }
    }

    #endregion

    #region Related tables

    [Test]
    public async Task GetSchemaAsync_ARelatedTable_SurfacesItsValueColumnAsAMultiValuedAttributeAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "EMPLOYEE_PHONES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("PHONE_NUMBER", "nvarchar", MaxLength: 32));

        var schema = await GetSchemaAsync(provider, """
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
            """);

        var attribute = schema.ObjectTypes.Single().Attributes.Single(a => a.Name == "PhoneNumbers");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text), "The value column's own type decides the attribute's type.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_ARelatedTableReferencingAnObjectType_IsAMultiValuedReferenceAsync()
    {
        // Group membership is exactly this shape: a join table whose value column carries another object
        // type's anchor.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "GROUPS", new FakeCatalogueColumn("GROUP_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "GROUP_MEMBERS",
            new FakeCatalogueColumn("GROUP_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("MEMBER_EMPLOYEE_ID", "int", IsNullable: false));

        var schema = await GetSchemaAsync(provider, """
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
                      "valueColumn": "MEMBER_EMPLOYEE_ID",
                      "joinColumns": [ "GROUP_ID" ],
                      "referencesObjectType": "Person"
                    }
                  ]
                }
              ]
            }
            """);

        var members = schema.ObjectTypes.Single(o => o.Name == "Group").Attributes.Single(a => a.Name == "Members");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(members.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
        }
    }

    [Test]
    public void GetSchemaAsync_ARelatedTableWhoseJoinColumnIsNotInIt_ThrowsNamingIt()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "EMPLOYEE_PHONES",
            new FakeCatalogueColumn("STAFF_NUMBER", "int"),
            new FakeCatalogueColumn("PHONE_NUMBER", "nvarchar", MaxLength: 32));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, RelatedTableDocument("PHONE_NUMBER")));

        Assert.That(exception!.Message, Does.Contain("EMPLOYEE_ID"));
    }

    [Test]
    public void GetSchemaAsync_ARelatedTableWhoseValueColumnIsNotInIt_ThrowsNamingIt()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "EMPLOYEE_PHONES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int"),
            new FakeCatalogueColumn("NUMBER_TEXT", "nvarchar", MaxLength: 32));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, RelatedTableDocument("PHONE_NUMBER")));

        Assert.That(exception!.Message, Does.Contain("PHONE_NUMBER"));
    }

    [Test]
    public void GetSchemaAsync_ARelatedTableAttributeNameCollidingWithAColumn_ThrowsRatherThanShadowingIt()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("PhoneNumbers", "nvarchar", MaxLength: 32));
        provider.Catalogue.AddTable("HR", "EMPLOYEE_PHONES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int"),
            new FakeCatalogueColumn("PHONE_NUMBER", "nvarchar", MaxLength: 32));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, RelatedTableDocument("PHONE_NUMBER")));

        Assert.That(exception!.Message, Does.Contain("PhoneNumbers"));
    }

    #endregion

    #region References

    [Test]
    public async Task GetSchemaAsync_AConfiguredReferenceColumn_IsAReferenceAttributeAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("MANAGER_EMPLOYEE_ID", "int"));

        var schema = await GetSchemaAsync(provider, ManagerReferenceDocument());

        var manager = schema.ObjectTypes.Single().Attributes.Single(a => a.Name == "MANAGER_EMPLOYEE_ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manager.Type, Is.EqualTo(AttributeDataType.Reference),
                "A reference is explicit configuration; the column itself is an ordinary integer.");
            Assert.That(manager.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public void GetSchemaAsync_AConfiguredReferenceColumnTheSourceDoesNotHave_ThrowsNamingIt()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, ManagerReferenceDocument()));

        Assert.That(exception!.Message, Does.Contain("MANAGER_EMPLOYEE_ID"));
    }

    [Test]
    public async Task GetSchemaAsync_AForeignKeyMatchingAnotherObjectTypesAnchor_IsSurfacedAsASuggestionAsync()
    {
        // The suggestion is advice, not configuration: the column keeps the type its own SQL type maps
        // to, and the administrator confirms it by configuring the column as a Reference.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "DEPARTMENTS", new FakeCatalogueColumn("DEPARTMENT_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("DEPARTMENT_ID", "int"));
        provider.Catalogue.AddForeignKey("HR", "EMPLOYEES",
            new FakeCatalogueForeignKey("FK_EMPLOYEES_DEPARTMENTS", "DEPARTMENT_ID", "HR", "DEPARTMENTS", "DEPARTMENT_ID"));

        var schema = await GetSchemaAsync(provider, PersonAndDepartmentDocument());

        var department = schema.ObjectTypes.Single(o => o.Name == "Person").Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(department.Description, Is.Not.Null.And.Contains("Department"),
                "The suggestion names the object type the foreign key points at.");
            Assert.That(department.Description, Does.Contain("referencesObjectType"),
                "An administrator needs to know how to confirm it, not only that it exists.");
            Assert.That(department.Type, Is.EqualTo(AttributeDataType.Number),
                "Explicit configuration remains the source of truth, so a suggestion never changes a type on its own.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_AForeignKeyToATableNoObjectTypeIsConfiguredFor_IsNotSuggestedAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("COST_CENTRE_ID", "int"));
        provider.Catalogue.AddForeignKey("HR", "EMPLOYEES",
            new FakeCatalogueForeignKey("FK_EMPLOYEES_COST_CENTRES", "COST_CENTRE_ID", "HR", "COST_CENTRES", "COST_CENTRE_ID"));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        // Every attribute carries its source column type, so the absence of a suggestion is asserted on the
        // suggestion's own wording rather than on the Description being empty (#1354).
        Assert.That(schema.ObjectTypes.Single().Attributes.Single(a => a.Name == "COST_CENTRE_ID").Description, Does.Not.Contain("Foreign key"),
            "A foreign key to something JIM does not synchronise is not a Reference an administrator could confirm.");
    }

    [Test]
    public async Task GetSchemaAsync_AColumnAlreadyConfiguredAsAReference_CarriesNoSuggestionAsync()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "DEPARTMENTS", new FakeCatalogueColumn("DEPARTMENT_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("DEPARTMENT_ID", "int"));
        provider.Catalogue.AddForeignKey("HR", "EMPLOYEES",
            new FakeCatalogueForeignKey("FK_EMPLOYEES_DEPARTMENTS", "DEPARTMENT_ID", "HR", "DEPARTMENTS", "DEPARTMENT_ID"));

        var schema = await GetSchemaAsync(provider, PersonAndDepartmentDocument(configureTheReference: true));

        var department = schema.ObjectTypes.Single(o => o.Name == "Person").Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(department.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(department.Description, Does.Not.Contain("Foreign key"), "There is nothing left to suggest once the administrator has configured it.");
            Assert.That(department.Description, Does.Contain("Source column type: int"), "The source column type is stated regardless, so the administrator can see what the attribute was built from.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_AViewBackedObjectType_IsNotAskedForForeignKeysAsync()
    {
        // A view carries no constraint metadata, so asking for its foreign keys is a query that can only
        // ever return nothing.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddView("HR", "V_EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));

        await GetSchemaAsync(provider, ObjectTypesDocument("HR", "V_EMPLOYEES"));

        Assert.That(provider.ExecutedCommandTexts, Has.No.Member(provider.ForeignKeyColumnsCommandText));
    }

    #endregion

    #region Type mapping opt-ins

    [Test]
    public async Task GetSchemaAsync_OracleNumber1WithTheOptInOn_MapsToBooleanAsync()
    {
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("IS_ACTIVE", "NUMBER", Precision: 1, Scale: 0));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"), treatNumber1AsBoolean: true);

        Assert.That(AttributeType(schema.ObjectTypes.Single(), "IS_ACTIVE"), Is.EqualTo(AttributeDataType.Boolean));
    }

    [Test]
    public async Task GetSchemaAsync_OracleNumber1WithTheOptInOff_StaysNumericAsync()
    {
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("IS_ACTIVE", "NUMBER", Precision: 1, Scale: 0));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        Assert.That(AttributeType(schema.ObjectTypes.Single(), "IS_ACTIVE"), Is.EqualTo(AttributeDataType.Number),
            "Reinterpreting a number as a flag is never inferred, only opted into. Without the opt-in it stays numeric, and one digit with no scale is a whole number (#1354).");
    }

    [Test]
    public async Task GetSchemaAsync_EveryColumn_StatesItsSourceTypeInTheDescriptionAsync()
    {
        // The inferred type is only arguable if the administrator can see what it was inferred from,
        // which matters most on Oracle where the declaration is the whole signal (#1354).
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("FTE", "NUMBER", Precision: 9, Scale: 4));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));
        var objectType = schema.ObjectTypes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Attributes.Single(a => a.Name == "EMPLOYEE_ID").Description, Is.EqualTo("Source column type: NUMBER(10)."),
                "A zero scale is left off, because NUMBER(10) is how the column is written.");
            Assert.That(objectType.Attributes.Single(a => a.Name == "FTE").Description, Is.EqualTo("Source column type: NUMBER(9,4)."),
                "A scale that carries meaning is stated, because it is what makes the column fractional.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_OracleWholeNumberAnchor_MapsToLongNumberAsync()
    {
        // The ordinary Oracle sequence-backed primary key. Ten digits reach 9,999,999,999, which
        // overflows a 32-bit whole number, so LongNumber is the narrowest type that always holds it.
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("HEADCOUNT", "NUMBER", Precision: 19, Scale: 0),
            new FakeCatalogueColumn("FTE", "NUMBER", Precision: 9, Scale: 4));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));
        var objectType = schema.ObjectTypes.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeType(objectType, "EMPLOYEE_ID"), Is.EqualTo(AttributeDataType.LongNumber),
                "NUMBER(10,0) exceeds a 32-bit whole number but fits a 64-bit one.");
            Assert.That(AttributeType(objectType, "HEADCOUNT"), Is.EqualTo(AttributeDataType.Decimal),
                "NUMBER(19,0) straddles long.MaxValue, so narrowing it is not safe.");
            Assert.That(AttributeType(objectType, "FTE"), Is.EqualTo(AttributeDataType.Decimal),
                "NUMBER(9,4) is genuinely fractional.");
        }
    }

    [Test]
    public async Task GetSchemaAsync_OracleRaw16WithTheOptInOn_MapsToGuidAsync()
    {
        var provider = new FakeSqlProvider { DialectUnderTest = SqlDatabaseType.Oracle };
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "NUMBER", Precision: 10, Scale: 0, IsNullable: false),
            new FakeCatalogueColumn("EXTERNAL_UID", "RAW", MaxLength: 16),
            new FakeCatalogueColumn("PASSWORD_DIGEST", "RAW", MaxLength: 32));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"), treatRaw16AsGuid: true);

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeType(objectType, "EXTERNAL_UID"), Is.EqualTo(AttributeDataType.Guid));
            Assert.That(AttributeType(objectType, "PASSWORD_DIGEST"), Is.EqualTo(AttributeDataType.Binary),
                "Only exactly sixteen bytes can hold a GUID, whatever the opt-in says.");
        }
    }

    #endregion

    #region Unmappable columns

    [Test]
    public async Task GetSchemaAsync_AnUnmappableColumn_IsSkippedAndWarnedAboutAsync()
    {
        // Refusing the whole object type over a column nobody wants to synchronise (spatial data, XML,
        // an interval) would make the Connector unusable against ordinary line-of-business tables, and
        // the administrator's only remedy would be creating a view. Skipping silently is not an option
        // either: a Synchronisation Rule cannot flow an attribute that never appeared.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("WORKPLACE_LOCATION", "geography"));

        var schema = await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES"));

        var objectType = schema.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Attributes.Select(a => a.Name), Has.No.Member("WORKPLACE_LOCATION"));
            Assert.That(schema.Warnings, Has.Count.EqualTo(1));
            Assert.That(schema.Warnings[0], Does.Contain("Person").And.Contain("WORKPLACE_LOCATION").And.Contain("geography"));
        }
    }

    [Test]
    public void GetSchemaAsync_AnUnmappableAnchorColumn_ThrowsRatherThanSkippingIt()
    {
        // An anchor JIM cannot read is not a shortfall it can work around: every object of the type
        // would be unidentifiable.
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "geography", IsNullable: false));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, ObjectTypesDocument("HR", "EMPLOYEES")));

        Assert.That(exception!.Message, Does.Contain("EMPLOYEE_ID").And.Contains("geography"));
    }

    [Test]
    public void GetSchemaAsync_AnUnmappableRelatedValueColumn_ThrowsRatherThanSkippingIt()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES", new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false));
        provider.Catalogue.AddTable("HR", "EMPLOYEE_PHONES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int"),
            new FakeCatalogueColumn("PHONE_NUMBER", "geography"));

        var exception = Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () =>
            await GetSchemaAsync(provider, RelatedTableDocument("PHONE_NUMBER")));

        Assert.That(exception!.Message, Does.Contain("PhoneNumbers").Or.Contains("PHONE_NUMBER"));
    }

    [Test]
    public void GetSchemaAsync_AnUnmappableColumnConfiguredAsAReference_ThrowsRatherThanSkippingIt()
    {
        var provider = new FakeSqlProvider();
        provider.Catalogue.AddTable("HR", "EMPLOYEES",
            new FakeCatalogueColumn("EMPLOYEE_ID", "int", IsNullable: false),
            new FakeCatalogueColumn("MANAGER_EMPLOYEE_ID", "geography"));

        Assert.ThrowsAsync<SqlSchemaConfigurationException>(async () => await GetSchemaAsync(provider, ManagerReferenceDocument()));
    }

    #endregion

    #region Helpers

    private async Task<ConnectorSchema> GetSchemaAsync(
        FakeSqlProvider provider,
        string? objectTypesDocument,
        bool treatNumber1AsBoolean = false,
        bool treatRaw16AsGuid = false)
    {
        _connector.Dispose();
        _connector = new SqlConnector { ProviderFactory = _ => provider };

        var settingValues = SqlConnectorSettingValues.CreateSqlServer(_connector);
        SqlConnectorSettingValues.SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, objectTypesDocument);
        SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatNumber1AsBoolean, treatNumber1AsBoolean);
        SqlConnectorSettingValues.SetCheckbox(settingValues, SqlConnectorConstants.SettingTreatRaw16AsGuid, treatRaw16AsGuid);

        return await _connector.GetSchemaAsync(settingValues, _logger);
    }

    private static AttributeDataType AttributeType(ConnectorSchemaObjectType objectType, string attributeName) =>
        objectType.Attributes.Single(a => a.Name == attributeName).Type;

    private static AttributeWritability Writability(ConnectorSchemaObjectType objectType, string attributeName) =>
        objectType.Attributes.Single(a => a.Name == attributeName).Writability;

    private static string ObjectTypesDocument(string? schemaName, string tableName)
    {
        var schemaField = schemaName == null ? string.Empty : $"\"schema\": \"{schemaName}\",";
        return $$"""
            {
              "objectTypes": [
                { "name": "Person", {{schemaField}} "table": "{{tableName}}", "anchorColumns": [ "EMPLOYEE_ID" ] }
              ]
            }
            """;
    }

    private static string RelatedTableDocument(string valueColumn) => $$"""
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
                  "valueColumn": "{{valueColumn}}",
                  "joinColumns": [ "EMPLOYEE_ID" ]
                }
              ]
            }
          ]
        }
        """;

    private static string ManagerReferenceDocument() => """
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

    private static string PersonAndDepartmentDocument(bool configureTheReference = false)
    {
        var columns = configureTheReference
            ? """, "columns": [ { "name": "DEPARTMENT_ID", "referencesObjectType": "Department" } ]"""
            : string.Empty;

        return $$"""
            {
              "objectTypes": [
                { "name": "Person", "schema": "HR", "table": "EMPLOYEES", "anchorColumns": [ "EMPLOYEE_ID" ]{{columns}} },
                { "name": "Department", "schema": "HR", "table": "DEPARTMENTS", "anchorColumns": [ "DEPARTMENT_ID" ] }
              ]
            }
            """;
    }

    #endregion
}
