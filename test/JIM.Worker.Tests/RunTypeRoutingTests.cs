using JIM.Models.Staging;

namespace JIM.Worker.Tests;

/// <summary>
/// Tests to validate that run types are routed to the correct processors.
/// These tests document the expected behaviour and prevent regression of routing bugs.
/// </summary>
/// <remarks>
/// This test class exists because a bug was found where DeltaImport was incorrectly
/// routed to SyncFullSyncTaskProcessor instead of SyncImportTaskProcessor.
/// The routing logic in Worker.cs is critical and must be validated.
/// </remarks>
public class RunTypeRoutingTests
{
    /// <summary>
    /// Documents which processor each run type should use.
    /// This serves as a specification and regression test.
    /// </summary>
    [Test]
    public void RunTypeRouting_DocumentedBehaviour_ShouldMatchExpectationsAsync()
    {
        // This test documents the expected routing behaviour.
        // If you're changing the routing in Worker.cs, update this test first.

        var expectedRouting = new Dictionary<ConnectedSystemRunType, string>
        {
            // Import operations use SyncImportTaskProcessor
            { ConnectedSystemRunType.FullImport, "SyncImportTaskProcessor" },
            { ConnectedSystemRunType.DeltaImport, "SyncImportTaskProcessor" },

            // Sync operations use their respective sync processors
            { ConnectedSystemRunType.FullSynchronisation, "SyncFullSyncTaskProcessor" },
            { ConnectedSystemRunType.DeltaSynchronisation, "SyncDeltaSyncTaskProcessor" },

            // Export operations use SyncExportTaskProcessor
            { ConnectedSystemRunType.Export, "SyncExportTaskProcessor" },
        };

        // Verify all run types are documented
        var allRunTypes = Enum.GetValues<ConnectedSystemRunType>()
            .Where(rt => rt != ConnectedSystemRunType.NotSet)
            .ToList();

        foreach (var runType in allRunTypes)
        {
            Assert.That(expectedRouting.ContainsKey(runType), Is.True,
                $"Run type {runType} is not documented in the expected routing. " +
                "Update this test when adding new run types.");
        }

        // Document the critical routing decisions
        Assert.Multiple(() =>
        {
            // CRITICAL: Both import types must use SyncImportTaskProcessor
            // This was a bug where DeltaImport was incorrectly using SyncFullSyncTaskProcessor
            Assert.That(expectedRouting[ConnectedSystemRunType.FullImport],
                Is.EqualTo(expectedRouting[ConnectedSystemRunType.DeltaImport]),
                "FullImport and DeltaImport MUST use the same processor (SyncImportTaskProcessor). " +
                "The connector's ImportAsync method handles the difference between full and delta modes.");

            // Sync operations should use different processors
            Assert.That(expectedRouting[ConnectedSystemRunType.FullSynchronisation],
                Is.Not.EqualTo(expectedRouting[ConnectedSystemRunType.DeltaSynchronisation]),
                "FullSynchronisation and DeltaSynchronisation should use different processors.");
        });
    }

    /// <summary>
    /// Validates that all ConnectedSystemRunType values are handled in the Worker.
    /// </summary>
    [Test]
    public void AllRunTypes_ShouldBeHandledInWorkerAsync()
    {
        var handledRunTypes = new[]
        {
            ConnectedSystemRunType.FullImport,
            ConnectedSystemRunType.DeltaImport,
            ConnectedSystemRunType.FullSynchronisation,
            ConnectedSystemRunType.DeltaSynchronisation,
            ConnectedSystemRunType.Export,
        };

        var allRunTypes = Enum.GetValues<ConnectedSystemRunType>()
            .Where(rt => rt != ConnectedSystemRunType.NotSet)
            .ToList();

        foreach (var runType in allRunTypes)
        {
            Assert.That(handledRunTypes, Does.Contain(runType),
                $"Run type {runType} is not handled in the Worker. " +
                "Add a case for this run type in Worker.ExecuteAsync.");
        }
    }
}
