using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for synchronisation processes.
/// These tests verify that Full Sync and Delta Sync work correctly together,
/// particularly testing the watermark mechanism that was previously buggy.
///
/// THE BUG THESE TESTS CATCH:
/// Before the fix, Full Sync did not set LastDeltaSyncCompletedAt (the watermark).
/// This meant Delta Sync would use DateTime.MinValue as its baseline, causing it
/// to process ALL CSOs instead of just modified ones - a massive performance problem.
///
/// These workflow tests would have caught that bug by verifying:
/// 1. Full Sync sets the watermark
/// 2. Delta Sync uses the watermark correctly
/// 3. Only modified CSOs are processed in Delta Sync
/// </summary>
[TestFixture]
public class SyncWorkflowTests : WorkflowTestBase
{
    /// <summary>
    /// Verifies that Full Sync sets the LastDeltaSyncCompletedAt watermark.
    /// This is the fundamental requirement for Delta Sync to work efficiently.
    /// </summary>
    [Test]
    public async Task FullSync_WhenCompleted_SetsWatermarkAsync()
    {
        // Arrange: Create a Connected System with CSOs
        var connectedSystem = await CreateConnectedSystemAsync("Test HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        // Create some CSOs
        await CreateCsosAsync(connectedSystem.Id, csoType, 10);

        // Create run profile and activity
        var syncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Full Sync",
            ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(
            connectedSystem.Id,
            syncProfile,
            ConnectedSystemRunType.FullSynchronisation);

        // Verify watermark is initially null
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Null,
            "Watermark should be null before first sync");

        // Act: Run Full Sync
        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            Jim,
            connectedSystem,
            syncProfile,
            activity,
            cts);

        await processor.PerformFullSyncAsync();

