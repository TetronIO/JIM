// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of <b>updating</b> an existing <see cref="SyncRule"/> through
/// <see cref="JIM.Application.Servers.ConnectedSystemServer.CreateOrUpdateSyncRuleAsync(SyncRule, MetaverseObject?, JIM.Models.Activities.Activity?)"/>.
/// </summary>
/// <remarks>
/// Regression guard for a silent data-loss bug: the global DbContext default is NoTracking and the web editor
/// created a fresh DbContext per handler, so the rule loaded for editing was detached from the context that
/// later called SaveChanges. <c>UpdateSyncRuleAsync</c> only calls <c>SaveChangesAsync()</c>, which silently
/// persisted nothing for a detached entity; disabling an existing rule looked successful but never stuck.
///
/// Two protections are asserted here:
///  - same-context load -> mutate -> save persists (the contract the rebuilt editor relies on), and
///  - a detached entity now fails fast (throws) rather than silently discarding the change.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the creation tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent. The EF Core in-memory provider auto-fixes up navigation properties and
/// cannot reproduce the detached-context behaviour, hence a real database.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleUpdateDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL sync-rule update tests.");

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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    private async Task<SeedIds> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var csType = new ConnectedSystemObjectType { Name = "jimGroup", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Name = "cn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var mvAttr = new MetaverseAttribute { Name = "DisplayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        // A second attribute, so the attribute priority tests can move a contribution between two priority lists.
        var mvAttr2 = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(mvAttr);
        mvType.Attributes.Add(mvAttr2);
        csType.Attributes.Add(csAttr);

        // an initiator is required so the operation's Activity can be attributed to a security principal
        var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseObjects.Add(initiator);
        await seed.SaveChangesAsync();

        return new SeedIds(system.Id, csType.Id, csAttr.Id, mvType.Id, mvAttr.Id, mvAttr2.Id, initiator.Id);
    }

    private record SeedIds(int SystemId, int CsTypeId, int CsAttrId, int MvTypeId, int MvAttrId, int MvAttr2Id, Guid InitiatorId);

    private async Task<MetaverseObject> LoadInitiatorAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);
    }

    /// <summary>
    /// Creates and persists a bare enabled import rule, returning its id, so the update tests have something to edit.
    /// </summary>
    private async Task<int> CreatePersistedImportRuleAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == ids.CsTypeId);
        var mvType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == ids.MvTypeId);
        var initiator = await LoadInitiatorAsync(ids);

        var rule = new SyncRule
        {
            Name = "Existing Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };

        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
        Assert.That(ok, Is.True, "Failed to create the rule the update tests need.");
        return rule.Id;
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisableExistingRuleSameContext_PersistsAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreatePersistedImportRuleAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);

        // Load -> mutate -> save through a single JimApplication/DbContext, exactly as the rebuilt editor now does.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(ruleId);
            Assert.That(rule, Is.Not.Null);
            rule!.Enabled = false;
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True);
        }

        await using var verify = NewContext();
        var persisted = await verify.SyncRules.SingleAsync(r => r.Id == ruleId);
        Assert.That(persisted.Enabled, Is.False, "Disabling an existing Synchronisation Rule must persist.");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DetachedRule_ThrowsRatherThanSilentlyDiscardingChangesAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreatePersistedImportRuleAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);

        // Load the rule in one context; it is detached relative to any other, as the old editor's per-handler
        // contexts were.
        SyncRule detachedRule;
        await using (var loadCtx = NewContext())
        {
            var loadJim = new JimApplication(new PostgresDataRepository(loadCtx));
            detachedRule = (await loadJim.ConnectedSystems.GetSyncRuleAsync(ruleId))!;
        }
        Assert.That(detachedRule, Is.Not.Null);
        detachedRule.Enabled = false;

        // Saving the detached rule through a different context must fail loudly. Before the fix this silently
        // persisted nothing and reported success, losing the change.
        await using (var saveCtx = NewContext())
        {
            var saveJim = new JimApplication(new PostgresDataRepository(saveCtx));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await saveJim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(detachedRule, initiator));
        }

        await using var verify = NewContext();
        var persisted = await verify.SyncRules.SingleAsync(r => r.Id == ruleId);
        Assert.That(persisted.Enabled, Is.True, "A failed update must not have changed the stored rule.");
    }

    // ─── Attribute priority through the whole-rule save path (#1199) ───
    //
    // The portal's Attribute Flow editor never calls the granular mapping methods: it mutates
    // SyncRule.AttributeFlowRules in memory and saves the whole rule here. These prove the attribute priority
    // reconcile that path now performs, and they need a real database for the reason the rest of this fixture does:
    // the reconcile's pre-save read must return what the database holds while the change tracker holds the
    // administrator's edits, and the in-memory provider cannot tell the two apart.

    /// <summary>
    /// Creates a persisted import rule carrying one import mapping to the given Metaverse attribute, the way the
    /// portal does it: the target is set on the navigation property, and the whole rule is saved in one go.
    /// </summary>
    private async Task<(int RuleId, int MappingId)> CreatePersistedRuleWithMappingAsync(SeedIds ids, string name, int metaverseAttributeId)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == ids.CsTypeId);
        var mvType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == ids.MvTypeId);
        var mvAttr = await ctx.MetaverseAttributes.AsTracking().SingleAsync(x => x.Id == metaverseAttributeId);
        var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvAttr };
        var rule = new SyncRule
        {
            Name = name,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        rule.AttributeFlowRules.Add(mapping);

        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
        Assert.That(ok, Is.True, $"Failed to create the rule '{name}' the attribute priority tests need.");
        return (rule.Id, mapping.Id);
    }

    private async Task<int> GetPersistedPriorityAsync(int mappingId)
    {
        await using var verify = NewContext();
        var mapping = await verify.SyncRuleMappings.SingleAsync(m => m.Id == mappingId);
        return mapping.Priority;
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportMappingAddedInWholeRuleSave_DensifiesAttributePriorityAsync()
    {
        var ids = await SeedAsync();
        var first = await CreatePersistedRuleWithMappingAsync(ids, "First Contributor", ids.MvAttrId);
        var second = await CreatePersistedRuleWithMappingAsync(ids, "Second Contributor Rule", ids.MvAttr2Id);

        // Retarget nothing; simply add a second contribution to the first attribute, on the second rule, exactly as
        // the portal's Attribute Flow dialog does: load, add to the collection, save the rule.
        int addedMappingId;
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(second.RuleId);
            Assert.That(rule, Is.Not.Null);

            var mvAttr = await ctx.MetaverseAttributes.AsTracking().SingleAsync(x => x.Id == ids.MvAttrId);
            var added = new SyncRuleMapping { TargetMetaverseAttribute = mvAttr };
            rule!.AttributeFlowRules.Add(added);

            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(ids)), Is.True);
            addedMappingId = added.Id;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await GetPersistedPriorityAsync(first.MappingId), Is.EqualTo(1),
                "the incumbent contributor must take the top, explicit priority");
            Assert.That(await GetPersistedPriorityAsync(addedMappingId), Is.EqualTo(2),
                "a mapping added through the portal's whole-rule save must land at the bottom of the priority list");
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_MappingRetargetedInWholeRuleSave_LandsLastInTheNewAttributesListAsync()
    {
        var ids = await SeedAsync();

        // Two contributors to the first attribute, so the second one holds an explicit priority of 2.
        var first = await CreatePersistedRuleWithMappingAsync(ids, "First Contributor", ids.MvAttrId);
        var mover = await CreatePersistedRuleWithMappingAsync(ids, "Mover Rule", ids.MvAttrId);

        // A sole contributor to the second attribute, created last so its mapping id is HIGHER than the mover's. That
        // ordering is the trap: contributor lists are read (Priority asc, Id asc), and while both sit at the
        // int.MaxValue sentinel the older mapping sorts first, so a retargeted mapping would take priority 1 in its
        // new list and silently start winning resolution.
        var incumbent = await CreatePersistedRuleWithMappingAsync(ids, "Second Attribute Rule", ids.MvAttr2Id);
        Assert.That(mover.MappingId, Is.LessThan(incumbent.MappingId), "the fixture depends on the mover being the older mapping");

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(mover.RuleId);
            Assert.That(rule, Is.Not.Null);

            // Retarget through the navigation property, as the editor's attribute picker binds it.
            var mvAttr2 = await ctx.MetaverseAttributes.AsTracking().SingleAsync(x => x.Id == ids.MvAttr2Id);
            var mapping = rule!.AttributeFlowRules.Single(m => m.Id == mover.MappingId);
            mapping.TargetMetaverseAttribute = mvAttr2;
            mapping.TargetMetaverseAttributeId = mvAttr2.Id;

            Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, await LoadInitiatorAsync(ids)), Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await GetPersistedPriorityAsync(incumbent.MappingId), Is.EqualTo(1),
                "the attribute's existing contributor keeps the top priority");
            Assert.That(await GetPersistedPriorityAsync(mover.MappingId), Is.EqualTo(2),
                "a retargeted mapping must arrive at the bottom of its new attribute's list, not inherit a rank from its old one");
            Assert.That(await GetPersistedPriorityAsync(first.MappingId), Is.EqualTo(int.MaxValue),
                "the attribute it left is back to a sole contributor, so it resets to the safe-addition sentinel");
        }
    }
}
