// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Workflows;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Proves an export records the steps an administrator sees (#454, #1214), and in particular that a
/// Connector's own step stops being shown as running the moment the Connector's work is done.
/// </summary>
/// <remarks>
/// <para>
/// Driven against a stubbed <see cref="ISyncServer"/> rather than a real export: the defect is in
/// how the processor orchestrates the run around the Connector call, so what matters is what the
/// processor does once the export returns, not how the export itself was performed.
/// </para>
/// <para>
/// The runs are arranged so that nothing follows the export, because every later step closes a
/// still-running Connector step as a side effect of starting; that is the very fault being pinned,
/// and read after one of them has run the step looks properly finished whether or not it was closed
/// at the right moment. Preview mode is what buys that: the processor skips both reference
/// resolution and password delivery, leaving the Connector's step as the last thing to have
/// happened. The stub calls the Connector's progress reporting either way, so the step is still
/// entered exactly as a real export would enter it.
/// </para>
/// </remarks>
[TestFixture]
public class ExportPhaseWiringTests : WorkflowTestBase
{
    private const string ConnectorWritePhaseKey = "write";

    [Test]
    public async Task Export_ConnectorHasFinished_StopsShowingItsStepAsRunningAsync()
    {
        // Arrange: an export whose Connector declares a step of its own and enters it while writing.
        var (connectedSystem, runProfile, activity) = await ArrangeExportAsync();
        var connector = new MockPhaseDeclaringExportConnector();
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        // Act
        await BuildProcessor(StubSyncServer().Object, connector, connectedSystem, runProfile, activity, reporter).PerformExportAsync();

        // Assert
        Assert.That(WritePhase().Status, Is.EqualTo(ActivityPhaseStatus.Completed),
            "The Connector had returned, so its step should have been closed there and then rather than left " +
            "running for the next step to close as a side effect of starting, which hands it the time that step took.");
        Assert.That(WritePhase().Ended, Is.Not.Null, "A step nothing is running has to carry how long it took.");
    }

    [Test]
    public async Task Export_ConnectorHasFinished_LeavesTheExportItselfRunningAsync()
    {
        // Closing the Connector's step must not close the JIM step hosting it: the export is still
        // going on, and a rail showing nothing running would be worse than the stale step it replaced.
        var (connectedSystem, runProfile, activity) = await ArrangeExportAsync();
        var connector = new MockPhaseDeclaringExportConnector();
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        await BuildProcessor(StubSyncServer().Object, connector, connectedSystem, runProfile, activity, reporter).PerformExportAsync();

        Assert.That(SyncRepo.ActivityPhases.Single(p => p.Key == RunPhaseKeys.ExportExecute).Status,
            Is.EqualTo(ActivityPhaseStatus.Active));
    }

    [Test]
    public async Task Export_ConnectorDeclaredPhase_NestsInsideTheStepThatCallsTheConnectorAsync()
    {
        var (connectedSystem, runProfile, activity) = await ArrangeExportAsync();
        var connector = new MockPhaseDeclaringExportConnector();
        var reporter = await ActivityPhaseReporter.StartAsync(SyncRepo, activity, connector, connectedSystem, runProfile);

        await BuildProcessor(StubSyncServer().Object, connector, connectedSystem, runProfile, activity, reporter).PerformExportAsync();

        Assert.That(WritePhase().ParentKey, Is.EqualTo(RunPhaseKeys.ExportExecute),
            "A Connector's step belongs inside the JIM step that called it, so the top-level step count stays the same whichever Connector is in use.");
        Assert.That(WritePhase().Name, Is.EqualTo("Writing objects"));
    }

    #region Helpers

    private ActivityPhase WritePhase() =>
        SyncRepo.ActivityPhases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey(ConnectorWritePhaseKey));

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemRunProfile RunProfile, Activity Activity)> ArrangeExportAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Target Directory");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Someone");

        // One waiting change, so the run has work to do: an export with nothing pending returns
        // before it ever reaches the Connector. It needs a pending attribute change of its own; an
        // Update carrying none is not executable and would leave the run with nothing to do either.
        var displayNameAttribute = csoType.Attributes.First(a => a.Name == "DisplayName");
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = []
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = displayNameAttribute.Id,
            Attribute = displayNameAttribute,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "Someone Else",
            Status = PendingExportAttributeChangeStatus.Pending
        });
        SyncRepo.SeedPendingExport(pendingExport);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);
        return (connectedSystem, runProfile, activity);
    }

    /// <summary>
    /// An <see cref="ISyncServer"/> that performs no export of its own, but narrates one: it reports
    /// the Connector entering its declared step, exactly as a real export relays it, and returns.
    /// </summary>
    private static Mock<ISyncServer> StubSyncServer()
    {
        var syncServer = new Mock<ISyncServer>();

        syncServer.Setup(s => s.GetSyncOutcomeTrackingLevelAsync())
            .ReturnsAsync(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None);
        syncServer.Setup(s => s.GetCsoChangeTrackingEnabledAsync()).ReturnsAsync(false);

        syncServer.Setup(s => s.ExecuteExportsAsync(
                It.IsAny<ConnectedSystem>(),
                It.IsAny<IConnector>(),
                It.IsAny<SyncRunMode>(),
                It.IsAny<ExportExecutionOptions?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<ExportProgressInfo, Task>?>(),
                It.IsAny<Func<IConnector>?>(),
                It.IsAny<Func<ISyncRepositoryScope>?>(),
                It.IsAny<Func<List<ProcessedExportItem>, Task>?>()))
            .Returns(async (
                ConnectedSystem _,
                IConnector _,
                SyncRunMode _,
                ExportExecutionOptions? _,
                CancellationToken _,
                Func<ExportProgressInfo, Task>? progressCallback,
                Func<IConnector>? _,
                Func<ISyncRepositoryScope>? _,
                Func<List<ProcessedExportItem>, Task>? _) =>
            {
                if (progressCallback != null)
                {
                    await progressCallback(new ExportProgressInfo
                    {
                        Phase = ExportPhase.Executing,
                        ConnectorPhaseKey = ConnectorWritePhaseKey,
                        Message = "Writing objects"
                    });
                }

                return new ExportExecutionResult { CompletedAt = DateTime.UtcNow };
            });

        return syncServer;
    }

    private SyncExportTaskProcessor BuildProcessor(
        ISyncServer syncServer,
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        Activity activity,
        ActivityPhaseReporter reporter) =>
        new(syncServer, SyncRepo, connector, connectedSystem, runProfile,
            new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
            {
                Id = Guid.NewGuid(),
                Status = WorkerTaskStatus.Processing,
                Activity = activity
            },
            new CancellationTokenSource(), runMode: SyncRunMode.PreviewOnly, phaseReporter: reporter);

    /// <summary>
    /// A call-based export Connector that declares one step of its own, standing in for a directory
    /// without needing one.
    /// </summary>
    private class MockPhaseDeclaringExportConnector : IConnector, IConnectorExportUsingCalls, IConnectorPhases
    {
        public string Name => "MockPhaseDeclaringExportConnector";
        public string? Description => null;
        public string? Url => null;

        public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile) =>
            [new ConnectorPhase(ConnectorWritePhaseKey, "Writing objects")];

        public void OpenExportConnection(IList<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData) { }

        public string? CloseExportConnection() => null;

        public Task<List<ConnectedSystemExportResult>> ExportAsync(
            IList<PendingExport> pendingExports,
            CancellationToken cancellationToken,
            IConnectorProgress progress) =>
            Task.FromResult(pendingExports.Select(_ => ConnectedSystemExportResult.Succeeded()).ToList());
    }

    #endregion
}
