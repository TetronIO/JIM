using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for synchronisation scenarios.
/// These tests verify end-to-end multi-step business processes work correctly together.
/// Unlike unit tests (which test individual methods with mocks), workflow tests use real implementations
/// with an in-memory database to test how components integrate.
/// Unlike integration tests (which test the full stack with Docker), workflow tests run entirely in-process.
/// </summary>
[TestFixture]
public class SyncWorkflowTests : WorkflowTestBase
{
    /// <summary>
    /// Critical test: Verifies that Full Sync sets the delta sync watermark,
    /// allowing subsequent Delta Syncs to only process modified CSOs.
    /// This test would have caught the bug where Full Sync didn't set LastDeltaSyncCompletedAt.
    /// </summary>
    [Test]
    public async Task FullSyncThenDeltaSync_WithNoModifications_DeltaSyncProcessesZeroCsos()
    {
        // Arrange: Create a Connected System with sync rules and CSOs
        var connectedSystem = await CreateConnectedSystemWithSyncRulesAsync("TestSystem");
        var csvCsoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvObjectType = await CreateMvObjectTypeAsync("Person");

        // Create an import sync rule with projection enabled
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csvCsoType.Id, mvObjectType.Id, projectToMetaverse: true);

        // Create 100 test CSOs (simulating initial import)
        for (int i = 0; i < 100; i++)
        {
            await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, $"user{i}");
        }

        // Verify watermark is null before sync
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Null);

        // Act 1: Run Full Sync
        var fullSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(
            Jim,
            connectedSystem,
            fullSyncRunProfile,
            fullSyncActivity,
            new CancellationTokenSource());

        await fullSyncProcessor.PerformFullSyncAsync();

        // Assert: Full Sync set the watermark
        await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null,
            "Full Sync must set LastDeltaSyncCompletedAt to establish baseline for delta syncs");
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt.Value,
            Is.GreaterThan(DateTime.UtcNow.AddSeconds(-5)),
            "Watermark should be recent");

        // Record watermark time for later comparison
        var watermarkAfterFullSync = connectedSystem.LastDeltaSyncCompletedAt.Value;

        // Act 2: Run Delta Sync (without modifying any CSOs)
        var deltaSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(
            Jim,
            connectedSystem,
            deltaSyncRunProfile,
            deltaSyncActivity,
            new CancellationTokenSource());

        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Delta Sync found zero CSOs to process
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(0),
            "Delta Sync should find 0 CSOs to process since none were modified after the watermark");
        Assert.That(deltaSyncActivity.ObjectsProcessed, Is.EqualTo(0),
            "Delta Sync should process 0 CSOs");

        // Verify watermark was updated even with no changes
        await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt.Value,
            Is.GreaterThanOrEqualTo(watermarkAfterFullSync),
            "Delta Sync should update watermark even when no CSOs were processed");
    }

    /// <summary>
    /// Critical test: Verifies that Delta Sync only processes CSOs modified after the watermark.
    /// This is the core performance optimization of delta sync.
    /// </summary>
    [Test]
    public async Task FullSyncThenDeltaSync_WithOneModifiedCso_ProcessesOnlyModifiedCso()
    {
        // Arrange: Create a Connected System with 100 CSOs
        var connectedSystem = await CreateConnectedSystemWithSyncRulesAsync("TestSystem");
        var csvCsoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvObjectType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csvCsoType.Id, mvObjectType.Id, projectToMetaverse: true);

        // Create 100 CSOs
        var csos = new List<ConnectedSystemObject>();
        for (int i = 0; i < 100; i++)
        {
            var cso = await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, $"user{i}");
            csos.Add(cso);
        }

        // Run Full Sync to establish baseline
        var fullSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, fullSyncRunProfile, fullSyncActivity, new CancellationTokenSource());
        await fullSyncProcessor.PerformFullSyncAsync();

        // Verify watermark was set
        await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);
        var watermark = connectedSystem.LastDeltaSyncCompletedAt.Value;

        // Small delay to ensure LastUpdated will be after watermark
        await Task.Delay(100);

        // Act: Modify just ONE CSO (simulate delta import updating it)
        var modifiedCso = csos[50];
        modifiedCso.LastUpdated = DateTime.UtcNow;
        await Jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(modifiedCso);

        // Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(Jim, connectedSystem, deltaSyncRunProfile, deltaSyncActivity, new CancellationTokenSource());
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Delta Sync processed only 1 CSO, not all 100
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(1),
            "Delta Sync should identify exactly 1 CSO as modified");
        Assert.That(deltaSyncActivity.ObjectsProcessed, Is.EqualTo(1),
            "Delta Sync should process exactly 1 CSO, not all 100");
    }

    /// <summary>
    /// Tests that Delta Sync correctly handles multiple modified CSOs.
    /// </summary>
    [Test]
    public async Task FullSyncThenDeltaSync_WithMultipleModifiedCsos_ProcessesOnlyModifiedCsos()
    {
        // Arrange: Create system with 100 CSOs
        var connectedSystem = await CreateConnectedSystemWithSyncRulesAsync("TestSystem");
        var csvCsoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvObjectType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csvCsoType.Id, mvObjectType.Id, projectToMetaverse: true);

        var csos = new List<ConnectedSystemObject>();
        for (int i = 0; i < 100; i++)
        {
            var cso = await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, $"user{i}");
            csos.Add(cso);
        }

        // Run Full Sync
        var fullSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, fullSyncRunProfile, fullSyncActivity, new CancellationTokenSource());
        await fullSyncProcessor.PerformFullSyncAsync();

        await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);

        await Task.Delay(100);

        // Modify 5 CSOs
        for (int i = 0; i < 5; i++)
        {
            csos[i * 20].LastUpdated = DateTime.UtcNow; // Update CSOs at indices 0, 20, 40, 60, 80
            await Jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(csos[i * 20]);
        }

        // Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(Jim, connectedSystem, deltaSyncRunProfile, deltaSyncActivity, new CancellationTokenSource());
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: Processed exactly 5 CSOs
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(5));
        Assert.That(deltaSyncActivity.ObjectsProcessed, Is.EqualTo(5));
    }

    /// <summary>
    /// Tests that newly created CSOs (Created > watermark) are included in delta sync.
    /// </summary>
    [Test]
    public async Task FullSyncThenDeltaSync_WithNewlyCreatedCso_IncludesNewCso()
    {
        // Arrange: Create system with 10 CSOs
        var connectedSystem = await CreateConnectedSystemWithSyncRulesAsync("TestSystem");
        var csvCsoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvObjectType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csvCsoType.Id, mvObjectType.Id, projectToMetaverse: true);

        for (int i = 0; i < 10; i++)
        {
            await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, $"user{i}");
        }

        // Run Full Sync
        var fullSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, fullSyncRunProfile, fullSyncActivity, new CancellationTokenSource());
        await fullSyncProcessor.PerformFullSyncAsync();

        await ReloadEntityAsync(connectedSystem);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);

        await Task.Delay(100);

        // Create a brand new CSO after the watermark
        await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, "newUser");

        // Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(Jim, connectedSystem, deltaSyncRunProfile, deltaSyncActivity, new CancellationTokenSource());
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: New CSO was included
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(1),
            "Delta Sync should include newly created CSOs (Created > watermark)");
    }

    /// <summary>
    /// Tests that CSOs with LastUpdated = null are excluded from delta sync
    /// (they must have been created before the watermark and never modified).
    /// </summary>
    [Test]
    public async Task DeltaSync_WithCsoWithNullLastUpdated_ExcludesCso()
    {
        // Arrange: Create system with CSOs
        var connectedSystem = await CreateConnectedSystemWithSyncRulesAsync("TestSystem");
        var csvCsoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvObjectType = await CreateMvObjectTypeAsync("Person");
        var syncRule = await CreateImportSyncRuleAsync(connectedSystem.Id, csvCsoType.Id, mvObjectType.Id, projectToMetaverse: true);

        // Create CSO with Created timestamp but no LastUpdated
        var cso = await CreateCsoAsync(connectedSystem.Id, csvCsoType.Id, "user1");

        // Run Full Sync to set watermark
        var fullSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.Synchronisation);
        var fullSyncProcessor = new SyncFullSyncTaskProcessor(Jim, connectedSystem, fullSyncRunProfile, fullSyncActivity, new CancellationTokenSource());
        await fullSyncProcessor.PerformFullSyncAsync();

        await ReloadEntityAsync(connectedSystem);

        // Backdating: Simulate CSO created before watermark (edge case)
        cso.Created = connectedSystem.LastDeltaSyncCompletedAt.Value.AddHours(-1);
        cso.LastUpdated = null; // Never been updated
        await Jim.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(cso);

        // Run Delta Sync
        var deltaSyncActivity = await CreateActivityAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncRunProfile = await CreateRunProfileAsync(connectedSystem.Id, ConnectedSystemRunType.DeltaSynchronisation);
        var deltaSyncProcessor = new SyncDeltaSyncTaskProcessor(Jim, connectedSystem, deltaSyncRunProfile, deltaSyncActivity, new CancellationTokenSource());
        await deltaSyncProcessor.PerformDeltaSyncAsync();

        // Assert: CSO was excluded (Created < watermark AND LastUpdated = null)
        Assert.That(deltaSyncActivity.ObjectsToProcess, Is.EqualTo(0),
            "CSO created before watermark with null LastUpdated should be excluded");
    }
}
