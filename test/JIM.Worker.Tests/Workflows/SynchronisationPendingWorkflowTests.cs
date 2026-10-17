// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The synchronisation-pending mark (#2092) through the real processors: each import path marks what it changes and
/// nothing else, and both synchronisations page by keyset until a page comes back empty, so an object that enters the
/// selection above the cursor while the run is in flight is processed by the same run, and processing more than the
/// count taken at the start is not an error. The PostgreSQL behaviour of the same writers and the row-version guard is
/// proved by <c>SynchronisationPendingDatabaseTests</c> and <c>SynchronisationPendingRaceDatabaseTests</c>.
/// </summary>
[TestFixture]
public class SynchronisationPendingWorkflowTests : WorkflowTestBase
{
    #region Import paths set the mark

    [Test]
    public async Task FullImport_CreatesObjects_MarksEveryOneAsync()
    {
        var (system, type) = await ArrangeImportAsync();

        await RunImportAsync(system, ConnectedSystemRunType.FullImport, [[ImportObject(type, Guid.NewGuid()), ImportObject(type, Guid.NewGuid())]]);

        Assert.That(SyncRepo.ConnectedSystemObjects.Values.Select(c => c.SynchronisationPending), Is.All.True);
    }

    [Test]
    public async Task FullImport_UpdatesOneObject_MarksOnlyThatObjectAsync()
    {
        var (system, type) = await ArrangeImportAsync();
        var changing = Guid.NewGuid();
        var unchanging = Guid.NewGuid();
        await RunImportAsync(system, ConnectedSystemRunType.FullImport, [[ImportObject(type, changing), ImportObject(type, unchanging)]]);
        ClearAllMarks();

        await RunImportAsync(system, ConnectedSystemRunType.FullImport,
            [[ImportObject(type, changing, displayName: "Renamed"), ImportObject(type, unchanging)]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CsoWithExternalId(type, changing).SynchronisationPending, Is.True, "the updated object is pending synchronisation");
            Assert.That(CsoWithExternalId(type, unchanging).SynchronisationPending, Is.False,
                "an object the import found unchanged is not written, so it is not marked");
        }
    }

    [Test]
    public async Task FullImport_DeletionDetection_MarksTheObsoletedObjectAsync()
    {
        var (system, type) = await ArrangeImportAsync();
        var kept = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        await RunImportAsync(system, ConnectedSystemRunType.FullImport, [[ImportObject(type, kept), ImportObject(type, deleted)]]);
        ClearAllMarks();

        await RunImportAsync(system, ConnectedSystemRunType.FullImport, [[ImportObject(type, kept)]]);

        var obsoleted = CsoWithExternalId(type, deleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(obsoleted.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "precondition: deletion detected");
            Assert.That(obsoleted.SynchronisationPending, Is.True, "a detected deletion is pending synchronisation");
        }
    }

    [Test]
    public async Task DeltaImport_ExplicitDelete_MarksTheObsoletedObjectAsync()
    {
        var (system, type) = await ArrangeImportAsync();
        var deleted = Guid.NewGuid();
        await RunImportAsync(system, ConnectedSystemRunType.FullImport, [[ImportObject(type, deleted), ImportObject(type, Guid.NewGuid())]]);
        ClearAllMarks();

        var delete = ImportObject(type, deleted);
        delete.ChangeType = ObjectChangeType.Deleted;
        await RunImportAsync(system, ConnectedSystemRunType.DeltaImport, [[delete]]);

        var obsoleted = CsoWithExternalId(type, deleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(obsoleted.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "precondition: the delete was applied");
            Assert.That(obsoleted.SynchronisationPending, Is.True, "an explicit delete is pending synchronisation");
        }
    }

    #endregion

    #region Paging until a page comes back empty

