// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Issue #230 slice 1: the import processor now replays <see cref="JIM.Models.Staging.ConnectedSystem.PersistedConnectorData"/>
/// into <c>OpenImportConnection</c>, and persists whatever <c>CloseImportConnection</c> returns, even
/// when the import itself failed. This is plumbing only; no connector yet uses it to invalidate state
/// (the LDAP DC-pinning slice comes later), so these tests exercise the contract via
/// <see cref="MockCallConnector"/>.
/// </summary>
[TestFixture]
public class ImportPersistedConnectorDataTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_PassesConnectedSystemPersistedConnectorDataIntoOpenImportConnectionAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = "seed-watermark";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        var connector = new MockCallConnector();
        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        Assert.That(connector.LastOpenImportPersistedConnectorData, Is.EqualTo("seed-watermark"),
            "OpenImportConnection must be replayed the Connected System's persisted connector state");
    }

    [Test]
    public async Task FullImport_PersistsNonNullCloseImportConnectionReturn_EvenWhenTheImportFailsAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = null;
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        var connector = new MockCallConnector
        {
            TestExceptionToThrow = new InvalidOperationException("simulated import failure")
        };
        connector.WithCloseImportConnectionReturnValue("invalidated-watermark");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act: the import fails, but the run must still fail hard (Synchronisation Integrity:
        // fast/hard failures over corrupted state) - the exception must propagate.
        Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.PerformImportAsync());

        // Assert: CloseImportConnection's non-null return was persisted despite the failure - this is
        // the whole point of the Close return value (e.g. invalidating a pin that a failed connection
        // open proved stale).
        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("invalidated-watermark"),
            "A non-null CloseImportConnection return must be persisted even when the import run failed");
    }

    [Test]
    public async Task FullImport_DoesNotPersist_WhenCloseImportConnectionReturnsNullAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = "original-watermark";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        // Default MockCallConnector: CloseImportConnection returns null (the overwhelmingly common
        // case) and ImportAsync returns an empty, non-failing result - i.e. a completely normal run.
        var connector = new MockCallConnector();
        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert: a null Close return must leave the persisted connector state exactly as it was. If
        // the null return were wrongly persisted, this would have been overwritten to null.
        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"),
            "A null CloseImportConnection return must not trigger any persistence call");
    }

    #region Helpers

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
