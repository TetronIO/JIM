// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Metaverse Attribute that configuration reads cannot be deleted, unassigned or renamed out from under it (#2265).
/// A Scoping Criterion left reading nothing takes every object out of scope, which deprovisions them; an Expression
/// left reading a name that no longer exists evaluates against nothing. Real PostgreSQL, because the readers are
/// found by a projection across nested Scoping Criteria groups that the in-memory provider would not exercise.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseAttributeReaderRefusalDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute reader tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_NestedScopingCriterionComparingIt_RefusesNamingTheRuleAsync()
    {
        var seeded = await SeedAsync(rule => rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            ChildGroups =
            [
                new SyncRuleScopingCriteriaGroup
                {
                    Type = SearchGroupType.Any,
                    Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = rule.MetaverseObjectType!.Attributes.Single(a => a.Name == "Department"), ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }]
                }
            ]
        }));

        AttributeDeletionImpact impact;
        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(seeded.DepartmentId);
            impact = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.Deleted, Is.False);
            Assert.That(impact.BlockedByReaders, Is.True);
            Assert.That(impact.Readers.Single().Description, Does.Contain("Export users").And.Contain("Department"));
        }
        await AssertUntouchedAsync(seeded.DepartmentId);
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_EveryExpressionKindReadingIt_RefusesNamingEachAsync()
    {
        var seeded = await SeedAsync(rule =>
        {
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = [new SyncRuleScopingCriteria { Expression = "Lower(mv[\"department\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.StartsWith, StringValue = "fin" }]
            });
            var description = rule.ConnectedSystemObjectType!.Attributes.Single();
            var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = description };
            mapping.Sources.Add(new SyncRuleMappingSource { Expression = "\"Dept: \" + mv[\"Department\"]", Order = 0 });
            rule.AttributeFlowRules.Add(mapping);
            var matching = new ObjectMatchingRule { TargetMetaverseAttribute = rule.MetaverseObjectType!.Attributes.Single(a => a.Name == "Title") };
            matching.Sources.Add(new ObjectMatchingRuleSource { Expression = "mv[\"DEPARTMENT\"]", Order = 0 });
            rule.ObjectMatchingRules.Add(matching);
        }, withTemplateExpression: "mv[\"Department\"] + \" team\"");

        AttributeDeletionImpact impact;
        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(seeded.DepartmentId);
            impact = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.Deleted, Is.False);
            Assert.That(impact.Readers.Select(r => r.Kind), Is.EquivalentTo(new[]
            {
                AttributeReaderKind.ScopingCriterion,
                AttributeReaderKind.AttributeFlowExpression,
                AttributeReaderKind.ObjectMatchingRuleExpression,
                AttributeReaderKind.ExampleDataTemplateExpression
            }));
            Assert.That(impact.Readers.Single(r => r.Kind == AttributeReaderKind.ExampleDataTemplateExpression).Description, Does.Contain("Demo people"));
        }
        await AssertUntouchedAsync(seeded.DepartmentId);
    }

    [Test]
    public async Task RenameMetaverseAttributeAsync_ExpressionReadsTheOldName_RefusesNamingTheReaderAsync()
    {
        var seeded = await SeedAsync(rule => rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { Expression = "Lower(mv[\"Department\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.StartsWith, StringValue = "fin" }]
        }));

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var refusal = Assert.ThrowsAsync<AttributeReadByConfigurationException>(() =>
                jim.Metaverse.RenameMetaverseAttributeAsync(seeded.DepartmentId, "Division", TestUtilities.GetInitiatedBy()));
            Assert.That(refusal!.Message, Does.Contain("Export users").And.Contain("Lower(mv[\"Department\"])"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.MetaverseAttributes.SingleAsync(a => a.Id == seeded.DepartmentId)).Name, Is.EqualTo("Department"));
    }

    [Test]
    public async Task RenameMetaverseAttributeAsync_OnlyADirectComparisonOrACaseChange_RenamesAsync()
    {
        var seeded = await SeedAsync(rule => rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria { MetaverseAttribute = rule.MetaverseObjectType!.Attributes.Single(a => a.Name == "Department"), ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" },
                new SyncRuleScopingCriteria { Expression = "Lower(mv[\"Department\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.StartsWith, StringValue = "fin" }
            ]
        }));

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            // Expressions ignore case, so a case-only rename leaves every reader reading it.
            await jim.Metaverse.RenameMetaverseAttributeAsync(seeded.DepartmentId, "department", TestUtilities.GetInitiatedBy());
        }

        await using var verify = NewContext();
        Assert.That((await verify.MetaverseAttributes.SingleAsync(a => a.Id == seeded.DepartmentId)).Name, Is.EqualTo("department"));
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_ReaderOnThatType_RefusesButAnotherTypeProceedsAsync()
    {
        var seeded = await SeedAsync(rule => rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria { Expression = "Lower(mv[\"Department\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.StartsWith, StringValue = "fin" }]
        }));

        await using var act = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(act));

        var refused = await jim.Metaverse.UnassignAttributeFromObjectTypeAsync(seeded.DepartmentId, seeded.PersonTypeId, TestUtilities.GetInitiatedBy());
        var allowed = await jim.Metaverse.UnassignAttributeFromObjectTypeAsync(seeded.DepartmentId, seeded.GroupTypeId, TestUtilities.GetInitiatedBy());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.Unassigned, Is.False);
            Assert.That(refused.BlockedByReaders, Is.True);
            Assert.That(refused.Readers.Single().SyncRuleName, Is.EqualTo("Export users"));
            Assert.That(allowed.Unassigned, Is.True, "no reader evaluates against Group");
        }
    }

    private async Task AssertUntouchedAsync(int departmentId)
    {
        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == departmentId), Is.True, "the attribute is kept");
            Assert.That(await verify.SyncRuleScopingCriteria.CountAsync(), Is.EqualTo(1), "the Scoping Criterion is kept, not cascade-removed");
            Assert.That(await verify.MetaverseObjectTypes.Where(t => t.Attributes.Any(a => a.Id == departmentId)).CountAsync(), Is.EqualTo(2), "both bindings are kept");
        }
    }

    private sealed record Seeded(int DepartmentId, int PersonTypeId, int GroupTypeId);

    private async Task<Seeded> SeedAsync(Action<SyncRule> configure, string? withTemplateExpression = null)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        csType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true });

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var title = new MetaverseAttribute { Name = "Title", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        personType.Attributes.Add(department);
        personType.Attributes.Add(title);
        groupType.Attributes.Add(department);

        var rule = new SyncRule
        {
            Name = "Export users",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = personType
        };
        configure(rule);

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.MetaverseObjectTypes.AddRange(personType, groupType);
        seed.SyncRules.Add(rule);

        if (withTemplateExpression != null)
        {
            var template = new ExampleDataTemplate { Name = "Demo people" };
            var objectType = new ExampleDataObjectType { MetaverseObjectType = personType, ObjectsToCreate = 1 };
            objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute { MetaverseAttribute = title, Expression = withTemplateExpression });
            template.ObjectTypes.Add(objectType);
            seed.ExampleDataTemplates.Add(template);
        }

        await seed.SaveChangesAsync();
        return new Seeded(department.Id, personType.Id, groupType.Id);
    }
}
