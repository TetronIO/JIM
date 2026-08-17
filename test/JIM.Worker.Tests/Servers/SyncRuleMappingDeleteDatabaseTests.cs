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
/// Real-PostgreSQL verification of deleting a Synchronisation Rule Mapping through
/// <see cref="JIM.Application.Servers.ConnectedSystemServer.DeleteSyncRuleMappingAsync"/>.
/// </summary>
/// <remarks>
/// Regression guard for a delete that failed for every caller of the REST endpoint and of
/// <c>Remove-JIMSyncRuleMapping</c>. The API's delete handler loads the Synchronisation Rule to check it exists
/// and then loads the mapping, in the same request and therefore the same DbContext. <c>GetSyncRuleAsync</c>
/// opts into <c>AsTracking()</c> and includes <c>AttributeFlowRules.Sources</c>, so every
/// <c>SyncRuleMappingSource</c> on the rule enters the change tracker; <c>GetSyncRuleMappingAsync</c> runs under
/// the context default of NoTracking and materialises a second, detached copy of the same rows. The delete then
/// attached the detached copies and EF threw "The instance of entity type 'SyncRuleMappingSource' cannot be
/// tracked because another instance with the same key value for {'Id'} is already being tracked."
///
/// The EF Core in-memory provider cannot reproduce this: it fixes up navigation properties automatically and
/// does not model NoTracking materialisation, so only a real database exercises the identity conflict. Opt in
/// with the same <c>JIM_TEST_RESET_*</c> environment variables as the sibling database tests.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleMappingDeleteDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL sync-rule mapping delete tests.");

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

    private record SeedIds(int SyncRuleId, int MappingId, Guid InitiatorId);

    /// <summary>
    /// Seeds an import Synchronisation Rule carrying one mapping with one source, which is the smallest shape
    /// that reproduces the conflict: the source rows are what both queries materialise.
    /// </summary>
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
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            ConnectedSystemObjectType = csType,
            Selected = true
        };
        csType.Attributes.Add(csAttr);

        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var mvAttr = new MetaverseAttribute
        {
            Name = "Display Name",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = true
        };
        mvType.Attributes.Add(mvAttr);

        // An initiator is required so the delete's Activity can be attributed to a security principal.
        var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };

        var rule = new SyncRule
        {
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType,
            ProjectToMetaverse = true
        };

        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            Priority = int.MaxValue,
            TargetMetaverseAttribute = mvAttr,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csAttr } }
        };
        rule.AttributeFlowRules.Add(mapping);

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(mvType);
        seed.MetaverseObjects.Add(initiator);
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();

        return new SeedIds(rule.Id, mapping.Id, initiator.Id);
    }

    /// <summary>
    /// The exact sequence the API's delete handler performs: check the rule exists, fetch the mapping, delete it.
    /// All three share one DbContext, as they do inside a single HTTP request.
    /// </summary>
    [Test]
    public async Task DeleteSyncRuleMappingAsync_AfterTheRuleWasLoadedInTheSameContext_DeletesTheMappingAsync()
    {
        var ids = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);

            // Loading the rule tracks every one of its mapping sources. The API does this only to return a 404
            // for an unknown rule, but the tracking it causes is what the delete then has to survive.
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(ids.SyncRuleId);
            Assert.That(rule, Is.Not.Null, "The seeded Synchronisation Rule should be retrievable.");

            var mapping = await jim.ConnectedSystems.GetSyncRuleMappingAsync(ids.MappingId);
            Assert.That(mapping, Is.Not.Null, "The seeded mapping should be retrievable.");

            Assert.That(async () =>
                await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping!, initiator), Throws.Nothing,
                "Deleting a mapping must not fail because the rule it belongs to was loaded first.");
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == ids.MappingId), Is.False,
                "The mapping should no longer exist.");
            // The seed creates exactly one source, and it belongs to the deleted mapping, so an empty table is
            // the assertion. SyncRuleMappingSource carries no foreign key property to filter on.
            Assert.That(await verify.SyncRuleMappingSources.AnyAsync(), Is.False,
                "The mapping's sources should have been removed with it.");
        }
    }

    /// <summary>
    /// The same delete without the preceding rule load, which is the path that already worked. Kept so a future
    /// change cannot fix the conflicting case by breaking the simple one.
    /// </summary>
    [Test]
    public async Task DeleteSyncRuleMappingAsync_WithoutTheRuleBeingLoadedFirst_DeletesTheMappingAsync()
    {
        var ids = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == ids.InitiatorId);
            var mapping = await jim.ConnectedSystems.GetSyncRuleMappingAsync(ids.MappingId);
            Assert.That(mapping, Is.Not.Null);

            await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping!, initiator);
        }

        await using var verify = NewContext();
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == ids.MappingId), Is.False);
    }
}
