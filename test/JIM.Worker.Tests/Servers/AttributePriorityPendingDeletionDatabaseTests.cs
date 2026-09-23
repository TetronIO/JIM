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
/// Real-PostgreSQL verification of attribute priority while a Synchronisation Rule deletion is queued (#1597): the
/// queue step moving the rule's mapping to the bottom of the order on a <c>NoTracking</c> context (as JIM.Web runs),
/// and a replacement order that leaves the rule out being accepted, which depends on the worker task lookup
/// translating against the real Worker Task table hierarchy. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is
/// absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AttributePriorityPendingDeletionDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute priority pending-deletion tests.");

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
    public async Task DeleteThenReorderSurvivors_OnNoTrackingContexts_QueuesRecallAndAcceptsTheOrderAsync()
    {
        var seed = await SeedThreeContributorsAsync();

        // Delete HR the way the portal and the REST API do: load and delete on one short-lived NoTracking unit of work.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            var rule = await jim.ConnectedSystems.GetSyncRuleAsync(seed.Hr.RuleId);
            var deletion = await jim.ConnectedSystems.DeleteSyncRuleAsync(rule!, await LoadInitiatorAsync(seed));
            Assert.That(deletion.RecallQueued, Is.True, "precondition: HR contributes a value, so its deletion must queue a recall");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await GetPersistedPriorityAsync(seed.Training.MappingId), Is.EqualTo(1));
            Assert.That(await GetPersistedPriorityAsync(seed.Payroll.MappingId), Is.EqualTo(2));
            Assert.That(await GetPersistedPriorityAsync(seed.Hr.MappingId), Is.EqualTo(3),
                "the rule being deleted must drop to the bottom of the order as soon as its recall is queued");
        }

        // Then reorder to the survivors only, on a fresh unit of work, as a script or a second portal action would.
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.ConnectedSystems.SetAttributePriorityOrderAsync(seed.MvTypeId, seed.DescriptionAttributeId,
                [(seed.Payroll.MappingId, false), (seed.Training.MappingId, false)], await LoadInitiatorAsync(seed));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await GetPersistedPriorityAsync(seed.Payroll.MappingId), Is.EqualTo(1));
            Assert.That(await GetPersistedPriorityAsync(seed.Training.MappingId), Is.EqualTo(2));
            Assert.That(await GetPersistedPriorityAsync(seed.Hr.MappingId), Is.EqualTo(3),
                "left out of the order because its deletion is queued, the rule stays at the bottom");
        }
    }

    [Test]
    public async Task SetAttributePriorityOrderAsync_OmittingDisabledRuleWithNoQueuedDeletion_IsRefusedAsync()
    {
        // A disabled rule that is not being deleted is an ordinary contributor, however it came to be disabled.
        var seed = await SeedThreeContributorsAsync();
        await using (var ctx = NewContext())
        {
            await ctx.SyncRules.Where(r => r.Id == seed.Hr.RuleId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false));
        }

        await using var actCtx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(actCtx));
        var initiator = await LoadInitiatorAsync(seed);

        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await jim.ConnectedSystems.SetAttributePriorityOrderAsync(seed.MvTypeId, seed.DescriptionAttributeId,
                [(seed.Payroll.MappingId, false), (seed.Training.MappingId, false)], initiator));

        Assert.That(ex!.Message, Does.Contain($"mapping {seed.Hr.MappingId} (Synchronisation Rule 'HR Import')"));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Seeding
    // -----------------------------------------------------------------------------------------------------------------

    private sealed record Contributor(int RuleId, int MappingId);

    private sealed record ThreeContributorSeed(
        Guid InitiatorId,
        int MvTypeId,
        int DescriptionAttributeId,
        Contributor Hr,
        Contributor Training,
        Contributor Payroll);

    private async Task<MetaverseObject> LoadInitiatorAsync(ThreeContributorSeed seed)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.SingleAsync(x => x.Id == seed.InitiatorId);
    }

    private async Task<int> GetPersistedPriorityAsync(int mappingId)
    {
        await using var verify = NewContext();
        var mapping = await verify.SyncRuleMappings.SingleAsync(m => m.Id == mappingId);
        return mapping.Priority;
    }

    /// <summary>
    /// Three import Synchronisation Rules (HR, Training, Payroll) contributing Description, created in that order
    /// through the whole-rule save so they hold priorities 1, 2 and 3; HR has contributed one value, so deleting it
    /// queues a recall rather than deleting synchronously.
    /// </summary>
    private async Task<ThreeContributorSeed> SeedThreeContributorsAsync()
    {
        int systemId, csTypeId, mvTypeId, descriptionId;
        Guid initiatorId, targetMvoId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem
            {
                Name = "Test System",
                ConnectorDefinition = connectorDefinition,
                ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
            };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            csType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Name = "description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true });
            var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var description = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
            mvType.Attributes.Add(description);

            var initiator = new MetaverseObject { Type = mvType, CachedDisplayName = "Test Administrator" };
            var target = new MetaverseObject { Type = mvType };

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.MetaverseObjects.AddRange(initiator, target);
            await seed.SaveChangesAsync();

            (systemId, csTypeId, mvTypeId, descriptionId, initiatorId, targetMvoId) =
                (system.Id, csType.Id, mvType.Id, description.Id, initiator.Id, target.Id);
        }

        var hr = await CreateContributorAsync("HR Import", systemId, csTypeId, mvTypeId, descriptionId, initiatorId);
        var training = await CreateContributorAsync("Training Import", systemId, csTypeId, mvTypeId, descriptionId, initiatorId);
        var payroll = await CreateContributorAsync("Payroll Import", systemId, csTypeId, mvTypeId, descriptionId, initiatorId);

        await using (var ctx = NewContext())
        {
            // The owning Metaverse Object is a shadow foreign key, so attach through a tracked navigation.
            ctx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = await ctx.MetaverseObjects.AsTracking().SingleAsync(x => x.Id == targetMvoId),
                AttributeId = descriptionId,
                StringValue = "HR Description",
                ContributedBySyncRuleId = hr.RuleId,
                ContributedBySystemId = systemId
            });
            await ctx.SaveChangesAsync();
        }

        var result = new ThreeContributorSeed(initiatorId, mvTypeId, descriptionId, hr, training, payroll);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await GetPersistedPriorityAsync(hr.MappingId), Is.EqualTo(1), "precondition: HR is the top contributor");
            Assert.That(await GetPersistedPriorityAsync(training.MappingId), Is.EqualTo(2));
            Assert.That(await GetPersistedPriorityAsync(payroll.MappingId), Is.EqualTo(3));
        }

        return result;
    }

    private async Task<Contributor> CreateContributorAsync(string name, int systemId, int csTypeId, int mvTypeId, int descriptionId, Guid initiatorId)
    {
        await using var ctx = NewContext();
        var rule = new SyncRule
        {
            Name = name,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = await ctx.ConnectedSystems.SingleAsync(x => x.Id == systemId),
            ConnectedSystemObjectType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == csTypeId),
            MetaverseObjectType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == mvTypeId)
        };
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = await ctx.MetaverseAttributes.SingleAsync(x => x.Id == descriptionId) };
        rule.AttributeFlowRules.Add(mapping);

        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var initiator = await ctx.MetaverseObjects.SingleAsync(x => x.Id == initiatorId);
        Assert.That(await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator), Is.True, $"Failed to create '{name}'.");
        return new Contributor(rule.Id, mapping.Id);
    }
}
