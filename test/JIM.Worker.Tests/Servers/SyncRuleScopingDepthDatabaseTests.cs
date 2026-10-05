// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
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
/// Real-PostgreSQL verification that a Synchronisation Rule's scoping criteria tree loads, and is accounted for, at
/// every depth the editor lets an administrator build.
/// </summary>
/// <remarks>
/// The criteria editor offers "add group" inside every group, so a tree can nest to any depth, but the rule loaders
/// used to spell their Includes out two levels deep. A group the loader did not reach was simply absent at evaluation
/// time, and its parent then evaluated as an empty group, which counts as met: the rule's scope silently widened to
/// objects its criteria exclude. The same two-level assumption sat in the configuration drift scope and in the
/// attribute-reference check that guards attribute removal. The EF Core in-memory provider fixes navigations up
/// regardless of Includes and cannot show any of this, hence a real database.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleScopingDepthDatabaseTests
{
    private const string ExportRuleName = "Finance App Users Export";
    private const string ImportRuleName = "Finance App Users Import";

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

    private static JimApplication NewJimApplication(JimDbContext context)
    {
        var repository = new PostgresDataRepository(context);
        return new JimApplication(repository, syncRepository: repository.Sync);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL scoping depth tests.");

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
    public async Task CreateOrUpdateSyncRuleAsync_ScopingTreeFourLevelsDeep_PersistsEveryGroupAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 4);

        // Read the tables directly so this proves what was saved, independently of any loader.
        await using var ctx = NewContext();
        var groupCount = await ctx.SyncRuleScopingCriteriaGroups.CountAsync();
        var criteriaCount = await ctx.SyncRuleScopingCriteria.CountAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groupCount, Is.EqualTo(4), "every group in the chain should be persisted");
            Assert.That(criteriaCount, Is.EqualTo(1), "the deepest group's criterion should be persisted");
        }
    }

    [Test]
    public async Task GetSyncRulesAsync_ScopingTreeFourLevelsDeep_LoadsEveryLevelAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 4);

        await using var ctx = NewContext();
        var rule = (await NewJimApplication(ctx).ConnectedSystems.GetSyncRulesAsync()).Single(r => r.Name == ExportRuleName);

        AssertChainLoaded(rule, depth: 4, expectMetaverseAttribute: true);
    }

    [Test]
    public async Task GetSyncRulesAsync_ForConnectedSystem_LoadsEveryLevelWithoutRelyingOnAnotherLoadAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 4);

        // Full and delta synchronisation load their own system's rules this way. Before the fix this loader included
        // no scoping criteria at all; its rules only received them when a tracked all-rules load ran later in the same
        // context and EF fixed the navigations up. Load it alone, untracked, so nothing can paper over the gap.
        await using var ctx = NewContext();
        var rules = await NewJimApplication(ctx).ConnectedSystems.GetSyncRulesAsync(ids.SystemId, includeDisabledSyncRules: false);

        AssertChainLoaded(rules.Single(r => r.Name == ExportRuleName), depth: 4, expectMetaverseAttribute: true);
    }

    [Test]
    public async Task GetSyncRulesAsync_ForConnectedSystemWithChangeTracking_LoadsEveryLevelOnceAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 4);

        // The synchronisation processors load tracked, then load every rule tracked into the same context. The tree
        // must come back complete, with no group attached twice.
        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var systemRules = await jim.ConnectedSystems.GetSyncRulesAsync(ids.SystemId, includeDisabledSyncRules: false, withChangeTracking: true);
        await jim.SyncRepo.GetAllSyncRulesAsync(withChangeTracking: true);

        AssertChainLoaded(systemRules.Single(r => r.Name == ExportRuleName), depth: 4, expectMetaverseAttribute: true);
    }

    [Test]
    public async Task GetSyncRulesAsync_OnAContextThatTracksByDefault_LeavesNothingToSaveAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 4);

        // The worker's context tracks by default, so a load that does not ask for change tracking still tracks the
        // rules. Their scoping groups must be tracked with them: untracked groups hung off a tracked rule look like
        // new rows to EF, and the run's next save would try to insert the whole tree again.
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var ctx = new JimDbContext(options);
        var jim = NewJimApplication(ctx);
        var rule = (await jim.ConnectedSystems.GetSyncRulesAsync()).Single(r => r.Name == ExportRuleName);

        AssertChainLoaded(rule, depth: 4, expectMetaverseAttribute: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ChangeTracker.HasChanges(), Is.False, "loading must not leave pending changes");
            Assert.That(await ctx.SaveChangesAsync(), Is.Zero, "nothing should be written");
            Assert.That(await ctx.SyncRuleScopingCriteriaGroups.CountAsync(), Is.EqualTo(4), "no group duplicated");
        }
    }

    [Test]
    public async Task GetSyncRuleAsync_ScopingTreeFourLevelsDeep_LoadsEveryLevelAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreateExportRuleAsync(ids, depth: 4);

        await using var ctx = NewContext();
        var rule = await NewJimApplication(ctx).ConnectedSystems.GetSyncRuleAsync(ruleId);

        Assert.That(rule, Is.Not.Null);
        AssertChainLoaded(rule!, depth: 4, expectMetaverseAttribute: true);
    }

    [Test]
    public async Task GetSyncRuleAsync_ImportRuleScopingTreeThreeLevelsDeep_LoadsTheConnectedSystemAttributeAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreateImportRuleAsync(ids, depth: 3);

        await using var ctx = NewContext();
        var rule = await NewJimApplication(ctx).ConnectedSystems.GetSyncRuleAsync(ruleId);

        Assert.That(rule, Is.Not.Null);
        AssertChainLoaded(rule!, depth: 3, expectMetaverseAttribute: false);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_EditingTheDeepestCriterionOfALoadedRule_PersistsTheEditAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreateExportRuleAsync(ids, depth: 4);

        // The editor's contract: load (tracked), mutate, save on the same JimApplication.
        await using (var editContext = NewContext())
        {
            var jim = NewJimApplication(editContext);
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(ruleId);
            Assert.That(rule, Is.Not.Null);
            DeepestGroup(rule!).Criteria.Single().StringValue = "Payroll";

            var initiator = await editContext.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);
            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule!, initiator), Is.True);
        }

        await using var verifyContext = NewContext();
        var stored = await verifyContext.SyncRuleScopingCriteria.SingleAsync();
        Assert.That(stored.StringValue, Is.EqualTo("Payroll"));
    }

    [Test]
    public async Task IsMvoInScopeForExportRule_RuleLoadedForSynchronisation_DoesNotWidenScopeAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 3);

        await using var ctx = NewContext();
        var jim = NewJimApplication(ctx);
        var rule = (await jim.ConnectedSystems.GetSyncRulesAsync()).Single(r => r.Name == ExportRuleName);
        var department = rule.MetaverseObjectType!.Attributes.Single(a => a.Id == ids.DepartmentAttributeId);

        // Department is Sales, and the only criterion (three levels down) requires Finance, so the object is out of
        // scope. If the deepest group is not loaded, its parent evaluates as empty (met) and the object is wrongly
        // reported in scope.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = rule.MetaverseObjectType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = department,
            AttributeId = department.Id,
            StringValue = "Sales"
        });

        Assert.That(jim.ScopingEvaluation.IsMvoInScopeForExportRule(mvo, rule), Is.False);
    }

    [Test]
    public async Task GetConfigurationScopesAsync_AttributeOnlyInADeepCriterion_IsInTheScopeAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 3);

        // Configuration drift detection asks which Metaverse Attributes a system's rules depend on. Department is used
        // only by the criterion three levels down (the rule has no Attribute Flow), so it must still be reported.
        await using var ctx = NewContext();
        var scopes = await new PostgresDataRepository(ctx).ConnectedSystems.GetConfigurationScopesAsync([ids.SystemId]);

        Assert.That(scopes.Single().MetaverseAttributeIds, Does.Contain(ids.DepartmentAttributeId));
    }

    [Test]
    public async Task GetAttributeReferencesForObjectTypeAsync_AttributeUsedByADeepCriterion_ReportsTheCriterionAsync()
    {
        var ids = await SeedAsync();
        await CreateExportRuleAsync(ids, depth: 3);

        // This is the check that stops an attribute being removed from a type while something still uses it. A
        // criterion it cannot see is left pointing at nothing, and a criterion with no attribute never matches.
        await using var ctx = NewContext();
        var references = await new PostgresDataRepository(ctx).Metaverse
            .GetAttributeReferencesForObjectTypeAsync(ids.DepartmentAttributeId, ids.MvTypeId);

        Assert.That(references.Count(r => r.Kind == AttributeReferenceKind.ScopingCriterion), Is.EqualTo(1));
    }

    private static SyncRuleScopingCriteriaGroup DeepestGroup(SyncRule rule)
    {
        var group = rule.ObjectScopingCriteriaGroups.Single();
        while (group.ChildGroups.Count > 0)
            group = group.ChildGroups.Single();
        return group;
    }

    private static void AssertChainLoaded(SyncRule rule, int depth, bool expectMetaverseAttribute)
    {
        Assert.That(rule.ObjectScopingCriteriaGroups, Has.Count.EqualTo(1), "top-level group");
        var group = rule.ObjectScopingCriteriaGroups.Single();
        for (var level = 2; level <= depth; level++)
        {
            Assert.That(group.ChildGroups, Has.Count.EqualTo(1), $"group at level {level}");
            var child = group.ChildGroups.Single();
            Assert.That(child.ParentGroup, Is.SameAs(group), $"group at level {level} should point at its parent");
            group = child;
        }

        Assert.That(group.ChildGroups, Is.Empty, "the deepest group has no children");
        var criterion = group.Criteria.Single();
        if (expectMetaverseAttribute)
            Assert.That(criterion.MetaverseAttribute, Is.Not.Null, "the criterion's attribute must load for evaluation");
        else
            Assert.That(criterion.ConnectedSystemAttribute, Is.Not.Null, "the criterion's attribute must load for evaluation");
    }

    /// <summary>
    /// Builds a chain of alternating All / Any groups, <paramref name="depth"/> long, with one criterion in the deepest
    /// group, and attaches it to the rule.
    /// </summary>
    private static void AddChain(SyncRule rule, int depth, SyncRuleScopingCriteria criterion)
    {
        var top = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        var current = top;
        for (var level = 2; level <= depth; level++)
        {
            var child = new SyncRuleScopingCriteriaGroup
            {
                Type = level % 2 == 0 ? SearchGroupType.Any : SearchGroupType.All,
                ParentGroup = current
            };
            current.ChildGroups.Add(child);
            current = child;
        }
        current.Criteria.Add(criterion);
        rule.ObjectScopingCriteriaGroups.Add(top);
    }

    private async Task<int> CreateExportRuleAsync(SeedIds ids, int depth)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == ids.CsTypeId);
        var mvType = await ctx.MetaverseObjectTypes.Include(t => t.Attributes).SingleAsync(x => x.Id == ids.MvTypeId);
        var department = mvType.Attributes.Single(a => a.Id == ids.DepartmentAttributeId);
        var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);

        var rule = new SyncRule
        {
            Name = ExportRuleName,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ProvisionToConnectedSystem = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        AddChain(rule, depth, new SyncRuleScopingCriteria
        {
            MetaverseAttribute = department,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });

        Assert.That(await NewJimApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True,
            "Failed to create the export rule with a nested scoping tree.");
        return rule.Id;
    }

    private async Task<int> CreateImportRuleAsync(SeedIds ids, int depth)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.Include(t => t.Attributes).SingleAsync(x => x.Id == ids.CsTypeId);
        var csDepartment = csType.Attributes.Single(a => a.Id == ids.CsDepartmentAttributeId);
        var mvType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == ids.MvTypeId);
        var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);

        var rule = new SyncRule
        {
            Name = ImportRuleName,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        AddChain(rule, depth, new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = csDepartment,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });

        Assert.That(await NewJimApplication(ctx).ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True,
            "Failed to create the import rule with a nested scoping tree.");
        return rule.Id;
    }

    private async Task<SeedIds> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Finance App",
            ConnectorDefinition = connectorDefinition,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csDepartment = new ConnectedSystemObjectTypeAttribute { Name = "department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var department = new MetaverseAttribute { Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(department);
        csType.Attributes.Add(csDepartment);

        // an initiator is required so the operation's Activity can be attributed to a security principal
        var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseObjects.Add(initiator);
        await seed.SaveChangesAsync();

        return new SeedIds(system.Id, csType.Id, csDepartment.Id, mvType.Id, department.Id, initiator.Id);
    }

    private record SeedIds(int SystemId, int CsTypeId, int CsDepartmentAttributeId, int MvTypeId, int DepartmentAttributeId, Guid InitiatorId);
}
