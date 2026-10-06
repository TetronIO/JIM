// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for <c>GetSyncRuleScopeStateAsync</c>, the read that decides whether saving an export
/// Synchronisation Rule flags its Metaverse Objects for export scope review (#1925). The portal editor and the REST
/// controllers load the rule tracked and edit it in memory before calling the save, so the read must return the rule
/// as stored, and must leave the caller's edited graph exactly as it was. Both contexts here track, the stricter case:
/// on a tracking context an ordinary load resolves to the caller's edited instances.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleScopeStateDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Synchronisation Rule scope state tests.");

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
        .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [Test]
    public async Task GetSyncRuleScopeStateAsync_CallerHoldsAnEditedTrackedCopy_ReturnsTheRuleAsStoredAsync()
    {
        var seeded = await SeedRuleAsync();
        SyncRuleScopingProposal storedScope;
        await using (var readCtx = NewContext())
        {
            var stored = await new PostgresDataRepository(readCtx).ConnectedSystems.GetSyncRuleAsync(seeded.SyncRuleId);
            storedScope = SyncRuleScopingProposal.FromCurrentScope(stored!);
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var edited = await repository.ConnectedSystems.GetSyncRuleAsync(seeded.SyncRuleId);
        Assert.That(edited, Is.Not.Null);
        var editedRule = edited!;
        editedRule.Enabled = false;
        editedRule.ProvisionToConnectedSystem = true;
        var topGroup = editedRule.ObjectScopingCriteriaGroups.Single();
        topGroup.ChildGroups.Single().Criteria.Single().StringValue = "Sales";
        topGroup.Criteria.Clear();

        var state = await repository.ConnectedSystems.GetSyncRuleScopeStateAsync(seeded.SyncRuleId);

        Assert.That(state, Is.Not.Null);
        var stateNotNull = state!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateNotNull.Enabled, Is.True, "as stored, not as edited");
            Assert.That(stateNotNull.ProvisionToConnectedSystem, Is.False, "as stored, not as edited");
            Assert.That(stateNotNull.Scope.DescribesSameScopeAs(storedScope), Is.True, "the nested criteria as stored, at every depth");
            Assert.That(stateNotNull.Scope.DescribesSameScopeAs(SyncRuleScopingProposal.FromCurrentScope(editedRule)), Is.False);

            // The caller's edited graph is exactly as the caller left it: no stored group re-parented onto it, no edit undone.
            Assert.That(editedRule.ObjectScopingCriteriaGroups, Has.Count.EqualTo(1));
            Assert.That(topGroup.Criteria, Is.Empty);
            Assert.That(topGroup.ChildGroups, Has.Count.EqualTo(1));
            Assert.That(topGroup.ChildGroups.Single().Criteria.Single().StringValue, Is.EqualTo("Sales"));
        }
    }

    [Test]
    public async Task GetSyncRuleScopeStateAsync_RuleDoesNotExist_ReturnsNullAsync()
    {
        await using var ctx = NewContext();
        Assert.That(await new PostgresDataRepository(ctx).ConnectedSystems.GetSyncRuleScopeStateAsync(int.MaxValue), Is.Null);
    }

    private sealed record SeededRule(int SyncRuleId);

    /// <summary>
    /// An enabled, non-provisioning export rule scoped to Type = Employee AND (Department = Finance), the second
    /// criterion in a nested group, so the read has to walk below the top level.
    /// </summary>
    private async Task<SeededRule> SeedRuleAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"scope-state-def-{suffix}" };
        var system = new ConnectedSystem { Name = $"scope-state-system-{suffix}", ConnectorDefinition = definition };
        var objectType = new ConnectedSystemObjectType { ConnectedSystem = system, Name = "user", Selected = true };
        var metaverseObjectType = new MetaverseObjectType { Name = $"scope-state-type-{suffix}", PluralName = $"scope-state-types-{suffix}" };
        var type = new MetaverseAttribute { Name = $"type-{suffix}", Type = AttributeDataType.Text };
        var department = new MetaverseAttribute { Name = $"department-{suffix}", Type = AttributeDataType.Text };
        ctx.AddRange(definition, system, objectType, metaverseObjectType, type, department);
        await ctx.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = $"scope-state-rule-{suffix}",
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ProvisionToConnectedSystem = false,
            ObjectScopingCriteriaGroups =
            [
                new SyncRuleScopingCriteriaGroup
                {
                    Type = SearchGroupType.All,
                    Criteria = [new SyncRuleScopingCriteria { MetaverseAttributeId = type.Id, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee" }],
                    ChildGroups =
                    [
                        new SyncRuleScopingCriteriaGroup
                        {
                            Type = SearchGroupType.Any,
                            Criteria = [new SyncRuleScopingCriteria { MetaverseAttributeId = department.Id, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }]
                        }
                    ]
                }
            ]
        };
        ctx.SyncRules.Add(rule);
        await ctx.SaveChangesAsync();

        return new SeededRule(rule.Id);
    }
}
