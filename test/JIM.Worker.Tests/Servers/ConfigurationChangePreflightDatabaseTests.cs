// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the save-time acknowledgement, exercised the way the editor exercises it:
/// load the rule, mutate it in place, and ask for a preflight, all on the one tracked DbContext.
/// </summary>
/// <remarks>
/// This is the test that guards the design decision, and it is the reason the preflight takes its baseline from the
/// stored configuration snapshot rather than re-reading the entity. The editor mutates the instance it loaded and
/// saves it on the same context (see <see cref="SyncRuleUpdateDatabaseTests"/> for why it must), so a "before" read
/// through that same context returns the *mutated* instance: the diff comes back empty, no acknowledgement is ever
/// shown, and the whole feature silently does nothing. Every unit test in
/// <c>ConfigurationChangePreflightServiceTests</c> passes with that bug present, because they use detached objects
/// and a mocked repository. Only a real context reproduces it.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationChangePreflightDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration change preflight tests.");

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

    [Test]
    public async Task EvaluateSyncRuleAsync_DestructiveToggleOnTheEditorsOwnContext_AsksForAcknowledgementAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreatePersistedExportRuleAsync(ids);

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var rule = await jim.ConnectedSystems.GetSyncRuleAsync(ruleId);
        Assert.That(rule, Is.Not.Null);

        // Exactly what the editor does: mutate the loaded instance, then ask before saving.
        rule!.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        var preflight = await jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(rule);

        var item = preflight.DestructiveItems.SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.BaselineUnavailable, Is.False, "the create should have captured a version 1 baseline");
            Assert.That(preflight.IsDestructive, Is.True,
                "switching the Deprovisioning Action to Delete must be caught before the save, not after");
            Assert.That(item, Is.Not.Null);
            Assert.That(item!.Consequence, Does.Contain("deleted"));
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_RenameOnTheEditorsOwnContext_StaysSilentAsync()
    {
        var ids = await SeedAsync();
        var ruleId = await CreatePersistedExportRuleAsync(ids);

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var rule = await jim.ConnectedSystems.GetSyncRuleAsync(ruleId);
        Assert.That(rule, Is.Not.Null);

        rule!.Name = "Renamed Export Rule";
        var preflight = await jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.RequiresAcknowledgement, Is.False,
                "a rename cannot change a synchronisation outcome and must not interrupt the administrator");
            Assert.That(preflight.HighestClass, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        }
    }

    #region Seeding

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
        var csAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "cn",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            ConnectedSystemObjectType = csType,
            Selected = true
        };
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var mvAttr = new MetaverseAttribute
        {
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = true
        };
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

        return new SeedIds(system.Id, csType.Id, mvType.Id, initiator.Id);
    }

    private record SeedIds(int SystemId, int CsTypeId, int MvTypeId, Guid InitiatorId);

    private async Task<MetaverseObject> LoadInitiatorAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);
    }

    /// <summary>
    /// Creates an export rule through the audited path, so it ends up with the version 1 configuration snapshot the
    /// preflight compares against. Export, because the Deprovisioning Action is an outbound property.
    /// </summary>
    private async Task<int> CreatePersistedExportRuleAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == ids.CsTypeId);
        var mvType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == ids.MvTypeId);
        var initiator = await LoadInitiatorAsync(ids);

        var rule = new SyncRule
        {
            Name = "Existing Export Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };

        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
        Assert.That(ok, Is.True, "Failed to create the rule the preflight tests need.");
        return rule.Id;
    }

    #endregion
}
