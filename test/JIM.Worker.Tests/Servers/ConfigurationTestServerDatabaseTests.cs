// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for saved configuration test cases (#2055 Phase 4) through the application layer every
/// surface calls: create, update and delete with their checks, each recorded in the Configuration History of what owns
/// the case without its values, the cascade that removes cases with an Attribute Flow listed in that history, and runs
/// by Synchronisation Rule, Connected System or id. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationTestServerDatabaseTests
{
    private const string InputValue = "Bloggs-input-value";

    private string _connectionString = null!;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration test case tests.");

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
        TestUtilities.SetEnvironmentVariables();
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    #region Create, update, delete

    [Test]
    public async Task CreateTestCaseAsync_AttributeFlowCase_StoresItAndRecordsItInTheRulesHistoryWithoutItsValuesAsync()
    {
        var seed = await SeedAsync();

        int caseId;
        await using (var ctx = NewContext())
        {
            var testCase = FlowCase(seed);
            await NewJimApplication(ctx).ConfigurationTests.CreateTestCaseAsync(testCase, await LoadInitiatorAsync(seed));
            caseId = testCase.Id;
        }

        await using var verify = NewContext();
        var activity = await LatestActivityAsync(verify);
        var snapshot = ConfigurationSnapshotService.Deserialise(activity.ConfigurationChangeSnapshot)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConfigurationTestCases.AnyAsync(c => c.Id == caseId), Is.True);
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.SynchronisationRule));
            Assert.That(activity.SyncRuleId, Is.EqualTo(seed.RuleId));
            Assert.That(activity.TargetName, Is.EqualTo("Test case 'Surname flows'"));
            Assert.That(activity.ConfigurationChangeClass, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
            Assert.That(snapshot.Root.Children!.Single(c => c.Key == "testCases").Children!.Single().ItemId, Is.EqualTo(caseId));
            Assert.That(activity.ConfigurationChangeSnapshot, Does.Not.Contain(InputValue));
        }
    }

    [Test]
    public async Task CreateTestCaseAsync_InvalidCase_IsRefusedAndStoresNothingAsync()
    {
        var seed = await SeedAsync();
        var testCase = FlowCase(seed);
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope);

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var initiator = await LoadInitiatorAsync(seed);
            Assert.That(async () => await jim.ConfigurationTests.CreateTestCaseAsync(testCase, initiator),
                Throws.TypeOf<ArgumentException>().With.Message.Contains("InScope"));
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConfigurationTestCases.AnyAsync(), Is.False);
    }

    [Test]
    public async Task CreateTestCaseAsync_AttributeFlowThatDoesNotExist_IsRefusedAsync()
    {
        var seed = await SeedAsync();
        var testCase = FlowCase(seed);
        testCase.SyncRuleMappingId = int.MaxValue;

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var initiator = await LoadInitiatorAsync(seed);
        Assert.That(async () => await jim.ConfigurationTests.CreateTestCaseAsync(testCase, initiator),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("Attribute Flow"));
    }

    [Test]
    public async Task CreateTestCaseAsync_GeneratedValueCaseOnAnOrdinaryAttributeFlow_IsRefusedAsync()
    {
        var seed = await SeedAsync();
        var testCase = FlowCase(seed);
        testCase.Kind = ConfigurationTestCaseKind.GeneratedValue;

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var initiator = await LoadInitiatorAsync(seed);
        Assert.That(async () => await jim.ConfigurationTests.CreateTestCaseAsync(testCase, initiator),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("Generated Value"));
    }

    [Test]
    public async Task UpdateTestCaseAsync_ChangingWhatItCovers_IsRefusedAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var testCase = (await jim.ConfigurationTests.GetTestCaseAsync(caseId))!;
        testCase.Kind = ConfigurationTestCaseKind.Scope;
        testCase.SyncRuleMappingId = null;
        testCase.SyncRuleId = seed.RuleId;
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope);
        var initiator = await LoadInitiatorAsync(seed);

        Assert.That(async () => await jim.ConfigurationTests.UpdateTestCaseAsync(testCase, initiator),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("covers"));
    }

    [Test]
    public async Task UpdateTestCaseAsync_ChangingTheInputs_IsRecordedAsChangedWithoutTheValuesAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var testCase = (await jim.ConfigurationTests.GetTestCaseAsync(caseId))!;
            testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Smith-input-value"])];
            testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["SMITH-INPUT-VALUE"]);
            await jim.ConfigurationTests.UpdateTestCaseAsync(testCase, await LoadInitiatorAsync(seed));
        }

        await using var verify = NewContext();
        var activity = await LatestActivityAsync(verify);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
            Assert.That(activity.ConfigurationChangeClass, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
            Assert.That(activity.ConfigurationChangeSnapshot, Does.Not.Contain("Smith-input-value").And.Not.Contain("SMITH-INPUT-VALUE"));
            Assert.That((await verify.ConfigurationTestCases.SingleAsync(c => c.Id == caseId)).Inputs[0].Values, Is.EqualTo(new[] { "Smith-input-value" }));
        }
    }

    [Test]
    public async Task DeleteTestCaseAsync_RemovesItAndRecordsItAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            await jim.ConfigurationTests.DeleteTestCaseAsync((await jim.ConfigurationTests.GetTestCaseAsync(caseId))!, await LoadInitiatorAsync(seed));
        }

        await using var verify = NewContext();
        var activity = await LatestActivityAsync(verify);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConfigurationTestCases.AnyAsync(), Is.False);
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
            Assert.That(activity.TargetName, Is.EqualTo("Test case 'Surname flows'"));
            Assert.That(activity.ConfigurationChangeClass, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        }
    }

    [Test]
    public async Task RemovingAnAttributeFlow_ListsTheTestCasesTheCascadeRemovedInTheRulesHistoryAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;
            rule.AttributeFlowRules.RemoveAll(m => m.Id == seed.MappingId);
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
        }

        await using var verify = NewContext();
        var snapshots = await verify.Activities
            .Where(a => a.SyncRuleId == seed.RuleId && a.ConfigurationChangeSnapshot != null)
            .OrderByDescending(a => a.Created)
            .Select(a => a.ConfigurationChangeSnapshot)
            .Take(2)
            .ToListAsync();
        var diff = new ConfigurationDiffService().Diff(ConfigurationSnapshotService.Deserialise(snapshots[1]), ConfigurationSnapshotService.Deserialise(snapshots[0])!);

        var testCases = diff.Root!.Children!.Single(c => c.Key == "testCases");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConfigurationTestCases.AnyAsync(c => c.Id == caseId), Is.False);
            Assert.That(testCases.ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Removed));
            Assert.That(testCases.Children!.Single().ItemId, Is.EqualTo(caseId));
            Assert.That(testCases.Children!.Single().Label, Is.EqualTo("Surname flows"));
        }
    }

    #endregion

    #region Simple mode Object Matching Rules

    [Test]
    public async Task CreateTestCaseAsync_SimpleModeMatchingCase_IsRecordedInTheConnectedSystemsHistoryAsync()
    {
        var seed = await SeedAsync();

        await using (var ctx = NewContext())
            await NewJimApplication(ctx).ConfigurationTests.CreateTestCaseAsync(MatchingCase(seed), await LoadInitiatorAsync(seed));

        await using var verify = NewContext();
        var activity = await LatestActivityAsync(verify);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(seed.SystemId));
            Assert.That(ConfigurationSnapshotService.Deserialise(activity.ConfigurationChangeSnapshot)!.Root.Children!.Select(c => c.Key), Has.Member("testCases"));
        }
    }

    #endregion

    #region Run

    [Test]
    public async Task RunForSyncRuleAsync_RunsEachCaseCoveringTheRuleAsync()
    {
        var seed = await SeedAsync();
        var flowCaseId = await CreateAsync(seed, FlowCase(seed));
        var scopeCase = new ConfigurationTestCase
        {
            Name = "Everyone is in scope", Kind = ConfigurationTestCaseKind.Scope, SyncRuleId = seed.RuleId,
            Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope)
        };
        var scopeCaseId = await CreateAsync(seed, scopeCase);

        await using var ctx = NewContext();
        var results = await NewJimApplication(ctx).ConfigurationTests.RunForSyncRuleAsync(seed.RuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.TestCaseId), Is.EqualTo(new[] { flowCaseId, scopeCaseId }));
            Assert.That(results.Select(r => r.Status), Is.All.EqualTo(ConfigurationTestStatus.Passed), string.Join(" | ", results.Select(r => r.Summary)));
        }
    }

    [Test]
    public async Task RunForConnectedSystemAsync_IncludesItsSimpleModeMatchingCasesAsync()
    {
        var seed = await SeedAsync();
        var flowCaseId = await CreateAsync(seed, FlowCase(seed));
        var matchingCaseId = await CreateAsync(seed, MatchingCase(seed));

        await using var ctx = NewContext();
        var results = await NewJimApplication(ctx).ConfigurationTests.RunForConnectedSystemAsync(seed.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.TestCaseId), Is.EqualTo(new[] { flowCaseId, matchingCaseId }));
            Assert.That(results.Select(r => r.Status), Is.All.EqualTo(ConfigurationTestStatus.Passed), string.Join(" | ", results.Select(r => r.Summary)));
        }
    }

    [Test]
    public async Task RunByIdsAsync_AFailingCase_SaysWhatWasExpectedAndWhatCameAsync()
    {
        var seed = await SeedAsync();
        var testCase = FlowCase(seed);
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["bloggs"]);
        var caseId = await CreateAsync(seed, testCase);

        await using var ctx = NewContext();
        var result = (await NewJimApplication(ctx).ConfigurationTests.RunByIdsAsync([caseId])).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Actual!.Values, Is.EqualTo(new[] { InputValue.ToUpperInvariant() }));
        }
    }

    #endregion

    #region Helpers

    private record Seed(int SystemId, int RuleId, int MappingId, int MatchingRuleId, Guid InitiatorId);

    private static ConfigurationTestCase FlowCase(Seed seed) => new()
    {
        Name = "Surname flows",
        Kind = ConfigurationTestCaseKind.AttributeFlow,
        SyncRuleMappingId = seed.MappingId,
        Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", [InputValue])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, [InputValue.ToUpperInvariant()])
    };

    private static ConfigurationTestCase MatchingCase(Seed seed) => new()
    {
        Name = "Matches on surname",
        Kind = ConfigurationTestCaseKind.ObjectMatchingRule,
        ObjectMatchingRuleId = seed.MatchingRuleId,
        Inputs = [new ConfigurationTestInput(null, "sn", ["Bloggs"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["Bloggs"])
    };

    private async Task<int> CreateAsync(Seed seed, ConfigurationTestCase testCase)
    {
        await using var ctx = NewContext();
        await NewJimApplication(ctx).ConfigurationTests.CreateTestCaseAsync(testCase, await LoadInitiatorAsync(seed));
        return testCase.Id;
    }

    private static async Task<Activity> LatestActivityAsync(JimDbContext ctx) =>
        await ctx.Activities.OrderByDescending(a => a.Created).FirstAsync();

    /// <summary>
    /// A Connected System in Simple mode with an Object Matching Rule on its Object Type, and an import Synchronisation
    /// Rule with one Attribute Flow (<c>Upper(cs["sn"])</c> into Display Name).
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        int systemId, csTypeId, mvTypeId, displayNameId, snId;
        Guid initiatorId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var sn = new ConnectedSystemObjectTypeAttribute { Name = "sn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            csType.Attributes.Add(sn);
            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var displayName = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
            mvType.Attributes.Add(displayName);
            var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.MetaverseObjects.Add(initiator);
            await seed.SaveChangesAsync();
            (systemId, csTypeId, mvTypeId, displayNameId, snId, initiatorId) = (system.Id, csType.Id, mvType.Id, displayName.Id, sn.Id, initiator.Id);
        }

        int ruleId, mappingId;
        await using (var ctx = NewContext())
        {
            var rule = new SyncRule
            {
                Name = "Import People",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = await ctx.ConnectedSystems.SingleAsync(x => x.Id == systemId),
                ConnectedSystemObjectType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == csTypeId),
                MetaverseObjectType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == mvTypeId)
            };
            var mapping = new SyncRuleMapping { TargetMetaverseAttribute = await ctx.MetaverseAttributes.AsTracking().SingleAsync(x => x.Id == displayNameId) };
            mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Upper(cs[\"sn\"])" });
            rule.AttributeFlowRules.Add(mapping);

            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == initiatorId);
            Assert.That(await NewJimApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True);
            (ruleId, mappingId) = (rule.Id, mapping.Id);
        }

        int matchingRuleId;
        await using (var ctx = NewContext())
        {
            var matchingRule = new ObjectMatchingRule
            {
                ConnectedSystemObjectTypeId = csTypeId,
                MetaverseObjectTypeId = mvTypeId,
                TargetMetaverseAttributeId = displayNameId,
                Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = snId }]
            };
            ctx.ObjectMatchingRules.Add(matchingRule);
            await ctx.SaveChangesAsync();
            matchingRuleId = matchingRule.Id;
        }

        return new Seed(systemId, ruleId, mappingId, matchingRuleId, initiatorId);
    }

    private async Task<MetaverseObject> LoadInitiatorAsync(Seed seed)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == seed.InitiatorId);
    }

    private static JimApplication NewJimApplication(JimDbContext context)
    {
        var repository = new PostgresDataRepository(context);
        return new JimApplication(repository, syncRepository: repository.Sync);
    }

    #endregion
}