        // Assert: Watermark should now be set
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null,
            "Full Sync MUST set the watermark (LastDeltaSyncCompletedAt) for Delta Sync to work efficiently");
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt!.Value,
            Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)),
            "Watermark should be set to approximately the current time");
    }

    /// <summary>
    /// Verifies that Delta Sync processes zero CSOs when nothing has changed since last sync.
    /// This is the key performance test - if the watermark bug exists, this would process ALL CSOs.
    /// </summary>
    [Test]
    public async Task DeltaSync_WithNoModifications_ProcessesZeroCsosAsync()
    {
        // Arrange: Create a Connected System with CSOs
        var connectedSystem = await CreateConnectedSystemAsync("Test HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        // Create 100 CSOs (enough to notice a performance problem)
        await CreateCsosAsync(connectedSystem.Id, csoType, 100);

        // Create run profiles
        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Full Sync",
            ConnectedSystemRunType.FullSynchronisation);
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Delta Sync",
            ConnectedSystemRunType.DeltaSynchronisation);

        // Run Full Sync first to establish baseline
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            fullSyncProfile,
            ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            Jim,
            connectedSystem,
            fullSyncProfile,
            fullSyncActivity,
            cts1);

        await fullSyncProcessor.PerformFullSyncAsync();

        // Reload to get the updated watermark
        connectedSystem = await ReloadEntityAsync(connectedSystem);

        // Act: Run Delta Sync with no changes
        var deltaSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            deltaSyncProfile,
            ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            Jim,
            connectedSystem,
            deltaSyncProfile,
            deltaSyncActivity,
            cts2);

        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Delta Sync should process 0 CSOs (no changes since Full Sync)
        deltaSyncActivity = await ReloadEntityAsync(deltaSyncActivity);
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(0),
            "Delta Sync should find 0 CSOs to process when nothing changed. " +
            "If this fails and shows 100, the watermark is not being used correctly!");
    }

    /// <summary>
    /// Verifies that Delta Sync only processes CSOs modified after the watermark.
    /// This is THE critical test for the watermark bug.
    /// </summary>
    [Test]
    public async Task DeltaSync_WithOneModifiedCso_ProcessesOnlyOneCsoAsync()
    {
        // Arrange: Create a Connected System with CSOs
        var connectedSystem = await CreateConnectedSystemAsync("Test HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        // Create 100 CSOs
        var csos = await CreateCsosAsync(connectedSystem.Id, csoType, 100);

        // Create run profiles
        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Full Sync",
            ConnectedSystemRunType.FullSynchronisation);
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Delta Sync",
            ConnectedSystemRunType.DeltaSynchronisation);

        // Run Full Sync first
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            fullSyncProfile,
            ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            Jim,
            connectedSystem,
            fullSyncProfile,
            fullSyncActivity,
            cts1);

        await fullSyncProcessor.PerformFullSyncAsync();
        connectedSystem = await ReloadEntityAsync(connectedSystem);

        // Wait a moment to ensure watermark is in the past
        await Task.Delay(100);

        // Modify ONLY 1 CSO (the 50th one)
        await ModifyCsoAsync(csos[49]);

        // Act: Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            deltaSyncProfile,
            ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            Jim,
            connectedSystem,
            deltaSyncProfile,
            deltaSyncActivity,
            cts2);

        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Delta Sync should process only 1 CSO
        deltaSyncActivity = await ReloadEntityAsync(deltaSyncActivity);
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(1),
            "Delta Sync should process ONLY 1 CSO (the modified one). " +
            "If this shows 100, it means ALL CSOs are being processed - the watermark bug!");
    }

    /// <summary>
    /// Verifies that Delta Sync correctly processes multiple modified CSOs.
    /// </summary>
    [Test]
    public async Task DeltaSync_WithMultipleModifiedCsos_ProcessesOnlyModifiedCsosAsync()
    {
        // Arrange: Create a Connected System with CSOs
        var connectedSystem = await CreateConnectedSystemAsync("Test HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        // Create 50 CSOs
        var csos = await CreateCsosAsync(connectedSystem.Id, csoType, 50);

        // Create run profiles
        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Full Sync",
            ConnectedSystemRunType.FullSynchronisation);
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Delta Sync",
            ConnectedSystemRunType.DeltaSynchronisation);

        // Run Full Sync first
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            fullSyncProfile,
            ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            Jim,
            connectedSystem,
            fullSyncProfile,
            fullSyncActivity,
            cts1);

        await fullSyncProcessor.PerformFullSyncAsync();
        connectedSystem = await ReloadEntityAsync(connectedSystem);

        // Wait a moment
        await Task.Delay(100);

        // Modify 5 CSOs
        await ModifyCsoAsync(csos[0]);
        await ModifyCsoAsync(csos[10]);
        await ModifyCsoAsync(csos[20]);
        await ModifyCsoAsync(csos[30]);
        await ModifyCsoAsync(csos[40]);

        // Act: Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            deltaSyncProfile,
            ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            Jim,
            connectedSystem,
            deltaSyncProfile,
            deltaSyncActivity,
            cts2);

        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Should process exactly 5 CSOs
        deltaSyncActivity = await ReloadEntityAsync(deltaSyncActivity);
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(5),
            "Delta Sync should process exactly 5 CSOs (the modified ones), not all 50");
    }

    // NOTE: Test for "newly created CSO after sync" removed due to EF Core InMemory tracking limitations.
    // Creating new CSOs after sync operations run causes entity tracking conflicts.
    // This scenario is tested in integration tests with a real database.

    /// <summary>
    /// Verifies that Delta Sync updates the watermark after completion.
    /// This ensures subsequent delta syncs have a proper baseline.
    /// </summary>
    [Test]
    public async Task DeltaSync_WhenCompleted_UpdatesWatermarkAsync()
    {
        // Arrange: Create a Connected System with CSOs
        var connectedSystem = await CreateConnectedSystemAsync("Test HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        await CreateCsosAsync(connectedSystem.Id, csoType, 5);

        // Create run profiles
        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Full Sync",
            ConnectedSystemRunType.FullSynchronisation);
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Delta Sync",
            ConnectedSystemRunType.DeltaSynchronisation);

        // Run Full Sync first
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            fullSyncProfile,
            ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            Jim,
            connectedSystem,
            fullSyncProfile,
            fullSyncActivity,
            cts1);

        await fullSyncProcessor.PerformFullSyncAsync();
        connectedSystem = await ReloadEntityAsync(connectedSystem);

        var watermarkAfterFullSync = connectedSystem.LastDeltaSyncCompletedAt;

        // Wait a moment
        await Task.Delay(100);

        // Act: Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            deltaSyncProfile,
            ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            Jim,
            connectedSystem,
            deltaSyncProfile,
            deltaSyncActivity,
            cts2);

        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Watermark should be updated
        connectedSystem = await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.GreaterThan(watermarkAfterFullSync!.Value),
            "Delta Sync should advance the watermark forward");
    }

    /// <summary>
    /// Verifies that consecutive delta syncs work correctly.
    /// Each delta sync should only process CSOs modified since the PREVIOUS delta sync.
    /// </summary>
    [Test]
    public async Task ConsecutiveDeltaSyncs_EachProcessOnlyNewModificationsAsync()
    {
        // Arrange: Create a Connected System with CSOs
        var connectedSystem = await CreateConnectedSystemAsync("Test HR System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var csos = await CreateCsosAsync(connectedSystem.Id, csoType, 20);

        // Create run profiles
        var fullSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Full Sync",
            ConnectedSystemRunType.FullSynchronisation);
        var deltaSyncProfile = await CreateRunProfileAsync(
            connectedSystem.Id,
            "Delta Sync",
            ConnectedSystemRunType.DeltaSynchronisation);

        // Run Full Sync
        var fullSyncActivity = await CreateActivityAsync(
            connectedSystem.Id,
            fullSyncProfile,
            ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, connectedSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();
        connectedSystem = await ReloadEntityAsync(connectedSystem);

        // Wait then modify 3 CSOs
        await Task.Delay(100);
        await ModifyCsoAsync(csos[0]);
        await ModifyCsoAsync(csos[1]);
        await ModifyCsoAsync(csos[2]);

        // First Delta Sync
        var deltaSyncActivity1 = await CreateActivityAsync(
            connectedSystem.Id,
            deltaSyncProfile,
            ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, connectedSystem, deltaSyncProfile, deltaSyncActivity1, cts2)
            .PerformDeltaSyncAsync();
        deltaSyncActivity1 = await ReloadEntityAsync(deltaSyncActivity1);
        connectedSystem = await ReloadEntityAsync(connectedSystem);

        Assert.That(deltaSyncActivity1.ObjectsToProcess, Is.EqualTo(3),
            "First Delta Sync should process 3 modified CSOs");

        // Wait then modify 2 different CSOs
        await Task.Delay(100);
        await ModifyCsoAsync(csos[10]);
        await ModifyCsoAsync(csos[11]);

        // Second Delta Sync
        var deltaSyncActivity2 = await CreateActivityAsync(
            connectedSystem.Id,
            deltaSyncProfile,
            ConnectedSystemRunType.DeltaSynchronisation);
        var cts3 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, connectedSystem, deltaSyncProfile, deltaSyncActivity2, cts3)
            .PerformDeltaSyncAsync();
        deltaSyncActivity2 = await ReloadEntityAsync(deltaSyncActivity2);

        Assert.That(deltaSyncActivity2.ObjectsToProcess, Is.EqualTo(2),
            "Second Delta Sync should process only 2 CSOs modified since first delta sync, " +
            "not the original 3 that were already processed");
    }
}
