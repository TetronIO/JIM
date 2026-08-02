// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Issue #230 slice 1: the export processor now replays <see cref="ConnectedSystem.PersistedConnectorData"/>
/// into <c>OpenExportConnection</c>, and persists whatever <c>CloseExportConnection</c> returns, even
/// when the export itself failed. This is plumbing only; no connector yet uses it to invalidate state
/// (the LDAP DC-pinning slice comes later), so these tests exercise the contract via
/// <see cref="MockCallConnector"/>, driven through <see cref="SyncExportTaskProcessor"/> - the real
/// production entry point, which delegates through <c>ExportExecutionServer</c> where
/// OpenExportConnection/CloseExportConnection are actually called.
/// </summary>
[TestFixture]
public class ExportPersistedConnectorDataTests : WorkflowTestBase
{
    [Test]
    public async Task Export_PassesConnectedSystemPersistedConnectorDataIntoOpenExportConnectionAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        connectedSystem.PersistedConnectorData = "seed-pin";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Original Name");
        CreatePendingExport(connectedSystem, cso, displayNameAttr);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        var connector = new MockCallConnector();
        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        // Act
        await processor.PerformExportAsync();

        // Assert
        Assert.That(connector.LastOpenExportPersistedConnectorData, Is.EqualTo("seed-pin"),
            "OpenExportConnection must be replayed the Connected System's persisted connector state");
    }

    [Test]
    public async Task Export_PersistsNonNullCloseExportConnectionReturn_EvenWhenTheExportFailsAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        connectedSystem.PersistedConnectorData = null;
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Original Name");
        CreatePendingExport(connectedSystem, cso, displayNameAttr);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        // OperationCanceledException is not caught by the per-batch failure handler (which only
        // catches and records plain export failures) - it propagates straight past ExportAsync,
        // through the finally that closes the connection, exercising the "the run is already failing"
        // path. SyncExportTaskProcessor itself then swallows OperationCanceledException (treats it as
        // a graceful stop), so PerformExportAsync completes without throwing here.
        var connector = new MockCallConnector
        {
            ExportExceptionToThrow = new OperationCanceledException("simulated mid-export cancellation")
        };
        connector.WithCloseExportConnectionReturnValue("invalidated-pin");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        // Act
        await processor.PerformExportAsync();

        // Assert: CloseExportConnection's non-null return was persisted despite the failure - this is
        // the whole point of the Close return value (e.g. invalidating a pin that a failed connection
        // proved stale).
        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("invalidated-pin"),
            "A non-null CloseExportConnection return must be persisted even when the export run failed");
    }

    [Test]
    public async Task Export_DoesNotPersist_WhenCloseExportConnectionReturnsNullAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        connectedSystem.PersistedConnectorData = "original-pin";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Original Name");
        CreatePendingExport(connectedSystem, cso, displayNameAttr);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        // Default MockCallConnector: CloseExportConnection returns null (the overwhelmingly common
        // case) and ExportAsync succeeds - i.e. a completely normal run.
        var connector = new MockCallConnector();
        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        // Act
        await processor.PerformExportAsync();

        // Assert: a null Close return must leave the persisted connector state exactly as it was. If
        // the null return were wrongly persisted, this would have been overwritten to null.
        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-pin"),
            "A null CloseExportConnection return must not trigger any persistence call");
    }

    #region Helpers

    private PendingExport CreatePendingExport(
        ConnectedSystem connectedSystem, ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute displayNameAttr)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Update,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Update,
                    AttributeId = displayNameAttr.Id,
                    Attribute = displayNameAttr,
                    StringValue = "New Name",
                    Status = PendingExportAttributeChangeStatus.Pending
                }
            }
        };
        SyncRepo.SeedPendingExport(pendingExport);
        return pendingExport;
    }

    private static SynchronisationWorkerTask CreateWorkerTask(
        int connectedSystemId, int runProfileId, Activity activity)
    {
        return new SynchronisationWorkerTask(connectedSystemId, runProfileId)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };
    }

    #endregion
}
