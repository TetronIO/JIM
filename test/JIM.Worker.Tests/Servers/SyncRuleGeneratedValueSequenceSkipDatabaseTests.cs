// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the whole-rule save path's sequence skip-ahead (Unique Value Generation, #242,
/// Phase 3 follow-up; plan decision 3): <see cref="JIM.Application.Servers.ConnectedSystemServer.CreateOrUpdateSyncRuleAsync(SyncRule, MetaverseObject?, JIM.Models.Activities.Activity?, string?, System.Guid?, System.Collections.Generic.IReadOnlyCollection{JIM.Models.Logic.SyncRuleMappingRemovalChoice}?)"/>
/// and its API-key-initiated overload both raise a generated Sequence mapping's target attribute counter when the
/// mapping's configured <see cref="SyncRuleMappingGeneration.SequenceStart"/> stands above it, and stamp the move
/// onto the mapping's transient <see cref="SyncRuleMappingGeneration.SequenceSkippedAhead"/> so the caller (the
/// portal's Attribute Flow tab, which saves exclusively through this method) can report it.
/// </summary>
/// <remarks>
/// Before this follow-up, only the single-mapping create/update/settings-update paths applied the skip; a
/// Sequence mapping's start value raised through the whole-rule save (the portal's primary surface) silently
/// never moved the counter. A real database is required because the skip is applied only after the rule (and any
/// newly added mapping within it) has been persisted and its id populated by <c>SaveChangesAsync</c>, which the
/// EF Core in-memory provider's identity-fixup behaviour cannot faithfully reproduce (see <c>test/CLAUDE.md</c>).
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>SyncRule*DatabaseTests</c>.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleGeneratedValueSequenceSkipDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL sync-rule generated-value sequence-skip tests.");

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

    /// <summary>
    /// Seeds the Unique Value Generation feature flag (#242) enabled, since every test in this file exercises the
    /// whole-rule save's generated-mapping path (see "Tests run with flags on" in test/CLAUDE.md). The gate check
    /// itself is covered separately by <see cref="ConnectedSystemServerGeneratedMappingGateTests"/>.
    /// </summary>
    private async Task SeedFeatureFlagEnabledAsync()
    {
        await using var seed = NewContext();
        var flag = FeatureFlagCatalogue.UniqueValueGeneration;
        seed.Set<ServiceSetting>().Add(new ServiceSetting
        {
            Key = flag.Key,
            DisplayName = flag.DisplayName,
            Description = flag.Description,
            Category = ServiceSettingCategory.FeatureFlags,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            Value = "true"
        });
        await seed.SaveChangesAsync();
    }

    private async Task<SeedIds> SeedAsync()
    {
        await SeedFeatureFlagEnabledAsync();

        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var csType = new ConnectedSystemObjectType { Name = "jimUser", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Name = "cn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var mvAttr = new MetaverseAttribute { Name = "EmployeeNumber", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(mvAttr);
        csType.Attributes.Add(csAttr);

        // an initiator is required so the save's Activity can be attributed to a security principal
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
    /// Loads the seeded principals as detached instances (NoTracking), exactly as the web editor would hold them
    /// after a prior, disposed request scope: navigation objects present, FK scalars unset.
    /// </summary>
    private async Task<(ConnectedSystem cs, ConnectedSystemObjectType csType, MetaverseObjectType mvType, MetaverseAttribute mvAttr)> LoadDetachedAsync(SeedIds ids)
    {
        await using var ctx = NewContext();
        var cs = await ctx.ConnectedSystems.SingleAsync(x => x.Id == ids.SystemId);
        var csType = await ctx.ConnectedSystemObjectTypes.SingleAsync(x => x.Id == ids.CsTypeId);
        var mvType = await ctx.MetaverseObjectTypes.SingleAsync(x => x.Id == ids.MvTypeId);
        var mvAttr = await ctx.MetaverseAttributes.SingleAsync(x => x.Id == ids.MvAttrId);
        return (cs, csType, mvType, mvAttr);
    }

    /// <summary>
    /// Seeds a <see cref="GeneratedValueSequence"/> counter for <paramref name="mvAttrId"/>, standing at
    /// <paramref name="nextValue"/>, as though an earlier, now-deleted, generated mapping had already advanced it
    /// (the counter outlives any one flow; plan decision 3).
    /// </summary>
    private async Task SeedSequenceCounterAsync(int mvAttrId, long nextValue)
    {
        await using var ctx = NewContext();
        ctx.Set<JIM.Models.Transactional.GeneratedValueSequence>().Add(new JIM.Models.Transactional.GeneratedValueSequence
        {
            MetaverseAttributeId = mvAttrId,
            NextValue = nextValue
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<long> ReadSequenceCounterAsync(int mvAttrId)
    {
        await using var ctx = NewContext();
        return (await ctx.Set<JIM.Models.Transactional.GeneratedValueSequence>().SingleAsync(s => s.MetaverseAttributeId == mvAttrId)).NextValue;
    }

    /// <summary>
    /// Wires a <see cref="JimApplication"/> over <paramref name="ctx"/> with a real
    /// <see cref="JIM.PostgresData.Repositories.SyncRepository"/> so <c>Application.UniqueValues</c> (which the
    /// whole-rule save's sequence skip-ahead calls into) has a repository to work with. Passing only the
    /// <see cref="PostgresDataRepository"/>, as the sibling create/update test files do, leaves
    /// <c>JimApplication.SyncRepo</c> null: production DI always supplies it explicitly (see the comment on
    /// <c>JimApplication</c>'s constructor), and those sibling files never exercise a path that needs it.
    /// </summary>
    private static JimApplication CreateApplication(JimDbContext ctx)
    {
        var repository = new PostgresDataRepository(ctx);
        return new JimApplication(repository, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repository));
    }

    private static SyncRule BuildImportRuleWithSequenceMapping(
        ConnectedSystem cs, ConnectedSystemObjectType csType, MetaverseObjectType mvType, MetaverseAttribute mvAttr, long sequenceStart)
    {
        var rule = new SyncRule
        {
            Name = "Import Rule With Generated Sequence",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = cs,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvAttr,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = sequenceStart }
        };
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_MetaverseObjectInitiatedNewRuleAboveSeededCounter_SkipsAheadAndStampsMappingAsync()
    {
        var ids = await SeedAsync();
        await SeedSequenceCounterAsync(ids.MvAttrId, nextValue: 6);
        var (cs, csType, mvType, mvAttr) = await LoadDetachedAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);
        var rule = BuildImportRuleWithSequenceMapping(cs, csType, mvType, mvAttr, sequenceStart: 500);

        await using (var ctx = NewContext())
        {
            var jim = CreateApplication(ctx);
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True, "CreateOrUpdateSyncRuleAsync returned false; the rule was not created.");
        }

        var mapping = rule.AttributeFlowRules.Single();
        Assert.That(mapping.Generation!.SequenceSkippedAhead, Is.Not.Null,
            "the whole-rule create path did not stamp the skip onto the mapping instance the portal holds.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.Generation.SequenceSkippedAhead!.From, Is.EqualTo(6));
            Assert.That(mapping.Generation.SequenceSkippedAhead.To, Is.EqualTo(500));
            Assert.That(mapping.Id, Is.Not.Zero, "the mapping's id must be populated before the skip is applied.");
        }

        Assert.That(await ReadSequenceCounterAsync(ids.MvAttrId), Is.EqualTo(500), "the counter itself must be moved, not just reported.");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ApiKeyInitiatedNewRuleAboveSeededCounter_SkipsAheadAndStampsMappingAsync()
    {
        var ids = await SeedAsync();
        await SeedSequenceCounterAsync(ids.MvAttrId, nextValue: 41);
        var (cs, csType, mvType, mvAttr) = await LoadDetachedAsync(ids);
        var rule = BuildImportRuleWithSequenceMapping(cs, csType, mvType, mvAttr, sequenceStart: 1000);
        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Test API Key", KeyHash = "test-hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow };

        await using (var ctx = NewContext())
        {
            var jim = CreateApplication(ctx);
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, apiKey);
            Assert.That(ok, Is.True, "CreateOrUpdateSyncRuleAsync (API key overload) returned false; the rule was not created.");
        }

        var mapping = rule.AttributeFlowRules.Single();
        Assert.That(mapping.Generation!.SequenceSkippedAhead, Is.Not.Null,
            "the API-key-initiated whole-rule create path did not stamp the skip onto the mapping instance the caller holds.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.Generation.SequenceSkippedAhead!.From, Is.EqualTo(41));
            Assert.That(mapping.Generation.SequenceSkippedAhead.To, Is.EqualTo(1000));
        }

        Assert.That(await ReadSequenceCounterAsync(ids.MvAttrId), Is.EqualTo(1000));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_UpdateExistingRuleWithRaisedSequenceStart_SkipsAheadAndStampsMappingAsync()
    {
        var ids = await SeedAsync();
        var (cs, csType, mvType, mvAttr) = await LoadDetachedAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);

        // First save: no counter seeded yet, so this is the documented no-op (nothing to skip ahead from).
        var rule = BuildImportRuleWithSequenceMapping(cs, csType, mvType, mvAttr, sequenceStart: 1);
        int mappingId;
        await using (var createCtx = NewContext())
        {
            var jim = CreateApplication(createCtx);
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True);
        }
        var createdMapping = rule.AttributeFlowRules.Single();
        Assert.That(createdMapping.Generation!.SequenceSkippedAhead, Is.Null, "an unseeded counter must not report a skip.");
        mappingId = createdMapping.Id;

        // Now seed the counter, as though intervening synchronisation runs had issued numbers from it.
        await SeedSequenceCounterAsync(ids.MvAttrId, nextValue: 6);

        // Second save: load the rule tracked on a fresh unit of work (as the portal's editor does), raise the
        // mapping's configured start above the counter's position, and save the whole rule again.
        await using var updateCtx = NewContext();
        // AsTracking(): the context defaults to NoTracking (matching JIM.Web), and UpdateSyncRuleAsync asserts it
        // was handed a tracked entity (TrackedEntityGuard), exactly as the real portal save path requires.
        var loadedRule = await updateCtx.SyncRules
            .AsTracking()
            .Include(r => r.AttributeFlowRules).ThenInclude(m => m.Generation)
            .Include(r => r.ConnectedSystem)
            .Include(r => r.ConnectedSystemObjectType)
            .Include(r => r.MetaverseObjectType)
            .SingleAsync(r => r.Id == rule.Id);
        var loadedMapping = loadedRule.AttributeFlowRules.Single(m => m.Id == mappingId);
        loadedMapping.Generation!.SequenceStart = 500;

        var jimUpdate = CreateApplication(updateCtx);
        var updateOk = await jimUpdate.ConnectedSystems.CreateOrUpdateSyncRuleAsync(loadedRule, initiator);
        Assert.That(updateOk, Is.True, "CreateOrUpdateSyncRuleAsync returned false on the update save.");

        Assert.That(loadedMapping.Generation.SequenceSkippedAhead, Is.Not.Null,
            "the whole-rule update path did not stamp the skip onto the mapping instance the caller holds.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedMapping.Generation.SequenceSkippedAhead!.From, Is.EqualTo(6));
            Assert.That(loadedMapping.Generation.SequenceSkippedAhead.To, Is.EqualTo(500));
        }

        Assert.That(await ReadSequenceCounterAsync(ids.MvAttrId), Is.EqualTo(500));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_SequenceStartAtOrBelowSeededCounter_LeavesSequenceSkippedAheadNullAsync()
    {
        var ids = await SeedAsync();
        await SeedSequenceCounterAsync(ids.MvAttrId, nextValue: 100);
        var (cs, csType, mvType, mvAttr) = await LoadDetachedAsync(ids);
        var initiator = await LoadInitiatorAsync(ids);
        // Start value (1) sits below the seeded counter (100); nothing should move.
        var rule = BuildImportRuleWithSequenceMapping(cs, csType, mvType, mvAttr, sequenceStart: 1);

        await using (var ctx = NewContext())
        {
            var jim = CreateApplication(ctx);
            var ok = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, initiator);
            Assert.That(ok, Is.True);
        }

        Assert.That(rule.AttributeFlowRules.Single().Generation!.SequenceSkippedAhead, Is.Null);
        Assert.That(await ReadSequenceCounterAsync(ids.MvAttrId), Is.EqualTo(100), "a start value at or below the counter must not move it.");
    }
}
