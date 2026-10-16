// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for <see cref="JIM.PostgresData.Repositories.ConfigurationTestRepository"/> (#2055 Phase 4):
/// which cases each read finds, through the joins from an Attribute Flow or an Object Matching Rule to its
/// Synchronisation Rule or Connected System, and the writes.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationTestRepositoryDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration test repository tests.");

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
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [Test]
    public async Task GetTestCasesForSyncRuleAsync_FindsItsScopeAttributeFlowAndOwnMatchingCasesOnlyAsync()
    {
        var estate = await SeedEstateAsync();
        var other = await SeedEstateAsync();

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConfigurationTests.GetTestCasesForSyncRuleAsync(estate.SyncRuleId);

        Assert.That(found.Select(c => c.Id), Is.EqualTo(new[] { estate.ScopeCaseId, estate.FlowCaseId, estate.RuleMatchingCaseId }));
        Assert.That(found.Select(c => c.Id), Has.None.AnyOf(other.ScopeCaseId, other.FlowCaseId, other.RuleMatchingCaseId));
    }

    [Test]
    public async Task GetSimpleModeMatchingTestCasesAsync_FindsOnlyTheConnectedSystemsSimpleModeCasesAsync()
    {
        var estate = await SeedEstateAsync();
        await SeedEstateAsync();

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConfigurationTests.GetSimpleModeMatchingTestCasesAsync(estate.ConnectedSystemId);

        Assert.That(found.Select(c => c.Id), Is.EqualTo(new[] { estate.SimpleMatchingCaseId }));
    }

    [Test]
    public async Task GetTestCasesForConnectedSystemAsync_FindsEveryCaseUnderItAsync()
    {
        var estate = await SeedEstateAsync();
        await SeedEstateAsync();

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConfigurationTests.GetTestCasesForConnectedSystemAsync(estate.ConnectedSystemId);

        Assert.That(found.Select(c => c.Id), Is.EqualTo(new[]
        {
            estate.ScopeCaseId, estate.FlowCaseId, estate.RuleMatchingCaseId, estate.SimpleMatchingCaseId
        }));
    }

    [Test]
    public async Task GetTestCasesAsync_ByIds_LeavesOutAnIdWithNoCaseAsync()
    {
        var estate = await SeedEstateAsync();

        await using var ctx = NewContext();
        var found = await new PostgresDataRepository(ctx).ConfigurationTests.GetTestCasesAsync([estate.FlowCaseId, int.MaxValue]);

        Assert.That(found.Select(c => c.Id), Is.EqualTo(new[] { estate.FlowCaseId }));
    }

    [Test]
    public async Task Reads_AreUntrackedAsync()
    {
        var estate = await SeedEstateAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.ConfigurationTests.GetTestCaseAsync(estate.FlowCaseId);
        await repository.ConfigurationTests.GetTestCasesForSyncRuleAsync(estate.SyncRuleId);

        Assert.That(ctx.ChangeTracker.Entries<ConfigurationTestCase>(), Is.Empty);
    }

    [Test]
    public async Task UpdateThenDeleteTestCaseAsync_ChangesThenRemovesTheRowAsync()
    {
        var estate = await SeedEstateAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var testCase = (await repository.ConfigurationTests.GetTestCaseAsync(estate.FlowCaseId))!;
            testCase.Name = "renamed";
            testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.NoValue);
            await repository.ConfigurationTests.UpdateTestCaseAsync(testCase);
        }

        await using (var ctx = NewContext())
        {
            var read = await ctx.ConfigurationTestCases.AsNoTracking().SingleAsync(c => c.Id == estate.FlowCaseId);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read.Name, Is.EqualTo("renamed"));
                Assert.That(read.Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.NoValue));
            }
            await new PostgresDataRepository(ctx).ConfigurationTests.DeleteTestCaseAsync(estate.FlowCaseId);
        }

        await using (var ctx = NewContext())
            Assert.That(await ctx.ConfigurationTestCases.AnyAsync(c => c.Id == estate.FlowCaseId), Is.False);
    }

    /// <summary>
    /// A Connected System with one import Synchronisation Rule holding an Attribute Flow and an Object Matching Rule,
    /// a Simple mode Object Matching Rule on its Object Type, and one test case covering each.
    /// </summary>
    private async Task<Estate> SeedEstateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var mvAttribute = new MetaverseAttribute { Name = $"Surname-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ctx.AddRange(mvoType, mvAttribute, connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.Add(syncRule);
        await ctx.SaveChangesAsync();

        var mapping = new SyncRuleMapping { SyncRuleId = syncRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        var ruleMatching = new ObjectMatchingRule { SyncRuleId = syncRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        var simpleMatching = new ObjectMatchingRule { ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        ctx.AddRange(mapping, ruleMatching, simpleMatching);
        await ctx.SaveChangesAsync();

        var scopeCase = Case(ConfigurationTestCaseKind.Scope, syncRuleId: syncRule.Id);
        var flowCase = Case(ConfigurationTestCaseKind.AttributeFlow, mappingId: mapping.Id);
        var ruleMatchingCase = Case(ConfigurationTestCaseKind.ObjectMatchingRule, matchingRuleId: ruleMatching.Id);
        var simpleMatchingCase = Case(ConfigurationTestCaseKind.ObjectMatchingRule, matchingRuleId: simpleMatching.Id);
        ctx.ConfigurationTestCases.AddRange(scopeCase, flowCase, ruleMatchingCase, simpleMatchingCase);
        await ctx.SaveChangesAsync();

        return new Estate(system.Id, syncRule.Id, scopeCase.Id, flowCase.Id, ruleMatchingCase.Id, simpleMatchingCase.Id);
    }

    private static ConfigurationTestCase Case(ConfigurationTestCaseKind kind, int? syncRuleId = null, int? mappingId = null, int? matchingRuleId = null) => new()
    {
        Name = $"{kind} case",
        Kind = kind,
        SyncRuleId = syncRuleId,
        SyncRuleMappingId = mappingId,
        ObjectMatchingRuleId = matchingRuleId,
        Expectation = new ConfigurationTestExpectation(kind == ConfigurationTestCaseKind.Scope
            ? ConfigurationTestExpectationKind.InScope
            : ConfigurationTestExpectationKind.AnyValue)
    };

    private sealed record Estate(int ConnectedSystemId, int SyncRuleId, int ScopeCaseId, int FlowCaseId, int RuleMatchingCaseId, int SimpleMatchingCaseId);
}
