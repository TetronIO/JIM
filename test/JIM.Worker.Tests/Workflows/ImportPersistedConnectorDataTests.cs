// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
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
/// <para>
/// Issue #1868: the watermark a connector returns with its first page is persisted only once the run has staged
/// everything it read, and never over a value <c>CloseImportConnection</c> returned.
/// </para>
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

    /// <summary>
    /// #1875: a connector that cannot connect records what the failure invalidated (the LDAP Connector's pinned
    /// domain controller) for <c>CloseImportConnection</c> to return. The connection was opened outside the block
    /// that closes it, so an open failure skipped the close and the invalidation was never persisted: every later run
    /// resolved the same unreachable server and failed again.
    /// </summary>
    [Test]
    public async Task FullImport_OpenImportConnectionFails_StillClosesTheConnectionAndPersistsItsCloseReturnAsync()
    {
        // Arrange
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = "pinned-state";
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);

        var openFailure = new InvalidOperationException("simulated connection failure");
        var connector = new MockCallConnector { OpenImportExceptionToThrow = openFailure };
        connector.WithCloseImportConnectionReturnValue("pin-invalidated-state");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act: the run still fails, on the open failure itself.
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.PerformImportAsync());

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(openFailure), "the open failure must be what fails the run");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("pin-invalidated-state"),
                "the connection must be closed after a failed open, and what the connector returned at close persisted");
        }
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

    /// <summary>
    /// The watermark a connector returns with its first page stands for every change the run reads, and the run only
    /// holds those changes once it has staged them. When staging fails after every page was read, the watermark must
    /// stay where the run started; persisting it would make the next Delta Import start beyond changes JIM never
    /// staged, and silently skip them (#1868).
    /// </summary>
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullImport)]
    public async Task Import_StagingFailsAfterEveryPageIsRead_LeavesThePersistedWatermarkUnchangedAsync(ConnectedSystemRunType runType)
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync(runType, "original-watermark");
        var connector = new MockCallConnector();
        QueueTwoPages(connector, csoType, firstPageWatermark: "new-watermark");

        // Staging fails when it comes to write the Connected System Objects both pages produced.
        var stagingFailure = new InvalidOperationException("simulated staging failure");
        var syncServer = FaultingSyncServerProxy.Create(
            new SyncServer(Jim), nameof(ISyncServer.CreateConnectedSystemObjectsAsync), stagingFailure);

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, syncServer, new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act: the run must still fail hard (Synchronisation Integrity: fast/hard failures over corrupted state).
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.PerformImportAsync());

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(stagingFailure), "precondition: the run failed while staging");
            Assert.That(connector.ImportPersistedDataHistory, Has.Count.EqualTo(2), "precondition: both pages were read before staging failed");
            Assert.That(csoCount, Is.EqualTo(0), "precondition: nothing was staged");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"),
                "a run that staged nothing must not record the watermark, or the next Delta Import skips the changes it read");
        }
    }

    /// <summary>
    /// Guards the test above against becoming over-broad: a run that stages every page it read records the watermark
    /// its first page returned.
    /// </summary>
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullImport)]
    public async Task Import_CompletesNormally_PersistsTheWatermarkTheFirstPageReturnedAsync(ConnectedSystemRunType runType)
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync(runType, "original-watermark");
        var connector = new MockCallConnector();
        QueueTwoPages(connector, csoType, firstPageWatermark: "new-watermark");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoCount, Is.EqualTo(2), "precondition: both pages were staged");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("new-watermark"));
        }
    }

    /// <summary>
    /// Connector state returned by <c>CloseImportConnection</c> overrides the watermark the pages reported, as
    /// <see cref="JIM.Models.Interfaces.IConnectorImportUsingCalls.CloseImportConnection"/> promises. The connection
    /// closes before staging, and the page watermark is now persisted only after staging (#1868), so the override
    /// must survive a successful run's later persistence rather than being overwritten by it.
    /// </summary>
    [Test]
    public async Task DeltaImport_CloseImportConnectionReturnsData_OverridesThePageWatermarkAsync()
    {
        // Arrange
        var (connectedSystem, csoType, runProfile, activity) = await ArrangeImportAsync(ConnectedSystemRunType.DeltaImport, "original-watermark");
        var connector = new MockCallConnector();
        QueueTwoPages(connector, csoType, firstPageWatermark: "new-watermark");
        connector.WithCloseImportConnectionReturnValue("state-returned-at-close");

        var workerTask = CreateWorkerTask(connectedSystem.Id, runProfile.Id, activity);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, new CancellationTokenSource());

        // Act
        await processor.PerformImportAsync();

        // Assert
        var csoCount = await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoCount, Is.EqualTo(2), "precondition: the run completed and staged both pages");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("state-returned-at-close"),
                "connector state returned at close must win over the watermark the pages reported");
        }
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType CsoType, ConnectedSystemRunProfile RunProfile, Activity Activity)> ArrangeImportAsync(
        ConnectedSystemRunType runType, string? persistedConnectorData)
    {
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        connectedSystem.PersistedConnectorData = persistedConnectorData;
        var csoType = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        await CreateImportSyncRuleAsync(connectedSystem.Id, csoType, mvType, "HR Import");

        var runProfileName = runType == ConnectedSystemRunType.DeltaImport ? "Delta Import" : "Full Import";
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, runProfileName, runType);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, runType);
        return (connectedSystem, csoType, runProfile, activity);
    }

    /// <summary>
    /// Queues two pages of one new object each. The first page carries the connector's new watermark and a token
    /// for the second, as the LDAP Connector's does; the second returns no watermark, meaning "no change".
    /// </summary>
    private static void QueueTwoPages(MockCallConnector connector, ConnectedSystemObjectType csoType, string firstPageWatermark)
    {
        connector.QueueImportResult(new ConnectedSystemImportResult
        {
            ImportObjects = [NewImportObject(csoType)],
            PersistedConnectorData = firstPageWatermark,
            PaginationTokens = [new ConnectedSystemPaginationToken("page", "2")]
        });
        connector.QueueImportResult(new ConnectedSystemImportResult
        {
            ImportObjects = [NewImportObject(csoType)]
        });
    }

    private static ConnectedSystemImportObject NewImportObject(ConnectedSystemObjectType csoType)
    {
        var externalIdAttribute = csoType.Attributes.Single(a => a.IsExternalId);
        return new ConnectedSystemImportObject
        {
            ObjectType = csoType.Name,
            ChangeType = ObjectChangeType.Created,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = externalIdAttribute.Name,
                    Type = externalIdAttribute.Type,
                    GuidValues = [Guid.NewGuid()]
                }
            ]
        };
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
