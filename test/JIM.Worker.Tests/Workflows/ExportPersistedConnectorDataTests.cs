// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
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

        // The run is cancelled while the Connector holds the batch: the cancellation is not a batch failure the per-batch
        // handler records, so it propagates through the finally that closes the connection, exercising the "the run is
        // already failing" path. SyncExportTaskProcessor itself then treats a requested cancellation as a graceful stop,
        // so PerformExportAsync completes without throwing here.
        using var run = new CancellationTokenSource();
        var connector = new MockCallConnector();
        connector.WithConnectedSystemExportResultFactory(_ =>
        {
            run.Cancel();
            return ConnectedSystemExportResult.Succeeded();
        });
        connector.WithCloseExportConnectionReturnValue("invalidated-pin");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask, run);

        // Act
        await processor.PerformExportAsync();

        // Assert: CloseExportConnection's non-null return was persisted despite the failure - this is
        // the whole point of the Close return value (e.g. invalidating a pin that a failed connection
        // proved stale).
        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("invalidated-pin"),
            "A non-null CloseExportConnection return must be persisted even when the export run failed");
    }

    /// <summary>
    /// #1875: the export side of the same fault. <c>OpenExportConnection</c> was called outside the block that closes
    /// the connection, so a connector that failed to connect was never closed and the state it returned at close (a
    /// pin the failure invalidated) was never persisted.
    /// </summary>
    [Test]
    public async Task Export_OpenExportConnectionFails_StillClosesTheConnectionAndPersistsItsCloseReturnAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        connectedSystem.PersistedConnectorData = "pinned-state";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Original Name");
        CreatePendingExport(connectedSystem, cso, displayNameAttr);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        var openFailure = new InvalidOperationException("simulated connection failure");
        var connector = new MockCallConnector { OpenExportExceptionToThrow = openFailure };
        connector.WithCloseExportConnectionReturnValue("pin-invalidated-state");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        // Act: the run still fails, on the open failure itself.
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.PerformExportAsync());

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(openFailure), "the open failure must be what fails the run");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("pin-invalidated-state"),
                "the connection must be closed after a failed open, and what the connector returned at close persisted");
        }
    }

    /// <summary>
    /// #2083: an <see cref="OperationCanceledException"/> nobody asked for (a timeout surfacing as one, outside any batch)
    /// was taken for the run being cancelled, and the Activity completed as "Export cancelled". It is a failure of the
    /// run, and fails its Activity.
    /// </summary>
    [Test]
    public async Task Export_OperationCanceledExceptionWithoutTheRunBeingCancelled_FailsTheRunAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Original Name");
        CreatePendingExport(connectedSystem, cso, displayNameAttr);

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
        var connector = new MockCallConnector { OpenExportExceptionToThrow = timeout };

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncExportTaskProcessor(
            new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask,
            new CancellationTokenSource());

        // Act
        var thrown = Assert.CatchAsync<OperationCanceledException>(async () => await processor.PerformExportAsync());

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(timeout), "the run fails on the exception itself rather than reading it as a cancellation");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
        }
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

    /// <summary>
    /// #2197: a container the Connector created for an account is selected whether or not the run completes. Captured only
    /// at the end of a completed run, a cancelled run's containers were never selected, so the accounts created in them
    /// stayed unconfirmed and later exports there were refused as outside the managed scope.
    /// </summary>
    [Test]
    public async Task Export_RunCancelledAfterAContainerWasCreated_StillAutoSelectsItAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Target System");
        // Selecting containers reads a configured Connected System, which has settings.
        connectedSystem.SettingValues.Add(new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Host" }, StringValue = "dc1.panoply.local" });
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var displayNameAttr = csoType.Attributes.Single(a => a.Name == "DisplayName");
        var cso = await CreateCsoAsync(connectedSystem.Id, csoType, "Original Name");
        CreatePendingExport(connectedSystem, cso, displayNameAttr);
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.Export);

        using var run = new CancellationTokenSource();
        var connector = new ContainerCreatingConnector();
        connector.WithConnectedSystemExportResultFactory(_ =>
        {
            connector.Created("OU=New,DC=panoply,DC=local");
            run.Cancel();
            return ConnectedSystemExportResult.Succeeded();
        });

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        workerTask.InitiatedByType = ActivityInitiatorType.System;
        var processor = new SyncExportTaskProcessor(new SyncServer(Jim), SyncRepo, connector, connectedSystem, runProfile, workerTask, run);
        await processor.PerformExportAsync();

        var autoSelections = await DbContext.Activities.Where(a => a.ParentActivityId == activity.Id && a.Message != null && a.Message.StartsWith("Auto-selecting")).ToListAsync();
        Assert.That(autoSelections, Has.Count.EqualTo(1), "the container the cancelled run created is still auto-selected");
    }

    /// <summary>
    /// A Connector that creates a container during export, and forgets the containers it created when its connection is
    /// closed, as the contract says (<see cref="IConnectorContainerCreation.CreatedContainerExternalIds"/>).
    /// </summary>
    private sealed class ContainerCreatingConnector : MockCallConnector, IConnectorExportUsingCalls, IConnectorContainerCreation
    {
        private readonly List<string> _created = [];

        public void Created(string containerExternalId) => _created.Add(containerExternalId);

        public IReadOnlyList<string> CreatedContainerExternalIds => _created.ToList();

        string? IConnectorExportUsingCalls.CloseExportConnection()
        {
            _created.Clear();
            return CloseExportConnection();
        }

        public Task<bool> VerifyContainerExistsAsync(string containerExternalId) => Task.FromResult(true);

        public string? GetParentContainerExternalId(string containerExternalId) => null;

        public string GetContainerDisplayName(string containerExternalId) => containerExternalId;
    }

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
