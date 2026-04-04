using JIM.Application.Servers;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Tests for safe cancellation behaviour during sync operations.
/// Verifies that when cancellation is requested, the current page's flush pipeline
/// completes before exiting — preventing orphaned MVOs without pending exports.
/// </summary>
[TestFixture]
public class SyncCancellationTests : WorkflowTestBase
{
    /// <summary>
    /// THE core bug test: cancellation fires mid-page during Pass 2, after one CSO has been
    /// processed (projected to the metaverse). With the old code, <c>return</c> skips the
    /// flush pipeline and the MVO is never persisted. With the fix, <c>break</c> falls through
    /// to the flush, persisting the MVO.
    /// </summary>
    [Test]
    public async Task FullSync_CancelledDuringPass2_FlushesPageAndExitsWithoutWatermarkAsync()
    {
        // Arrange: 6 CSOs across 2 pages (page size 3)
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        // Add attribute flow so projection creates MVO attribute values (proves flush worked)
        await AddAttributeFlowAsync(syncRule, "DisplayName", "DisplayName");

        var csos = await CreateCsosAsync(connectedSystem.Id, csoType, 6);

        // Override page size to 3 so we get 2 pages
        await SetSyncPageSizeAsync(3);

        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, syncProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            new SyncEngine(),
            new SyncServer(Jim),
            SyncRepo,
            connectedSystem,
            syncProfile,
            activity,
            cts);

        // Cancel after the 1st CSO is processed in Pass 2 (mid-page)
        var csoProcessedCount = 0;
        processor.OnCsoProcessedInPass2 = () =>
        {
            csoProcessedCount++;
            if (csoProcessedCount == 1)
                cts.Cancel();
        };

        // Act
        await processor.PerformFullSyncAsync();

        // Assert: The 1st CSO's MVO should be flushed (proves the flush pipeline ran)
        var mvoCount = SyncRepo.MetaverseObjects.Count;
        Assert.That(mvoCount, Is.GreaterThanOrEqualTo(1),
            "At least 1 MVO should be persisted — the flush pipeline must run for objects " +
            "processed before cancellation was detected");

        // Assert: Not all CSOs were processed (cancellation stopped early)
        Assert.That(activity.ObjectsProcessed, Is.LessThan(6),
            "Cancellation should stop processing before all 6 CSOs are complete");

