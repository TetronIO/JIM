using JIM.Application.Servers;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Explicit (slow) tests for cancellation scenarios with larger datasets and artificial delays.
/// These verify realistic cancellation behaviour that cannot be tested deterministically
/// with fast unit tests. Run on demand only.
/// </summary>
[TestFixture]
public class SyncCancellationExplicitTests : WorkflowTestBase
{
    /// <summary>
    /// Simulates mid-sync cancellation with a large dataset across multiple pages.
    /// Creates 50 CSOs with page size 10 (5 pages), cancels during page 3.
    /// Verifies: pages 1–2 fully flushed, page 3 partially flushed, pages 4–5 untouched,
    /// watermark NOT updated.
    /// </summary>
    [Test, Explicit("Slow test with large dataset — run on demand")]
    public async Task FullSync_CancelledMidRunWithLargeDataset_FlushesPartialAndExitsAsync()
    {
        // Arrange: 50 CSOs with page size 10 → 5 pages
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        AddAttributeFlow(syncRule, "DisplayName", "DisplayName");

        await CreateCsosAsync(connectedSystem.Id, csoType, 50);
        await SetSyncPageSizeAsync(10);

        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, syncProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, syncProfile, activity, cts);

        // Cancel after 25 CSOs (mid-way through page 3)
        var csoProcessedCount = 0;
        processor.OnCsoProcessedInPass2 = () =>
        {
            csoProcessedCount++;
            if (csoProcessedCount == 25)
                cts.Cancel();
        };

        // Act
        await processor.PerformFullSyncAsync();

        // Assert: At least 25 MVOs should exist (pages 1-2 = 20, plus some from page 3)
        var mvoCount = SyncRepo.MetaverseObjects.Count;
        Assert.That(mvoCount, Is.GreaterThanOrEqualTo(25),
            "At least 25 MVOs should be persisted (2 full pages + partial page 3)");
        Assert.That(mvoCount, Is.LessThan(50),
            "Not all 50 MVOs should be persisted — pages 4-5 were not processed");

        // Assert: Watermark NOT set
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.Null,
            "Watermark must not be set on cancelled sync");

        // Assert: Activity shows partial processing
        Assert.That(activity.ObjectsProcessed, Is.GreaterThanOrEqualTo(25));
        Assert.That(activity.ObjectsProcessed, Is.LessThan(50));
    }

    /// <summary>
    /// Verifies that cancellation between pages produces fully consistent state:
    /// all pages that completed processing are fully flushed, and no partial state exists.
    /// </summary>
    [Test, Explicit("Slow test with multi-page verification — run on demand")]
    public async Task FullSync_CancelledBetweenPages_AllCompletedPagesFullyFlushedAsync()
    {
        // Arrange: 30 CSOs with page size 10 → 3 pages, cancel after page 2
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        AddAttributeFlow(syncRule, "DisplayName", "DisplayName");

        await CreateCsosAsync(connectedSystem.Id, csoType, 30);
        await SetSyncPageSizeAsync(10);

        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, syncProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, syncProfile, activity, cts);

        // Cancel after exactly 20 CSOs (end of page 2, before page 3 starts)
        var csoProcessedCount = 0;
        processor.OnCsoProcessedInPass2 = () =>
        {
            csoProcessedCount++;
            if (csoProcessedCount == 20)
                cts.Cancel();
        };

        // Act
        await processor.PerformFullSyncAsync();

        // Assert: Exactly 20 MVOs (pages 1-2 fully flushed)
        Assert.That(SyncRepo.MetaverseObjects.Count, Is.EqualTo(20),
            "Pages 1 and 2 should be fully flushed (20 MVOs)");

        // Assert: Each MVO has its CSO properly joined
        foreach (var mvo in SyncRepo.MetaverseObjects.Values)
        {
            var joinedCsos = SyncRepo.ConnectedSystemObjects.Values
                .Where(cso => cso.MetaverseObjectId == mvo.Id)
                .ToList();
            Assert.That(joinedCsos, Has.Count.EqualTo(1),
                $"MVO {mvo.Id} should have exactly 1 joined CSO");
        }

        // Assert: No orphaned MVOs without joined CSOs
        var orphanedMvos = SyncRepo.MetaverseObjects.Values
            .Where(mvo => !SyncRepo.ConnectedSystemObjects.Values.Any(cso => cso.MetaverseObjectId == mvo.Id))
            .ToList();
        Assert.That(orphanedMvos, Is.Empty,
            "No MVOs should exist without a joined CSO — this would indicate a partial flush");
    }

    /// <summary>
    /// Concurrent cancellation race condition test: cancel while the flush pipeline is running.
    /// Uses the OnCsoProcessedInPass2 hook to cancel late in processing, ensuring the flush
    /// pipeline starts with a pending cancellation. Verifies the flush still completes.
    /// </summary>
    [Test, Explicit("Race condition test — run on demand")]
    public async Task FullSync_CancelledDuringFlushPipeline_FlushStillCompletesAsync()
    {
        // Arrange: 10 CSOs with page size 10 (1 page), cancel on the last CSO
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");
        AddAttributeFlow(syncRule, "DisplayName", "DisplayName");

        await CreateCsosAsync(connectedSystem.Id, csoType, 10);
        await SetSyncPageSizeAsync(10);

        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id, syncProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            connectedSystem, syncProfile, activity, cts);

        // Cancel on the LAST CSO — all 10 are processed but cancellation fires before the
        // next loop iteration check. The flush should still run for all 10 objects.
        var csoProcessedCount = 0;
        processor.OnCsoProcessedInPass2 = () =>
        {
            csoProcessedCount++;
            if (csoProcessedCount == 10)
                cts.Cancel();
        };

        // Act
        await processor.PerformFullSyncAsync();

        // Assert: All 10 MVOs flushed (cancellation detected after last CSO, but flush runs)
        Assert.That(SyncRepo.MetaverseObjects.Count, Is.EqualTo(10),
            "All 10 MVOs should be flushed — cancellation fires after last CSO but before flush");

        // Assert: Watermark NOT set despite all objects processed
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastSyncCompletedAt, Is.Null,
            "Watermark must not be set even when all objects on the page were processed");
    }

    #region Helper Methods

    private async Task SetSyncPageSizeAsync(int pageSize)
    {
        var setting = DbContext.ServiceSettingItems.FirstOrDefault(s => s.Key == "Sync.PageSize");
        if (setting != null)
        {
            setting.Value = pageSize.ToString();
            await DbContext.SaveChangesAsync();
        }
    }

    private static void AddAttributeFlow(
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
    }

    #endregion
}
