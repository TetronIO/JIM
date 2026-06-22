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
        _connectionString = $"Host={host};Database={dbName};Username={user};Password={pass}";

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
        mvType.Attributes.Add(mvAttr);
        csType.Attributes.Add(csAttr);

        // an initiator is required so the operation's Activity can be attributed to a security principal
        var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseObjects.Add(initiator);
        await seed.SaveChangesAsync();

        return new SeedIds(system.Id, csType.Id, csAttr.Id, mvType.Id, mvAttr.Id, initiator.Id);
    }

    private record SeedIds(int SystemId, int CsTypeId, int CsAttrId, int MvTypeId, int MvAttrId, Guid InitiatorId);

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
}
