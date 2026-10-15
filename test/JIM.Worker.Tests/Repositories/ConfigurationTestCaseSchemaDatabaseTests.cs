// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the configuration test case table (#2055 Phase 4): the check constraint that a case
/// holds exactly the one reference its kind covers, and the cascades that delete a case with what it covers. The
/// in-memory provider enforces neither, so only a real database can show them.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationTestCaseSchemaDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration test case schema tests.");

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

    // ---- The check constraint ----

    [TestCase(ConfigurationTestCaseKind.AttributeFlow)]
    [TestCase(ConfigurationTestCaseKind.GeneratedValue)]
    [TestCase(ConfigurationTestCaseKind.Scope)]
    [TestCase(ConfigurationTestCaseKind.ObjectMatchingRule)]
    public async Task CheckConstraint_CaseHoldingTheReferenceItsKindCovers_SavesAsync(ConfigurationTestCaseKind kind)
    {
        var estate = await SeedEstateAsync();

        await using var ctx = NewContext();
        ctx.ConfigurationTestCases.Add(Case(kind, estate));

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing);
    }

    [TestCase(ConfigurationTestCaseKind.AttributeFlow, ConfigurationTestCaseCoverage.SynchronisationRule)]
    [TestCase(ConfigurationTestCaseKind.GeneratedValue, ConfigurationTestCaseCoverage.ObjectMatchingRule)]
    [TestCase(ConfigurationTestCaseKind.Scope, ConfigurationTestCaseCoverage.AttributeFlow)]
    [TestCase(ConfigurationTestCaseKind.ObjectMatchingRule, ConfigurationTestCaseCoverage.SynchronisationRule)]
    public async Task CheckConstraint_CaseHoldingAnotherKindsReference_IsRejectedAsync(ConfigurationTestCaseKind kind, ConfigurationTestCaseCoverage held)
    {
        var estate = await SeedEstateAsync();
        var testCase = Case(kind, estate);
        testCase.SyncRuleMappingId = held == ConfigurationTestCaseCoverage.AttributeFlow ? estate.MappingId : null;
        testCase.SyncRuleId = held == ConfigurationTestCaseCoverage.SynchronisationRule ? estate.SyncRuleId : null;
        testCase.ObjectMatchingRuleId = held == ConfigurationTestCaseCoverage.ObjectMatchingRule ? estate.ObjectMatchingRuleId : null;

        await using var ctx = NewContext();
        ctx.ConfigurationTestCases.Add(testCase);

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_ConfigurationTestCases_CoversWhatItsKindTests must reject a case holding another kind's reference");
    }

    [Test]
    public async Task CheckConstraint_CaseHoldingTwoReferences_IsRejectedAsync()
    {
        var estate = await SeedEstateAsync();
        var testCase = Case(ConfigurationTestCaseKind.AttributeFlow, estate);
        testCase.SyncRuleId = estate.SyncRuleId;

        await using var ctx = NewContext();
        ctx.ConfigurationTestCases.Add(testCase);

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_ConfigurationTestCases_CoversWhatItsKindTests must reject a case covering two things");
    }

    [Test]
    public async Task CheckConstraint_CaseHoldingNoReference_IsRejectedAsync()
    {
        await using var ctx = NewContext();
        ctx.ConfigurationTestCases.Add(new ConfigurationTestCase { Name = "covers nothing", Kind = ConfigurationTestCaseKind.Scope });

        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>(),
            "CK_ConfigurationTestCases_CoversWhatItsKindTests must reject a case covering nothing");
    }

    // ---- Cascades ----

    [Test]
    public async Task DeletingTheAttributeFlow_DeletesItsTestCasesAsync()
    {
        var estate = await SeedEstateAsync();
        var caseId = await AddAsync(Case(ConfigurationTestCaseKind.AttributeFlow, estate));

        await using (var ctx = NewContext())
            await ctx.SyncRuleMappings.Where(m => m.Id == estate.MappingId).ExecuteDeleteAsync();

        Assert.That(await ExistsAsync(caseId), Is.False);
    }

    [Test]
    public async Task DeletingTheSynchronisationRule_DeletesItsScopeAndAttributeFlowTestCasesAsync()
    {
        var estate = await SeedEstateAsync();
        var scopeCaseId = await AddAsync(Case(ConfigurationTestCaseKind.Scope, estate));
        var flowCaseId = await AddAsync(Case(ConfigurationTestCaseKind.AttributeFlow, estate));

        await using (var ctx = NewContext())
            await ctx.SyncRules.Where(r => r.Id == estate.SyncRuleId).ExecuteDeleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ExistsAsync(scopeCaseId), Is.False);
            Assert.That(await ExistsAsync(flowCaseId), Is.False);
        }
    }

    [Test]
    public async Task DeletingTheObjectMatchingRule_DeletesItsTestCasesAsync()
    {
        var estate = await SeedEstateAsync();
        var caseId = await AddAsync(Case(ConfigurationTestCaseKind.ObjectMatchingRule, estate));

        await using (var ctx = NewContext())
            await ctx.ObjectMatchingRules.Where(r => r.Id == estate.ObjectMatchingRuleId).ExecuteDeleteAsync();

        Assert.That(await ExistsAsync(caseId), Is.False);
    }

    [Test]
    public async Task InputsAndExpectation_RoundTripThroughJsonbAsync()
    {
        var estate = await SeedEstateAsync();
        var testCase = Case(ConfigurationTestCaseKind.AttributeFlow, estate);
        testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs", "Smith"])];
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["SMITH", "BLOGGS"]);
        var caseId = await AddAsync(testCase);

        await using var ctx = NewContext();
        var read = await ctx.ConfigurationTestCases.SingleAsync(c => c.Id == caseId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Inputs[0].Values, Is.EqualTo(new[] { "Bloggs", "Smith" }));
            Assert.That(read.Expectation.Values, Is.EqualTo(new[] { "SMITH", "BLOGGS" }));
        }
    }

    private async Task<int> AddAsync(ConfigurationTestCase testCase)
    {
        await using var ctx = NewContext();
        ctx.ConfigurationTestCases.Add(testCase);
        await ctx.SaveChangesAsync();
        return testCase.Id;
    }

    private async Task<bool> ExistsAsync(int caseId)
    {
        await using var ctx = NewContext();
        return await ctx.ConfigurationTestCases.AnyAsync(c => c.Id == caseId);
    }

    private static ConfigurationTestCase Case(ConfigurationTestCaseKind kind, Estate estate)
    {
        var covered = ConfigurationTestCase.CoveredReference(kind);
        return new ConfigurationTestCase
        {
            Name = $"{kind} case",
            Kind = kind,
            SyncRuleMappingId = covered == ConfigurationTestCaseCoverage.AttributeFlow ? estate.MappingId : null,
            SyncRuleId = covered == ConfigurationTestCaseCoverage.SynchronisationRule ? estate.SyncRuleId : null,
            ObjectMatchingRuleId = covered == ConfigurationTestCaseCoverage.ObjectMatchingRule ? estate.ObjectMatchingRuleId : null,
            Expectation = new ConfigurationTestExpectation(kind == ConfigurationTestCaseKind.Scope
                ? ConfigurationTestExpectationKind.InScope
                : ConfigurationTestExpectationKind.AnyValue)
        };
    }

    /// <summary>
    /// A Connected System with an import Synchronisation Rule, one Attribute Flow and one Object Matching Rule, all
    /// named uniquely so tests never collide.
    /// </summary>
    private async Task<Estate> SeedEstateAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = $"Person-{suffix}", PluralName = $"People-{suffix}" };
        var mvAttribute = new MetaverseAttribute { Name = $"Surname-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.Add(mvAttribute);

        var connectorDefinition = new ConnectorDefinition { Name = $"def-{suffix}" };
        var system = new ConnectedSystem { Name = $"system-{suffix}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"import-{suffix}", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.Add(syncRule);
        await ctx.SaveChangesAsync();

        var mapping = new SyncRuleMapping { SyncRuleId = syncRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        var matchingRule = new ObjectMatchingRule { SyncRuleId = syncRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        ctx.SyncRuleMappings.Add(mapping);
        ctx.ObjectMatchingRules.Add(matchingRule);
        await ctx.SaveChangesAsync();

        return new Estate(syncRule.Id, mapping.Id, matchingRule.Id);
    }

    private sealed record Estate(int SyncRuleId, int MappingId, int ObjectMatchingRuleId);
}
