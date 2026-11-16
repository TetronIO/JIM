// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2521 Phase 4: the Connected System's import-incomplete marker (<see cref="ConnectedSystem.ImportIncompleteSince"/>).
/// An import's save phase writes in batches that commit one at a time, so an import that stops part-way leaves the
/// Connector Space holding part of what it read: objects saved with references to objects that were not. A
/// synchronisation reading that removes every Metaverse reference it cannot resolve, and exports the removal, so the
/// import marks the Connected System before its first write, and only an import that read every object of the whole
/// Connected System and completed clears it. Full and Delta Synchronisation refuse while it is set
/// (<see cref="WorkerImportIncompleteRefusalTests"/>).
/// </summary>
[TestFixture]
public class ImportIncompleteMarkerTests : WorkflowTestBase
{
    /// <summary>When the marker of an earlier stopped import was set: long enough ago that any write by the run is later.</summary>
    private static readonly DateTime EarlierStop = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    #region A run that stops

    [TestCase(ConnectedSystemRunType.FullImport)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    public async Task Import_SaveFailsAfterItsFirstBatchIsSaved_LeavesTheConnectedSystemMarkedImportIncompleteAsync(ConnectedSystemRunType runType)
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)], [NewObject(type)]]);

        // One object per batch, and the second batch's write fails: the first batch is saved and the second is not.
        var failure = new InvalidOperationException("simulated failure saving the second batch");
        var syncServer = FaultingSyncServerProxy.Create(new SyncServer(Jim), nameof(ISyncServer.CreateConnectedSystemObjectsAsync), failure, failOnCall: 2);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, runType, syncServer: syncServer, importBatchSize: 1);

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(processor.PerformImportAsync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(failure), "precondition: the run failed in its save phase");
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1),
                "precondition: the run stopped part-way, with one of its two objects saved");
            Assert.That(await StoredMarkerAsync(connectedSystem), Is.Not.Null,
                "a run that stopped part-way through writing must leave the Connected System marked, so synchronisation waits for a Full Import");
            Assert.That(connectedSystem.ImportIncompleteSince, Is.Not.Null, "the run's own instance is kept in step");
        }
    }

    [TestCase(ConnectedSystemRunType.FullImport)]
    [TestCase(ConnectedSystemRunType.DeltaImport)]
    public async Task Import_ConnectorFailsBeforeAnythingIsWritten_LeavesTheConnectedSystemUnmarkedAsync(ConnectedSystemRunType runType)
    {
        var (connectedSystem, _) = await ArrangeAsync();
        var connector = new MockCallConnector { TestExceptionToThrow = new InvalidOperationException("simulated read failure") };
        var (processor, _) = await PrepareAsync(connectedSystem, connector, runType);

        Assert.ThrowsAsync<InvalidOperationException>(processor.PerformImportAsync);

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null,
            "a run that wrote nothing left nothing half-saved, so it must not stop synchronisation");
    }

    [Test]
    public async Task FullImport_CancelledBeforeAnythingIsWritten_LeavesTheConnectedSystemUnmarkedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)], [NewObject(type)]]);
        var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport, cancellation: cancellation);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null);
    }

    /// <summary>
    /// Cancellation, and the Worker's memory guard, which stops a run by cancelling it, stop an import between its save
    /// batches: the run returns without failing, and what it saved stays saved.
    /// </summary>
    [Test]
    public async Task FullImport_CancelledDuringItsSavePhase_LeavesTheConnectedSystemMarkedImportIncompleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)], [NewObject(type)]]);
        var cancellation = new CancellationTokenSource();
        var syncServer = FaultingSyncServerProxy.CreateObserving(new SyncServer(Jim), nameof(ISyncServer.CreateConnectedSystemObjectsAsync), cancellation.Cancel);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport, syncServer: syncServer, cancellation: cancellation);

        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.GreaterThan(0), "precondition: the run had begun saving");
            Assert.That(await StoredMarkerAsync(connectedSystem), Is.Not.Null, "a cancelled run that wrote anything must leave the marker set");
        }
    }

    [Test]
    public async Task FullImport_StopsAgainWhileMarked_KeepsTheEarlierTimeAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var syncServer = FaultingSyncServerProxy.Create(new SyncServer(Jim), nameof(ISyncServer.CreateConnectedSystemObjectsAsync),
            new InvalidOperationException("simulated save failure"));
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport, syncServer: syncServer);

        Assert.ThrowsAsync<InvalidOperationException>(processor.PerformImportAsync);

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.EqualTo(EarlierStop),
            "the marker says when the earliest unfinished import began writing");
    }

    #endregion

    #region A run that completes

    [Test]
    public async Task FullImport_Completes_ClearsTheMarkerAStoppedImportLeftAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)], [NewObject(type)]]);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport);

        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null,
                "a completed Full Import re-read and saved everything, so what the stopped run left half-saved is whole again");
            Assert.That(connectedSystem.ImportIncompleteSince, Is.Null);
        }
    }

    [Test]
    public async Task FullImport_Completes_LeavesAnUnmarkedConnectedSystemUnmarkedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null, "a run that completes takes its own marker down again");
    }

    [Test]
    public async Task FullImport_DeletionsRefusedByTheRunProfilesLimits_StillClearsTheMarkerAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        SeedHeld(connectedSystem, type);
        SeedHeld(connectedSystem, type);
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var (processor, activity) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport, maxDetectedDeletions: 0);

        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(2), "precondition: the limits refused the run's detected deletions");
            Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null,
                "refusing deletions leaves objects as they were, not half-saved: the run read and saved everything");
        }
    }

    [Test]
    public async Task DeltaImport_ReadingChanges_Completes_LeavesTheMarkerAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.DeltaImport);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.EqualTo(EarlierStop),
            "a Delta Import reads only what changed, so it cannot repair what a stopped run left half-saved");
    }

    [Test]
    public async Task DeltaImport_ReadingChanges_AnotherRunMarksItBeforeThisRunsFirstWrite_LeavesTheMarkerAsync()
    {
        // Unmarked as the run starts; a parallel run of the same Connected System (a Schedule step) marks it after this
        // run has loaded the Connected System and before this run's first write. This run's completion says nothing
        // about what the other run left half-saved, so it must not take that marker down.
        var (connectedSystem, type) = await ArrangeAsync();
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var marksSoFar = 0;
        var syncRepository = InterceptingSyncRepositoryProxy.Create(SyncRepo, nameof(ISyncRepository.MarkImportIncompleteAsync), () =>
        {
            if (marksSoFar++ == 0)
                SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop).GetAwaiter().GetResult();
        });
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.DeltaImport, syncRepository: syncRepository);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.EqualTo(EarlierStop),
            "a marker another run set after this run loaded the Connected System is that run's, and this run read only what changed");
    }

    [Test]
    public async Task DeltaImport_ReadingChanges_CompletesOnAnUnmarkedConnectedSystem_LeavesItUnmarkedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var (processor, activity) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.DeltaImport);

        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1), "precondition: the run saved what it read");
            Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null, "a completed run takes down the marker it set itself, whatever it read");
            Assert.That(activity.WarningMessage, Is.Null.Or.Empty);
        }
    }

    [Test]
    public async Task DeltaImport_ReadingChanges_CompletesWhileMarked_SaysOnItsActivityThatSynchronisationStillWaitsAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var (processor, activity) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.DeltaImport);

        await processor.PerformImportAsync();

        Assert.That(activity.WarningMessage, Is.EqualTo(ImportCompleteness.DescribeStillIncompleteAfterImport(connectedSystem, readOnlyChanges: true, scopedToOnePartition: false)),
            "a run that cannot take the marker down says so, rather than leaving the administrator to find out from a refused synchronisation");
    }

    [Test]
    public async Task DeltaImport_ThatReadEveryObject_Completes_ClearsTheMarkerAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)], [NewObject(type)]], completeRead: true);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.DeltaImport);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.Null,
            "a Delta Import whose Connector read every object (#2720) re-read everything a Full Import would have");
    }

    [Test]
    public async Task FullImport_ScopedToOnePartition_Completes_LeavesTheMarkerAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var partition = new ConnectedSystemPartition { ConnectedSystem = connectedSystem, ExternalId = "dc=corp,dc=local", Name = "corp.local", Selected = true };
        DbContext.ConnectedSystemPartitions.Add(partition);
        await DbContext.SaveChangesAsync();
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[NewObject(type)]]);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport, partition: partition);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.EqualTo(EarlierStop),
            "a run that read one Partition cannot tell whether the stopped run left another half-saved");
    }

    [Test]
    public async Task FullImport_ReadsNothingWhileObjectsAreHeld_LeavesTheMarkerAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        SeedHeld(connectedSystem, type);
        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        var connector = new MockCallConnector();
        QueuePages(connector, [[]]);
        var (processor, _) = await PrepareAsync(connectedSystem, connector, ConnectedSystemRunType.FullImport);

        await processor.PerformImportAsync();

        Assert.That(await StoredMarkerAsync(connectedSystem), Is.EqualTo(EarlierStop),
            "a run that read nothing re-read nothing, as deletion detection also concludes, so what was half-saved still is");
    }

    #endregion

    #region Previews

    /// <summary>
    /// A synchronisation preview reads through the read-only repository guard and writes nothing, so it can neither set the
    /// marker nor take one down.
    /// </summary>
    [Test]
    public async Task PreviewFullSync_NeitherSetsNorClearsTheMarkerAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        SeedHeld(connectedSystem, type);

        await Jim.SyncPreview.PreviewFullSyncAsync(connectedSystem.Id);
        var unmarked = await StoredMarkerAsync(connectedSystem);

        await SyncRepo.MarkImportIncompleteAsync(connectedSystem.Id, EarlierStop);
        await Jim.SyncPreview.PreviewFullSyncAsync(connectedSystem.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unmarked, Is.Null);
            Assert.That(await StoredMarkerAsync(connectedSystem), Is.EqualTo(EarlierStop));
        }
    }

    #endregion

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("HR System");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "User");
        return (connectedSystem, type);
    }

    private async Task<(SyncImportTaskProcessor Processor, Activity Activity)> PrepareAsync(ConnectedSystem connectedSystem, MockCallConnector connector,
        ConnectedSystemRunType runType, ISyncServer? syncServer = null, int? importBatchSize = null, CancellationTokenSource? cancellation = null,
        int? maxDetectedDeletions = null, ConnectedSystemPartition? partition = null, ISyncRepository? syncRepository = null)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, runType == ConnectedSystemRunType.FullImport ? "Full Import" : "Delta Import", runType);
        runProfile.MaxDetectedDeletions = maxDetectedDeletions;
        runProfile.Partition = partition;
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(Jim, syncRepository ?? SyncRepo, syncServer ?? new SyncServer(Jim), new SyncEngine(),
            connector, connectedSystem, runProfile, workerTask, cancellation ?? new CancellationTokenSource())
        {
            ImportBatchSize = importBatchSize ?? 2000
        };
        return (processor, activity);
    }

    /// <summary>Queues each page in turn, every page but the last carrying a token for the next, as a paged read does.</summary>
    private static void QueuePages(MockCallConnector connector, List<List<ConnectedSystemImportObject>> pages, bool completeRead = false)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            connector.QueueImportResult(new ConnectedSystemImportResult
            {
                ImportObjects = pages[i],
                CompleteRead = completeRead,
                PaginationTokens = i < pages.Count - 1 ? [new ConnectedSystemPaginationToken("page", (i + 2).ToString())] : []
            });
        }
    }

    private static ConnectedSystemImportObject NewObject(ConnectedSystemObjectType type) =>
        new()
        {
            ObjectType = type.Name,
            ChangeType = ObjectChangeType.Created,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = type.Attributes.Single(a => a.IsExternalId).Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = [Guid.NewGuid()]
                }
            ]
        };

    /// <summary>A Connected System Object JIM already holds, as an earlier import would have left it.</summary>
    private void SeedHeld(ConnectedSystem connectedSystem, ConnectedSystemObjectType type)
    {
        var externalIdAttribute = type.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = externalIdAttribute.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttribute.Id,
            Attribute = externalIdAttribute,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = cso
        });
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    /// <summary>
    /// The marker as the synchronisation repository holds it: the Worker's narrow writes go there, and the real-PostgreSQL
    /// round trip is <see cref="Servers.ConnectedSystemImportIncompleteDatabaseTests"/>.
    /// </summary>
    private Task<DateTime?> StoredMarkerAsync(ConnectedSystem connectedSystem) =>
        Task.FromResult(SyncRepo.ConnectedSystems[connectedSystem.Id].ImportIncompleteSince);

    #endregion
}
