// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for removing Scoping Criteria from a Synchronisation Rule. The portal and the REST API both
/// remove a group or a criterion by taking it out of its owner's collection and saving the rule. Each of those links
/// is optional (a nested group hangs off its parent rather than the rule, so the rule link cannot be required), and
/// EF Core's answer to severing an optional link is to null it, not to delete the row. The group or criterion then
/// survived belonging to nothing, still referencing the Connected System attribute it compared, and the next attempt
/// to delete that Connected System failed with 23503 and rolled back: the system could never be deleted.
/// </summary>
/// <remarks>
/// <c>NoTracking</c>, matching JIM.Web, where the removals are made. The in-memory provider cannot see this: it
/// enforces no foreign keys, so the orphan blocks nothing there.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleScopingCriteriaRemovalDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Scoping Criteria removal tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [Test]
    public async Task UpdateSyncRuleAsync_TopLevelGroupRemoved_DeletesItWithItsCriteriaAndChildGroupsAsync()
    {
        var seeded = await SeedRuleAsync();

        await SaveAfterAsync(seeded.SyncRuleId, rule => rule.ObjectScopingCriteriaGroups.Remove(rule.ObjectScopingCriteriaGroups.Single(g => g.Id == seeded.RemovedGroupId)));

        await using var ctx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ctx.SyncRuleScopingCriteriaGroups.AnyAsync(g => g.Id == seeded.RemovedGroupId || g.Id == seeded.ChildGroupId), Is.False,
                "the removed group, and the group inside it, are deleted rather than left belonging to nothing");
            Assert.That(await ctx.SyncRuleScopingCriteria.AnyAsync(c => c.Id == seeded.RemovedGroupCriterionId || c.Id == seeded.ChildGroupCriterionId), Is.False);
            Assert.That(await ctx.SyncRuleScopingCriteria.AnyAsync(c => c.Id == seeded.KeptCriterionId), Is.True, "the rest of the rule's scope stays");
        }
    }

    [Test]
    public async Task UpdateSyncRuleAsync_NestedGroupRemoved_DeletesItWithItsCriteriaAsync()
    {
        var seeded = await SeedRuleAsync();

        await SaveAfterAsync(seeded.SyncRuleId, rule =>
        {
            var parent = rule.ObjectScopingCriteriaGroups.Single(g => g.Id == seeded.RemovedGroupId);
            parent.ChildGroups.Remove(parent.ChildGroups.Single(g => g.Id == seeded.ChildGroupId));
        });

        await using var ctx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ctx.SyncRuleScopingCriteriaGroups.AnyAsync(g => g.Id == seeded.ChildGroupId), Is.False);
            Assert.That(await ctx.SyncRuleScopingCriteria.AnyAsync(c => c.Id == seeded.ChildGroupCriterionId), Is.False);
            Assert.That(await ctx.SyncRuleScopingCriteriaGroups.AnyAsync(g => g.Id == seeded.RemovedGroupId), Is.True, "its parent stays");
        }
    }

    [Test]
    public async Task UpdateSyncRuleAsync_CriterionRemoved_DeletesItAsync()
    {
        var seeded = await SeedRuleAsync();

        await SaveAfterAsync(seeded.SyncRuleId, rule =>
        {
            var group = rule.ObjectScopingCriteriaGroups.Single(g => g.Id == seeded.RemovedGroupId);
            group.Criteria.Remove(group.Criteria.Single(c => c.Id == seeded.RemovedGroupCriterionId));
        });

        await using var ctx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ctx.SyncRuleScopingCriteria.AnyAsync(c => c.Id == seeded.RemovedGroupCriterionId), Is.False,
                "the removed criterion is deleted rather than left belonging to no group");
            Assert.That(await ctx.SyncRuleScopingCriteriaGroups.AnyAsync(g => g.Id == seeded.RemovedGroupId), Is.True, "its group stays");
        }
    }

    [Test]
    public async Task UpdateSyncRuleAsync_GroupRemoved_LeavesTheConnectedSystemDeletableAsync()
    {
        // The failure as an administrator met it: remove a group, later delete the Connected System.
        var seeded = await SeedRuleAsync();
        await SaveAfterAsync(seeded.SyncRuleId, rule => rule.ObjectScopingCriteriaGroups.Remove(rule.ObjectScopingCriteriaGroups.Single(g => g.Id == seeded.RemovedGroupId)));

        await using var deleteCtx = NewContext();
        var repository = new PostgresDataRepository(deleteCtx);

        Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(seeded.ConnectedSystemId), Throws.Nothing);
    }

    private async Task SaveAfterAsync(int syncRuleId, Action<SyncRule> change)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var rule = await repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        Assert.That(rule, Is.Not.Null);
        change(rule!);
        await repository.ConnectedSystems.UpdateSyncRuleAsync(rule!);
    }

    private sealed record SeededRule(int ConnectedSystemId, int SyncRuleId, int RemovedGroupId, int RemovedGroupCriterionId,
        int ChildGroupId, int ChildGroupCriterionId, int KeptCriterionId);

    /// <summary>
    /// An import Synchronisation Rule with two top-level Scoping Criteria groups, the first holding a group of its
    /// own, every criterion comparing the Connected System's own attribute, as an import rule's criteria do.
    /// </summary>
    private async Task<SeededRule> SeedRuleAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"scoping-removal-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"scoping-removal-system-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        await ctx.SaveChangesAsync();

        var attribute = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "employeeNumber", Type = AttributeDataType.Text };
        ctx.ConnectedSystemAttributes.Add(attribute);
        var metaverseObjectType = new MetaverseObjectType { Name = $"scoping-removal-type-{suffix}", PluralName = $"scoping-removal-types-{suffix}" };
        ctx.MetaverseObjectTypes.Add(metaverseObjectType);
        await ctx.SaveChangesAsync();

        SyncRuleScopingCriteria Criterion(string value) => new()
        {
            ConnectedSystemAttributeId = attribute.Id, ComparisonType = SearchComparisonType.NotEquals, StringValue = value
        };

        var childGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [Criterion("S14-9")] };
        var removedGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [Criterion("S14-4")], ChildGroups = [childGroup] };
        var keptGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [Criterion("S14-5")], Position = 1 };
        var rule = new SyncRule
        {
            Name = $"scoping-removal-rule-{suffix}",
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id,
            Direction = SyncRuleDirection.Import,
            ObjectScopingCriteriaGroups = [removedGroup, keptGroup]
        };
        ctx.SyncRules.Add(rule);
        await ctx.SaveChangesAsync();

        return new SeededRule(system.Id, rule.Id, removedGroup.Id, removedGroup.Criteria[0].Id, childGroup.Id, childGroup.Criteria[0].Id,
            keptGroup.Criteria[0].Id);
    }
}