        // Assert: Watermark must NOT be updated (so next sync re-processes)
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.Null,
            "Watermark must NOT be set on cancellation — the next sync must re-process");

        // Assert: Only 1 CSO was processed (the one before cancellation fired)
        Assert.That(csoProcessedCount, Is.EqualTo(1),
            "Only 1 CSO should have been processed before cancellation was detected");
    }

    /// <summary>
    /// Same as the full sync test but for delta sync — verifies both processors are fixed.
    /// </summary>
    [Test]
    public async Task DeltaSync_CancelledDuringPass2_FlushesPageAndExitsWithoutWatermarkAsync()
    {
        // Arrange: First run a full sync to establish baseline, then modify CSOs for delta
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        await AddAttributeFlowAsync(syncRule, "DisplayName", "DisplayName");

        var csos = await CreateCsosAsync(connectedSystem.Id, csoType, 6);

        await SetSyncPageSizeAsync(3);

        // Run full sync first to establish baseline
        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var fullSyncCts = new CancellationTokenSource();
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, fullSyncProfile, fullSyncActivity, fullSyncCts);
        await fullSyncProcessor.PerformFullSyncAsync();

        // Record the watermark from full sync
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        var watermarkAfterFullSync = connectedSystem.LastSyncCompletedAt;
        Assert.That(watermarkAfterFullSync, Is.Not.Null, "Full sync should set watermark");

        // Wait to ensure modifications are after the watermark
        await Task.Delay(100);

        // Modify all 6 CSOs to trigger delta processing
        foreach (var cso in csos)
            await ModifyCsoAsync(cso);

        // Run delta sync with cancellation after 1st CSO
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncActivity = await CreateActivityAsync(
            connectedSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaCts = new CancellationTokenSource();
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, deltaSyncProfile, deltaSyncActivity, deltaCts);

        var csoProcessedCount = 0;
        deltaSyncProcessor.OnCsoProcessedInPass2 = () =>
        {
            csoProcessedCount++;
            if (csoProcessedCount == 1)
                deltaCts.Cancel();
        };

        // Act
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Watermark must NOT have advanced (should still be the full sync watermark)
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.EqualTo(watermarkAfterFullSync),
            "Watermark must NOT advance on cancellation — next delta sync must re-process");

        // Assert: Not all CSOs were processed
        Assert.That(deltaSyncActivity.ObjectsProcessed, Is.LessThan(6),
            "Cancellation should stop processing before all 6 CSOs are complete");
    }

    /// <summary>
    /// Verifies that pre-cancelled CTS causes immediate exit without processing.
    /// </summary>
    [Test]
    public async Task FullSync_CancelledBeforeProcessing_ExitsImmediatelyAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        await CreateCsosAsync(connectedSystem.Id, csoType, 10);

        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, syncProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var processor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, syncProfile, activity, cts);

        // Act
        await processor.PerformFullSyncAsync();

        // Assert: No MVOs created, no watermark
        Assert.That(SyncRepo.MetaverseObjects.Count, Is.EqualTo(0),
            "No MVOs should be created when CTS is pre-cancelled");
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.Null,
            "Watermark must not be set on cancellation");
    }

    /// <summary>
    /// Regression test: verifies normal sync still updates watermark correctly.
    /// </summary>
    [Test]
    public async Task FullSync_CompletesNormally_UpdatesWatermarkAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        await CreateCsosAsync(connectedSystem.Id, csoType, 5);

        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, syncProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, syncProfile, activity, cts);

        // Act
        await processor.PerformFullSyncAsync();

        // Assert: All MVOs created and watermark set
        Assert.That(SyncRepo.MetaverseObjects.Count, Is.EqualTo(5),
            "All 5 CSOs should project to MVOs");
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.Not.Null,
            "Watermark must be set after successful sync");
    }

    /// <summary>
    /// Verifies that pre-cancelled CTS causes immediate exit for delta sync.
    /// </summary>
    [Test]
    public async Task DeltaSync_CancelledBeforeProcessing_ExitsImmediatelyAsync()
    {
        // Arrange: Run full sync first to establish watermark
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        var csos = await CreateCsosAsync(connectedSystem.Id, csoType, 5);

        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var fullSyncCts = new CancellationTokenSource();
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, fullSyncProfile, fullSyncActivity, fullSyncCts);
        await fullSyncProcessor.PerformFullSyncAsync();

        connectedSystem = await ReloadEntityAsync(connectedSystem);
        var watermarkAfterFullSync = connectedSystem.LastSyncCompletedAt;

        await Task.Delay(100);

        // Modify all CSOs so delta sync has work to do
        foreach (var cso in csos)
            await ModifyCsoAsync(cso);

        // Pre-cancel the delta sync CTS
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncActivity = await CreateActivityAsync(
            connectedSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaCts = new CancellationTokenSource();
        deltaCts.Cancel(); // Pre-cancel

        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, deltaSyncProfile, deltaSyncActivity, deltaCts);

        // Act
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Watermark unchanged, no additional processing
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.EqualTo(watermarkAfterFullSync),
            "Watermark must not advance on pre-cancelled delta sync");
        Assert.That(deltaSyncActivity.ObjectsProcessed, Is.EqualTo(0),
            "No objects should be processed when CTS is pre-cancelled");
    }

    #region Helper Methods

    /// <summary>
    /// Overrides the Sync.PageSize service setting for testing with small pages.
    /// </summary>
    private async Task SetSyncPageSizeAsync(int pageSize)
    {
        var setting = DbContext.ServiceSettingItems.FirstOrDefault(s => s.Key == "Sync.PageSize");
        if (setting != null)
        {
            setting.Value = pageSize.ToString();
            await DbContext.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Adds an attribute flow mapping to a sync rule.
    /// Follows the same pattern as NugatoryWorkOptimisationTests.
    /// </summary>
    private Task AddAttributeFlowAsync(
        JIM.Models.Logic.SyncRule syncRule,
        string csoAttributeName,
        string mvAttributeName)
    {
        var csoType = syncRule.ConnectedSystemObjectType!;
        var csoAttr = csoType.Attributes.First(a => a.Name == csoAttributeName);
        var mvAttr = syncRule.MetaverseObjectType!.Attributes.First(a => a.Name == mvAttributeName);

        syncRule.AttributeFlowRules.Add(new JIM.Models.Logic.SyncRuleMapping
        {
            SyncRule = syncRule,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            Sources = { new JIM.Models.Logic.SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoAttr,
                ConnectedSystemAttributeId = csoAttr.Id
            }}
        });

        return Task.CompletedTask;
    }

    #endregion
}
