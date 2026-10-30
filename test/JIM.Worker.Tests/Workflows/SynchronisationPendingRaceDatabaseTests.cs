// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL synchronisation runs for the Delta Synchronisation watermark race (#2092). Each test commits an
/// import's change to a Connected System Object on its own connection while a synchronisation of the same Connected
/// System is in flight (from the processor's per-object hook, or between two repository calls), then asserts that the
/// change reaches the Metaverse Object, by the same run or the next. Before the fix every one of these changes was
/// skipped silently: its timestamps sat below the watermark the run then wrote, and Full Synchronisation's unchanged
/// skip used the same watermark. The mark's guard depends on <c>xmin</c>, so only PostgreSQL can prove it.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationPendingRaceDatabaseTests
{
    private const int PageSize = 2;

    private string _connectionString = null!;
    private int _systemId;
    private int _externalIdAttributeId;
    private int _displayNameAttributeId;
    private int _mvDisplayNameAttributeId;

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); a synchronisation run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL synchronisation race tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedConfigurationAsync();
    }

    #region Harness

    /// <summary>
    /// The Id of the n-th test object. PostgreSQL orders uuids bytewise on their canonical text, so these sort in n
    /// order, which lets a test place an object below or above the run's keyset cursor.
    /// </summary>
    private static Guid IdOf(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    /// <summary>
    /// One Connected System whose import Synchronisation Rule projects every object and flows DisplayName into the
    /// Metaverse; page size 2, so a handful of objects spans several pages; change tracking off.
    /// </summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, PageSize.ToString()),
                     ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                     ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "false")
                 })
        {
            seed.ServiceSettingItems.Add(new ServiceSetting
            {
                Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                ValueType = valueType, DefaultValue = value, Value = value
            });
        }

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var externalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var displayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        type.Attributes.Add(externalId);
        type.Attributes.Add(displayName);

        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType]
        };

        seed.AddRange(connectorDefinition, system, type, mvType, mvDisplayName);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });
        await seed.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule, TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayName } }
        });
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        _externalIdAttributeId = externalId.Id;
        _displayNameAttributeId = displayName.Id;
        _mvDisplayNameAttributeId = mvDisplayName.Id;
    }

    /// <summary>Creates the objects through the import's own create writer, created two hours ago.</summary>
    private Task CreateObjectsAsync(params int[] ns) => CreateObjectsCreatedAtAsync(DateTime.UtcNow.AddHours(-2), ns);

    /// <summary>Creates the objects through the import's own create writer, created at <paramref name="created"/>.</summary>
    private async Task CreateObjectsCreatedAtAsync(DateTime created, params int[] ns)
    {
        await using var ctx = NewContext();
        var objects = ns.Select(n =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = IdOf(n), ConnectedSystemId = _systemId, TypeId = TypeIdOf(ctx), ExternalIdAttributeId = _externalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeId, StringValue = $"E{n}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = $"Name {n}" });
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(objects);
    }

    private int TypeIdOf(JimDbContext ctx) =>
        ctx.ConnectedSystemObjectTypes.AsNoTracking().Single(t => t.ConnectedSystemId == _systemId).Id;

    /// <summary>
    /// An import committing a DisplayName change on its own connection, through the import's own update writer.
    /// <paramref name="stampedAt"/> is the object's new LastUpdated: import stamps an object while processing it, long
    /// before the batch commits, so the stamp can be older than the moment the change becomes visible.
    /// </summary>
    private async Task ImportDisplayNameChangeAsync(int n, string newDisplayName, DateTime stampedAt)
    {
        await using var ctx = NewContext();
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues)
            .SingleAsync(c => c.Id == IdOf(n));
        var previous = cso.AttributeValues.Single(av => av.AttributeId == _displayNameAttributeId);
        var replacement = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = newDisplayName };
        cso.LastUpdated = stampedAt;
        await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync([cso], [(cso.Id, replacement)], [previous.Id]);
    }

    /// <summary>The DisplayName on the Metaverse Object the n-th object is joined to, or null if it is not joined.</summary>
    private async Task<string?> MetaverseDisplayNameAsync(int n)
    {
        await using var ctx = NewContext();
        var mvoId = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == IdOf(n)).Select(c => c.MetaverseObjectId).SingleAsync();
        if (mvoId == null)
            return null;
        return await ctx.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => av.MetaverseObject.Id == mvoId && av.AttributeId == _mvDisplayNameAttributeId)
            .Select(av => av.StringValue)
            .SingleOrDefaultAsync();
    }

    private async Task<DateTime?> WatermarkAsync()
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystems.AsNoTracking().Where(cs => cs.Id == _systemId).Select(cs => cs.LastSyncCompletedAt).SingleAsync();
    }

    private sealed record RunResult(Activity Activity, List<Guid> ProcessedIds);

    /// <summary>
    /// Runs one synchronisation on its own tracking context, as the Worker does. <paramref name="onObjectProcessed"/>
    /// runs after each object's Pass 2 with the ids processed so far; <paramref name="wrapRepository"/> lets a test act
    /// between two repository calls.
    /// </summary>
    private async Task<RunResult> RunAsync(ConnectedSystemRunType runType, Func<List<Guid>, Task>? onObjectProcessed = null,
        Func<ISyncRepository, ISyncRepository>? wrapRepository = null)
    {
        await using var ctx = NewContext(trackAll: true);
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        var syncRepository = wrapRepository?.Invoke(jim.SyncRepository) ?? jim.SyncRepository;
        var processed = new List<Guid>();

        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository,
                system, runProfile, activity, new CancellationTokenSource());
            await processor.PerformFullSyncAsync();
        }
        else
        {
            var processor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository,
                system, runProfile, activity, new CancellationTokenSource());
            processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
            {
                processed.Add(cso.Id);
                if (onObjectProcessed != null)
                    onObjectProcessed(processed).GetAwaiter().GetResult();
            };
            await processor.PerformDeltaSyncAsync();
        }

        var errors = activity.RunProfileExecutionItems.Where(i => i.ErrorType is { } e && e != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        return new RunResult(activity, processed);
    }

    /// <summary>
    /// Objects 1 to <paramref name="count"/> created and synchronised by a Full Synchronisation, so each is joined to a
    /// projected Metaverse Object carrying its original DisplayName, and the watermark is set.
    /// </summary>
    private async Task ArrangeSynchronisedObjectsAsync(int count)
    {
        await CreateObjectsAsync(Enumerable.Range(1, count).ToArray());
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        Assert.That(await MetaverseDisplayNameAsync(1), Is.EqualTo("Name 1"), "precondition: the Full Synchronisation projected and flowed");
        Assert.That(await WatermarkAsync(), Is.Not.Null, "precondition: the watermark is set");
    }

    #endregion

    /// <summary>
    /// Objects an import created just before the run are handed out by their type's walk, and then again by the closing
    /// sweep, which drops them as already handed out (#2598). By then the run has joined them, so the sweep's load hung
    /// their Metaverse Objects, untracked, off its tracked Connected System Objects; with nothing left to process the
    /// loop ended without clearing them, and the run's next save attached them as Modified and failed on their row
    /// version (Scenario 008 at Medium).
    /// </summary>
    [Test]
    public async Task FullSync_SweepReloadsOnlyObjectsAlreadyHandedOut_CompletesAndSavesTheRunAsync()
    {
        await CreateObjectsCreatedAtAsync(DateTime.UtcNow, 1, 2, 3);

        await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await MetaverseDisplayNameAsync(3), Is.EqualTo("Name 3"), "the run projected and flowed every object");
            Assert.That(await WatermarkAsync(), Is.Not.Null, "the run completed and wrote its watermark");
        }
    }

    /// <summary>
    /// The Delta Synchronisation counterpart of the test above: its closing sweep reloads changed objects created since
    /// the run's floor, all of them already handed out by their type's walk (#2598).
    /// </summary>
    [Test]
    public async Task DeltaSync_SweepReloadsOnlyObjectsAlreadyHandedOut_CompletesAndSavesTheRunAsync()
    {
        await CreateObjectsCreatedAtAsync(DateTime.UtcNow, 1, 2, 3);
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        foreach (var n in new[] { 1, 2, 3 })
            await ImportDisplayNameChangeAsync(n, $"Renamed {n}", DateTime.UtcNow);

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MetaverseDisplayNameAsync(3), Is.EqualTo("Renamed 3"), "the run processed every changed object");
    }

    /// <summary>
    /// (a) A joined object the run has already processed: the import's change commits after the object's page was
    /// processed, before the run writes its watermark. The next Delta Synchronisation processes it.
    /// </summary>
    [Test]
    public async Task DeltaSync_ImportUpdateCommittedAfterItsObjectWasProcessed_IsProcessedByTheNextDeltaSyncAsync()
    {
        await ArrangeSynchronisedObjectsAsync(4);
        var now = DateTime.UtcNow;
        foreach (var n in new[] { 1, 2, 3, 4 })
            await ImportDisplayNameChangeAsync(n, $"Renamed {n}", now);

        var injected = false;
        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, async processed =>
        {
            if (injected || !processed.Contains(IdOf(1)))
                return;
            injected = true;
            await ImportDisplayNameChangeAsync(1, "Renamed again 1", DateTime.UtcNow);
        });
        Assert.That(run.ProcessedIds, Does.Contain(IdOf(1)), "precondition: the run processed object 1 before the change committed");
        Assert.That(await MetaverseDisplayNameAsync(1), Is.EqualTo("Renamed 1"), "precondition: the run flowed the values it loaded");

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MetaverseDisplayNameAsync(1), Is.EqualTo("Renamed again 1"),
            "the change committed during the previous run must reach the Metaverse Object");
    }

    /// <summary>
    /// (b) An object outside the run's starting selection whose Id is below the cursor when its change commits. The
    /// next Delta Synchronisation processes it.
    /// </summary>
    [Test]
    public async Task DeltaSync_ImportUpdateToAnUnselectedObjectBelowTheCursor_IsProcessedByTheNextDeltaSyncAsync()
    {
        await ArrangeSynchronisedObjectsAsync(6);
        var now = DateTime.UtcNow;
        foreach (var n in new[] { 3, 4, 5, 6 })
            await ImportDisplayNameChangeAsync(n, $"Renamed {n}", now);

        var injected = false;
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, async processed =>
        {
            if (injected || !processed.Contains(IdOf(4)))
                return;
            injected = true;
            await ImportDisplayNameChangeAsync(1, "Renamed 1", DateTime.UtcNow);
        });
        Assert.That(injected, Is.True, "precondition: the change committed during the run");

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MetaverseDisplayNameAsync(1), Is.EqualTo("Renamed 1"),
            "a change to an object below the cursor, committed during the previous run, must reach the Metaverse Object");
    }

    /// <summary>
    /// (c) An object outside the starting selection whose Id is above the cursor and beyond the page budget the
    /// starting count implied. Paging until a page comes back empty processes it in the same run, and the run's
    /// processed count exceeding its estimate is not an error.
    /// </summary>
    [Test]
    public async Task DeltaSync_ImportUpdateToAnUnselectedObjectBeyondThePageBudget_IsProcessedByTheSameRunAsync()
    {
        await ArrangeSynchronisedObjectsAsync(6);
        var now = DateTime.UtcNow;
        foreach (var n in new[] { 1, 2 })
            await ImportDisplayNameChangeAsync(n, $"Renamed {n}", now);

        var injected = false;
        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, async processed =>
        {
            if (injected)
                return;
            injected = true;
            await ImportDisplayNameChangeAsync(6, "Renamed 6", DateTime.UtcNow);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Activity.ObjectsToProcess, Is.EqualTo(2), "precondition: one page was counted at the start");
            Assert.That(run.ProcessedIds, Does.Contain(IdOf(6)), "the object that entered the selection above the cursor is processed by the same run");
            Assert.That(run.Activity.ObjectsProcessed, Is.GreaterThan(run.Activity.ObjectsToProcess), "processing more than the estimate is allowed");
            Assert.That(await MetaverseDisplayNameAsync(6), Is.EqualTo("Renamed 6"));
        }
    }

    /// <summary>
    /// The same mid-run change to a joined, Normal object is processed by the next Full Synchronisation while its
    /// unchanged-object optimisation is active (configuration unchanged since the last Full Synchronisation).
    /// </summary>
    [Test]
    public async Task FullSync_AfterImportUpdateCommittedDuringADeltaSync_ProcessesItWithTheUnchangedSkipActiveAsync()
    {
        await ArrangeSynchronisedObjectsAsync(4);
        var now = DateTime.UtcNow;
        foreach (var n in new[] { 1, 2, 3, 4 })
            await ImportDisplayNameChangeAsync(n, $"Renamed {n}", now);

        var injected = false;
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, async processed =>
        {
            if (injected || !processed.Contains(IdOf(1)))
                return;
            injected = true;
            await ImportDisplayNameChangeAsync(1, "Renamed again 1", DateTime.UtcNow);
        });

        await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        Assert.That(await MetaverseDisplayNameAsync(1), Is.EqualTo("Renamed again 1"),
            "Full Synchronisation's unchanged skip must not skip an object with a change no synchronisation has processed");
    }

    /// <summary>
    /// A change stamped before the synchronisation started (and before the previous watermark, even), but committed
    /// after its page was loaded, is still processed: the fix does not rely on timestamps.
    /// </summary>
    [Test]
    public async Task DeltaSync_ChangeStampedBeforeTheRunButCommittedAfterItsPageLoad_IsProcessedByTheNextDeltaSyncAsync()
    {
        await ArrangeSynchronisedObjectsAsync(4);
        var stampedLongAgo = DateTime.UtcNow.AddHours(-1);
        var now = DateTime.UtcNow;
        foreach (var n in new[] { 1, 2, 3, 4 })
            await ImportDisplayNameChangeAsync(n, $"Renamed {n}", now);

        var injected = false;
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, async processed =>
        {
            if (injected || !processed.Contains(IdOf(1)))
                return;
            injected = true;
            await ImportDisplayNameChangeAsync(2, "Renamed again 2", stampedLongAgo);
        });

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MetaverseDisplayNameAsync(2), Is.EqualTo("Renamed again 2"),
            "a change committed after its page was loaded must be processed whatever its timestamp says");
    }

    /// <summary>
    /// The no-change fast path counts nothing to do, then writes the watermark. A change committed between the two
    /// (here, while the fast path's last check runs, before the watermark's timestamp is taken) survives to the next
    /// run.
    /// </summary>
    [Test]
    public async Task DeltaSync_ChangeCommittedBetweenTheFastPathCountAndItsWatermarkWrite_IsProcessedByTheNextRunAsync()
    {
        await ArrangeSynchronisedObjectsAsync(2);

        var injected = false;
        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, wrapRepository: inner =>
            InterceptingSyncRepositoryProxy.Create(inner, nameof(ISyncRepository.AnyGeneratedValueRevisionsPendingAsync), () =>
            {
                if (injected)
                    return;
                injected = true;
                ImportDisplayNameChangeAsync(1, "Renamed 1", DateTime.UtcNow).GetAwaiter().GetResult();
            }));
        Assert.That(run.ProcessedIds, Is.Empty, "precondition: the fast path ran, processing nothing");
        Assert.That(injected, Is.True, "precondition: the change committed before the watermark write");

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MetaverseDisplayNameAsync(1), Is.EqualTo("Renamed 1"));
    }

    /// <summary>
    /// The run clears the mark on what it processed, folded into its own join-state write: an object the run newly
    /// joins is not processed again by the next Delta Synchronisation.
    /// </summary>
    [Test]
    public async Task DeltaSync_NewlyProjectedObjects_AreNotProcessedAgainByTheNextDeltaSyncAsync()
    {
        await ArrangeSynchronisedObjectsAsync(2);
        await CreateObjectsAsync(3, 4, 5);

        var first = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        Assert.That(first.ProcessedIds, Is.EquivalentTo(new[] { IdOf(3), IdOf(4), IdOf(5) }), "precondition: the new objects are processed");
        Assert.That(await MetaverseDisplayNameAsync(3), Is.EqualTo("Name 3"), "precondition: and projected");

        var second = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        await using var ctx = NewContext();
        var stillMarked = await ctx.ConnectedSystemObjects.AsNoTracking().CountAsync(c => c.SynchronisationPending);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.ProcessedIds, Is.Empty, "nothing changed since the previous run");
            Assert.That(stillMarked, Is.Zero, "every processed object's mark was cleared");
        }
    }
}