    /// <summary>
    /// The run counts one object to process; while processing it, an import marks an object further along the keyset,
    /// with timestamps older than the watermark so only the mark selects it. Paging until empty processes it in the
    /// same run, and the processed count exceeding the estimate completes normally.
    /// </summary>
    [Test]
    public async Task DeltaSync_ObjectMarkedAboveTheCursorMidRun_IsProcessedByTheSameRunAsync()
    {
        var (system, type, rule) = await ArrangeSyncAsync();
        var longAgo = DateTime.UtcNow.AddHours(-2);
        var first = await CreateCsoWithIdAsync(system.Id, type, 1, longAgo);
        var later = await CreateCsoWithIdAsync(system.Id, type, 9, longAgo);
        await RunSyncAsync(system, ConnectedSystemRunType.FullSynchronisation);
        Assert.That(rule.Enabled, Is.True);
        ClearAllMarks();

        first.LastUpdated = DateTime.UtcNow;
        await SyncRepo.UpdateConnectedSystemObjectsAsync([first]);

        var processed = new List<Guid>();
        var activity = await RunSyncAsync(system, ConnectedSystemRunType.DeltaSynchronisation, processor =>
            processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
            {
                processed.Add(cso.Id);
                if (cso.Id != first.Id)
                    return;
                later.LastUpdated = longAgo;
                SyncRepo.UpdateConnectedSystemObjectsAsync([later]).GetAwaiter().GetResult();
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ObjectsToProcess, Is.EqualTo(1), "precondition: one object was counted at the start");
            Assert.That(processed, Is.EqualTo(new[] { first.Id, later.Id }), "the object marked mid-run above the cursor is processed by the same run");
            Assert.That(activity.ObjectsProcessed, Is.EqualTo(2), "processing more than the estimate is allowed");
            Assert.That(activity.Status, Is.Not.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(later.SynchronisationPending, Is.False, "and its mark is cleared");
        }
    }

    /// <summary>
    /// Full Synchronisation pages the same way: an object created while the run is in flight, with an Id above the
    /// cursor, is processed by the same run rather than left beyond a page budget fixed at the start.
    /// </summary>
    [Test]
    public async Task FullSync_ObjectCreatedAboveTheCursorMidRun_IsProcessedByTheSameRunAsync()
    {
        var (system, type, _) = await ArrangeSyncAsync();
        var first = await CreateCsoWithIdAsync(system.Id, type, 1, DateTime.UtcNow.AddHours(-2));
        var arrived = NewCsoWithId(system.Id, type, 9, DateTime.UtcNow.AddHours(-2));

        var processed = new List<Guid>();
        var activity = await RunSyncAsync(system, ConnectedSystemRunType.FullSynchronisation, processor =>
            processor.OnConnectedSystemObjectProcessedInPass2 = cso =>
            {
                processed.Add(cso.Id);
                if (cso.Id == first.Id)
                    SyncRepo.CreateConnectedSystemObjectsAsync([arrived]).GetAwaiter().GetResult();
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ObjectsToProcess, Is.EqualTo(1), "precondition: one object was counted at the start");
            Assert.That(processed, Is.EqualTo(new[] { first.Id, arrived.Id }));
            Assert.That(activity.ObjectsProcessed, Is.EqualTo(2));
        }
    }

    #endregion

    #region Helpers

    private void ClearAllMarks()
    {
        foreach (var cso in SyncRepo.ConnectedSystemObjects.Values)
            cso.SynchronisationPending = false;
    }

    private ConnectedSystemObject CsoWithExternalId(ConnectedSystemObjectType type, Guid externalId)
    {
        var externalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id;
        return SyncRepo.ConnectedSystemObjects.Values.Single(c =>
            c.AttributeValues.Any(av => av.AttributeId == externalIdAttributeId && av.GuidValue == externalId));
    }

    private static Guid IdOf(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static ConnectedSystemObject NewCsoWithId(int systemId, ConnectedSystemObjectType type, int n, DateTime created)
    {
        var cso = new ConnectedSystemObject
        {
            Id = IdOf(n), ConnectedSystemId = systemId, TypeId = type.Id, Type = type,
            ExternalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id,
            Status = ConnectedSystemObjectStatus.Normal, Created = created
        };
        foreach (var attribute in type.Attributes)
        {
            cso.AttributeValues.Add(attribute.IsExternalId
                ? new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, GuidValue = Guid.NewGuid() }
                : new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = $"{attribute.Name} {n}" });
        }
        return cso;
    }

    private Task<ConnectedSystemObject> CreateCsoWithIdAsync(int systemId, ConnectedSystemObjectType type, int n, DateTime created)
    {
        var cso = NewCsoWithId(systemId, type, n, created);
        SyncRepo.SeedConnectedSystemObject(cso);
        return Task.FromResult(cso);
    }

    private async Task<(ConnectedSystem System, ConnectedSystemObjectType Type)> ArrangeImportAsync()
    {
        var system = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(system.Id, "user");
        return (system, type);
    }

    private async Task<(ConnectedSystem System, ConnectedSystemObjectType Type, SyncRule Rule)> ArrangeSyncAsync()
    {
        var system = await CreateConnectedSystemAsync("HR");
        var type = await CreateCsoTypeAsync(system.Id, "person");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var rule = await CreateImportSyncRuleAsync(system.Id, type, mvType, "HR Import");
        SyncRepo.SetSyncPageSize(1);
        return (system, type, rule);
    }

    private async Task RunImportAsync(ConnectedSystem system, ConnectedSystemRunType runType, List<List<ConnectedSystemImportObject>> pages)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(system.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages), system, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
    }

    private async Task<Activity> RunSyncAsync(ConnectedSystem system, ConnectedSystemRunType runType, Action<SyncTaskProcessorBase>? configure = null)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(system.Id, runProfile, runType);
        system = await ReloadEntityAsync(system);

        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,
                system, runProfile, activity, new CancellationTokenSource());
            configure?.Invoke(processor);
            await processor.PerformFullSyncAsync();
        }
        else
        {
            var processor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo,
                system, runProfile, activity, new CancellationTokenSource());
            configure?.Invoke(processor);
            await processor.PerformDeltaSyncAsync();
        }

        return activity;
    }

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId, string? displayName = null) => new()
    {
        ObjectType = type.Name,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute
            {
                Name = type.Attributes.Single(a => a.IsExternalId).Name,
                Type = AttributeDataType.Guid,
                GuidValues = [externalId]
            },
            new ConnectedSystemImportObjectAttribute
            {
                Name = "DisplayName",
                Type = AttributeDataType.Text,
                StringValues = [displayName ?? $"User {externalId}"]
            }
        ]
    };

    #endregion
}
