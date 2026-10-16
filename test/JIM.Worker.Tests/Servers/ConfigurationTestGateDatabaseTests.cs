// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
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
/// Real-PostgreSQL cover for the save gate (#2055 Phase 4b) on every path a save of covered configuration takes: the
/// whole Synchronisation Rule save (portal, REST and every Scoping Criteria endpoint), an Attribute Flow's settings, and
/// an Object Matching Rule. A save that stops a passing case is refused and saves nothing, unless the new result is
/// accepted (the case is rewritten in the same Configuration History version) or the save is made anyway (recorded with
/// the change). Disabling, removing and deleting always save, whatever their cases give. Opt-in via
/// <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationTestGateDatabaseTests
{
    private const string InputValue = "Bloggs-input-value";
    private const string CaseName = "Surname flows";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL save gate tests.");

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

    #region Whole Synchronisation Rule save

    [Test]
    public async Task WholeRuleSave_StopsACasePassing_IsRefusedAndSavesNothingAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, FlowCase(seed));
        var activitiesBefore = await CountActivitiesAsync();

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await LoadRuleAsync(jim, seed);
            rule.AttributeFlowRules.Single().Sources[0].Expression = "Lower(cs[\"sn\"])";
            var initiator = await LoadInitiatorAsync(seed);

            var refused = Assert.ThrowsAsync<ConfigurationTestsRegressedException>(async () =>
                await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator))!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refused.Regressions.Single().TestCase.Name, Is.EqualTo(CaseName));
                Assert.That(refused.Regressions.Single().CanAccept, Is.True);
                Assert.That(refused.Message, Does.Contain(CaseName).And.Not.Contain(InputValue));
            }
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StoredExpressionAsync(verify, seed), Is.EqualTo("Upper(cs[\"sn\"])"));
            Assert.That(await CountActivitiesAsync(), Is.EqualTo(activitiesBefore));
        }
    }

    [Test]
    public async Task WholeRuleSave_SaveAnyway_SavesAndRecordsTheCaseItSavedPastWithoutItsValuesAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await LoadRuleAsync(jim, seed);
            rule.AttributeFlowRules.Single().Sources[0].Expression = "Lower(cs[\"sn\"])";
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed),
                configurationTestGate: new ConfigurationTestGateOptions(SaveAnyway: true)), Is.True);
        }

        await using var verify = NewContext();
        var activity = await LatestActivityAsync(verify);
        var storedCase = await verify.ConfigurationTestCases.SingleAsync(c => c.Id == caseId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StoredExpressionAsync(verify, seed), Is.EqualTo("Lower(cs[\"sn\"])"));
            Assert.That(activity.ConfigurationTestGateNote, Does.Contain("Saved past").And.Contain($"'{CaseName}' (failed)"));
            Assert.That(activity.ConfigurationTestGateNote, Does.Not.Contain(InputValue).And.Not.Contain(InputValue.ToLowerInvariant()));
            Assert.That(storedCase.Expectation.Values, Is.EqualTo(new[] { InputValue.ToUpperInvariant() }), "Saving anyway leaves the case as it was.");
        }

        // Configuration History shows it with the change, on every surface that lists the history.
        await using var historyContext = NewContext();
        var history = await NewJimApplication(historyContext).ChangeHistory
            .GetConfigurationChangeHistoryAsync(ActivityTargetType.SynchronisationRule, seed.RuleId);
        Assert.That(history.Results[0].ConfigurationTestGateNote, Is.EqualTo(activity.ConfigurationTestGateNote));
    }

    [Test]
    public async Task WholeRuleSave_AcceptNewResults_RewritesTheCaseInTheSameHistoryVersionAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));
        var otherCase = FlowCase(seed);
        otherCase.Name = "Already failing";
        otherCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["something else"]);
        var otherCaseId = await CreateAsync(seed, otherCase);

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await LoadRuleAsync(jim, seed);
            rule.AttributeFlowRules.Single().Sources[0].Expression = "Lower(cs[\"sn\"])";
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed),
                configurationTestGate: new ConfigurationTestGateOptions(AcceptNewResults: true)), Is.True);
        }

        await using var verify = NewContext();
        var activity = await LatestActivityAsync(verify);
        var diff = await LatestRuleDiffAsync(verify, seed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await verify.ConfigurationTestCases.SingleAsync(c => c.Id == caseId)).Expectation.Values,
                Is.EqualTo(new[] { InputValue.ToLowerInvariant() }));
            Assert.That((await verify.ConfigurationTestCases.SingleAsync(c => c.Id == otherCaseId)).Expectation.Values,
                Is.EqualTo(new[] { "something else" }), "Only a case that stopped passing in this save is rewritten.");
            Assert.That(activity.ConfigurationTestGateNote, Does.Contain($"Accepted the new result as expected for 1 configuration test case: '{CaseName}'"));
            Assert.That(activity.ConfigurationTestGateNote, Does.Not.Contain("Already failing"));
            Assert.That(diff.Root!.Children!.Single(c => c.Key == "testCases").ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Modified));
        }

        // The accepted expectation is what the configuration now gives, so the case passes.
        await using var runContext = NewContext();
        var result = (await NewJimApplication(runContext).ConfigurationTests.RunByIdsAsync([caseId])).Single();
        Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
    }

    [Test]
    public async Task WholeRuleSave_AcceptNewResultsWhenTheCaseNowNeedsReview_IsStillRefusedAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, FlowCase(seed));

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var rule = await LoadRuleAsync(jim, seed);
        // Reads an input the case does not give: the case needs editing, which accepting cannot do.
        rule.AttributeFlowRules.Single().Sources[0].Expression = "cs[\"sn\"] + cs[\"givenName\"]";
        var initiator = await LoadInitiatorAsync(seed);

        var refused = Assert.ThrowsAsync<ConfigurationTestsRegressedException>(async () =>
            await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator,
                configurationTestGate: new ConfigurationTestGateOptions(AcceptNewResults: true)))!;
        Assert.That(refused.Regressions.Single().After.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
    }

    [Test]
    public async Task WholeRuleSave_CaseThatReadsTheClock_RunsBothSidesAsOfOneInstantAsync()
    {
        var seed = await SeedAsync();
        var instant = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await LoadRuleAsync(jim, seed);
            rule.AttributeFlowRules.Single().Sources[0].Expression = "FormatDate(Now(), \"yyyy-MM-dd HH:mm:ss.fffffff\")";
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
        }

        var testCase = FlowCase(seed);
        testCase.Inputs = [];
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["2026-11-10 09:00:00.0000000"]);
        await CreateAsync(seed, testCase);

        await using var saveContext = NewContext();
        var saving = NewJimApplication(saveContext);
        // A clock that moves on every read: were the two runs to read it separately, the second would see another
        // instant and the case would stop passing with nothing it covers changed.
        var reads = 0;
        saving.ConfigurationTests.UtcNow = () => instant.AddMinutes(reads++);
        var edited = await LoadRuleAsync(saving, seed);
        edited.Description = "Edited";

        Assert.That(await saving.ConnectedSystems.CreateOrUpdateSyncRuleAsync(edited, await LoadInitiatorAsync(seed)), Is.True);
        Assert.That(reads, Is.EqualTo(1));
    }

    #endregion

    #region Never a lockout

    [Test]
    public async Task WholeRuleSave_CaseAlreadyFailingBeforeTheSave_NeverStopsItAsync()
    {
        var seed = await SeedAsync();
        var testCase = FlowCase(seed);
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["something else"]);
        await CreateAsync(seed, testCase);

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var rule = await LoadRuleAsync(jim, seed);
        rule.AttributeFlowRules.Single().Sources[0].Expression = "Lower(cs[\"sn\"])";

        Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
    }

    [Test]
    public async Task DisablingTheSynchronisationRule_AlwaysSavesAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, FlowCase(seed));

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var rule = await LoadRuleAsync(jim, seed);
        rule.Enabled = false;

        Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
        await using var verify = NewContext();
        Assert.That((await verify.SyncRules.SingleAsync(r => r.Id == seed.RuleId)).Enabled, Is.False);
    }

    [Test]
    public async Task RemovingTheAttributeFlowACaseCovers_AlwaysSavesAndTakesTheCaseAsync()
    {
        var seed = await SeedAsync();
        var caseId = await CreateAsync(seed, FlowCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await LoadRuleAsync(jim, seed);
            rule.AttributeFlowRules.Clear();
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(seed)), Is.True);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConfigurationTestCases.AnyAsync(c => c.Id == caseId), Is.False);
    }

    [Test]
    public async Task DisablingTheAttributeFlow_AlwaysSavesAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, FlowCase(seed));

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);

        Assert.That(await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(seed.MappingId,
            new SyncRuleMappingSettingsUpdate { Enabled = false }, await LoadInitiatorAsync(seed)), Is.Not.Null);
    }

    [Test]
    public async Task DeletingTheSynchronisationRule_AlwaysSucceedsAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, FlowCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await LoadRuleAsync(jim, seed);
            await jim.ConnectedSystems.DeleteSyncRuleAsync(rule, await LoadInitiatorAsync(seed));
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.SyncRules.AnyAsync(r => r.Id == seed.RuleId), Is.False);
            Assert.That(await verify.ConfigurationTestCases.AnyAsync(), Is.False);
        }
    }

    #endregion

    #region Attribute Flow settings

    [Test]
    public async Task AttributeFlowSettingsUpdate_StopsACasePassing_IsRefusedUnlessSavedAnywayAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, FlowCase(seed));
        var change = new SyncRuleMappingSettingsUpdate { Expression = "Lower(cs[\"sn\"])" };

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var initiator = await LoadInitiatorAsync(seed);
            Assert.That(async () => await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(seed.MappingId, change, initiator),
                Throws.TypeOf<ConfigurationTestsRegressedException>());
        }

        await using (var verify = NewContext())
            Assert.That(await StoredExpressionAsync(verify, seed), Is.EqualTo("Upper(cs[\"sn\"])"));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            Assert.That(await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(seed.MappingId, change, await LoadInitiatorAsync(seed),
                new ConfigurationTestGateOptions(SaveAnyway: true)), Is.Not.Null);
        }

        await using var after = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StoredExpressionAsync(after, seed), Is.EqualTo("Lower(cs[\"sn\"])"));
            Assert.That((await LatestActivityAsync(after)).ConfigurationTestGateNote, Does.Contain(CaseName));
        }
    }

    #endregion

    #region Object Matching Rules

    [Test]
    public async Task SimpleModeObjectMatchingRuleUpdate_StopsACasePassing_IsRefusedUnlessSavedAnywayAsync()
    {
        var seed = await SeedAsync();
        await CreateAsync(seed, MatchingCase(seed));

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await ChangedMatchingRuleAsync(jim, seed.MatchingRuleId);
            var initiator = await LoadInitiatorAsync(seed);
            Assert.That(async () => await jim.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, initiator),
                Throws.TypeOf<ConfigurationTestsRegressedException>());
        }

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await ChangedMatchingRuleAsync(jim, seed.MatchingRuleId);
            await jim.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, await LoadInitiatorAsync(seed), new ConfigurationTestGateOptions(SaveAnyway: true));
        }

        await using var verify = NewContext();
        Assert.That((await LatestActivityAsync(verify)).ConfigurationTestGateNote, Does.Contain("'Matches on surname' (failed)"));
    }

    [Test]
    public async Task SynchronisationRuleModeObjectMatchingRule_CaseRunsAndItsChangeIsGatedAsync()
    {
        var seed = await SeedAsync(advancedMatching: true);
        var matchingCaseId = await CreateAsync(seed, MatchingCase(seed));

        await using (var ctx = NewContext())
        {
            // A Synchronisation Rule mode rule runs as stored: it takes its Metaverse Object Type from its rule.
            var result = (await NewJimApplication(ctx).ConfigurationTests.RunByIdsAsync([matchingCaseId])).Single();
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
        }

        await using (var ctx = NewContext())
        {
            var jim = NewJimApplication(ctx);
            var rule = await ChangedMatchingRuleAsync(jim, seed.MatchingRuleId);
            var initiator = await LoadInitiatorAsync(seed);
            Assert.That(async () => await jim.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, initiator),
                Throws.TypeOf<ConfigurationTestsRegressedException>());
        }
    }

    #endregion

    #region Helpers

    private record Seed(int SystemId, int RuleId, int MappingId, int MatchingRuleId, Guid InitiatorId);

    private static ConfigurationTestCase FlowCase(Seed seed) => new()
    {
        Name = CaseName,
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
        Inputs = [new ConfigurationTestInput(null, "sn", [InputValue])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, [InputValue])
    };

    /// <summary>The Object Matching Rule, its source changed to an expression giving another match value.</summary>
    private static async Task<ObjectMatchingRule> ChangedMatchingRuleAsync(JimApplication jim, int matchingRuleId)
    {
        var rule = (await jim.ConnectedSystems.GetObjectMatchingRuleAsync(matchingRuleId))!;
        rule.Sources.Clear();
        rule.Sources.Add(new ObjectMatchingRuleSource { Order = 0, ObjectMatchingRuleId = rule.Id, Expression = "Lower(cs[\"sn\"])" });
        return rule;
    }

    private async Task<int> CreateAsync(Seed seed, ConfigurationTestCase testCase)
    {
        await using var ctx = NewContext();
        await NewJimApplication(ctx).ConfigurationTests.CreateTestCaseAsync(testCase, await LoadInitiatorAsync(seed));
        return testCase.Id;
    }

    private static async Task<SyncRule> LoadRuleAsync(JimApplication jim, Seed seed) =>
        (await jim.ConnectedSystems.GetSyncRuleAsync(seed.RuleId))!;

    private static async Task<string?> StoredExpressionAsync(JimDbContext ctx, Seed seed) =>
        await ctx.SyncRuleMappings.Where(m => m.Id == seed.MappingId).SelectMany(m => m.Sources).Select(s => s.Expression).SingleAsync();

    private async Task<int> CountActivitiesAsync()
    {
        await using var ctx = NewContext();
        return await ctx.Activities.CountAsync();
    }

    private static async Task<Activity> LatestActivityAsync(JimDbContext ctx) =>
        await ctx.Activities.OrderByDescending(a => a.Created).FirstAsync();

    private static async Task<ConfigurationDiff> LatestRuleDiffAsync(JimDbContext ctx, Seed seed)
    {
        var snapshots = await ctx.Activities
            .Where(a => a.SyncRuleId == seed.RuleId && a.ConfigurationChangeSnapshot != null)
            .OrderByDescending(a => a.Created)
            .Select(a => a.ConfigurationChangeSnapshot)
            .Take(2)
            .ToListAsync();
        return new ConfigurationDiffService().Diff(ConfigurationSnapshotService.Deserialise(snapshots[1]),
            ConfigurationSnapshotService.Deserialise(snapshots[0])!);
    }

    private async Task<Seed> SeedAsync(bool advancedMatching = false)
    {
        int systemId, csTypeId, mvTypeId, displayNameId, snId;
        Guid initiatorId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem
            {
                Name = "HR", ConnectorDefinition = connectorDefinition,
                ObjectMatchingRuleMode = advancedMatching ? ObjectMatchingRuleMode.SyncRule : ObjectMatchingRuleMode.ConnectedSystem
            };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var sn = new ConnectedSystemObjectTypeAttribute { Name = "sn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            csType.Attributes.Add(sn);
            csType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "givenName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true });
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
            var matchingRule = advancedMatching
                ? new ObjectMatchingRule { SyncRuleId = ruleId }
                : new ObjectMatchingRule { ConnectedSystemObjectTypeId = csTypeId, MetaverseObjectTypeId = mvTypeId };
            matchingRule.TargetMetaverseAttributeId = displayNameId;
            matchingRule.Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = snId }];
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
