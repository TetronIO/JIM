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
/// A Connected System Object created PendingProvisioning alongside a Create
/// Pending Export, whose Create was exported but whose Metaverse Object was withdrawn before any
/// confirming import, gets a Delete Pending Export staged for it. These tests drive that Delete
/// through <see cref="SyncExportTaskProcessor"/> - the real production entry point - to prove the
/// Connected System Object and its Pending Export are removed, and that
/// <c>PersistBatchRpeisAsync</c> still records an RPEI with a display-name snapshot but no
/// dangling foreign key to the row it just deleted.
/// </summary>
[TestFixture]
public class UnconfirmedProvisioningDeleteWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task Export_SuccessfulDeleteOfUnconfirmedProvisioningCso_RemovesCsoAndRecordsSnapshottedRpeiAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Cancelled Provisioning User");

        // The CSO's provisioning was never confirmed by an import: Status stays PendingProvisioning.
        cso.Status = ConnectedSystemObjectStatus.PendingProvisioning;
        cso.JoinType = ConnectedSystemObjectJoinType.Provisioned;

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            Status = PendingExportStatus.Pending,
            ChangeType = PendingExportChangeType.Delete,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
        SyncRepo.SeedPendingExport(pendingExport);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        // Default MockCallConnector: a Delete export succeeds with no External ID.
        var connector = new MockCallConnector();
        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        // Act
        await processor.PerformExportAsync();

        // Assert - the CSO and its Pending Export are gone.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(cso.Id), Is.False,
                "The unconfirmed-provisioning CSO must be removed once its Delete export succeeds");
            Assert.That(SyncRepo.PendingExports.ContainsKey(pendingExport.Id), Is.False,
                "The Pending Export must be removed alongside the CSO");
        }

        // Assert - PersistBatchRpeisAsync still recorded an RPEI, snapshotted for display but with no
        // dangling foreign key to the row it just deleted.
        var rpei = activity.RunProfileExecutionItems.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ConnectedSystemObjectId, Is.Null,
                "The RPEI must not FK to a Connected System Object row that no longer exists");
            Assert.That(rpei.ConnectedSystemObject, Is.Null);
            Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("Cancelled Provisioning User"),
                "The item must still keep its display name, taken from the in-memory CSO graph before removal");
            Assert.That(rpei.ObjectChangeType, Is.EqualTo(ObjectChangeType.Deprovisioned));
        }
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
}
