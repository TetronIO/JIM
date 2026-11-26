// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Full Import whose deletion detection finds an object gone clears the object's Pending Exports, and records the
/// confirmation of a Delete JIM sent it on the object's deletion item (#3087), as a Delta Import does for an object the
/// Connector reports deleted. Without the record, the walk from the import that caused the Delete ends at the Delete.
/// </summary>
[TestFixture]
public class FullImportDeletedObjectExportConfirmationTests : WorkflowTestBase
{
    [Test]
    public async Task FullImport_ObjectWhoseDeleteWasExportedIsFoundDeleted_RecordsTheConfirmationOfTheDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var stayer = SeedCso(connectedSystem, type, "Stayer");
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var delete = SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Delete, PendingExportStatus.Exported);

        var activity = await RunFullImportAsync(connectedSystem, [Present(type, stayer)]);

        var edge = SyncRepo.CausalEdges.SingleOrDefault(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.PendingExportsConfirmed, Is.EqualTo(1), "the Activity counts the confirmed Delete with its other confirmations");
            Assert.That(await PendingExportOfAsync(leaver), Is.Null, "the object is gone, so its Delete is done");
            Assert.That(edge, Is.Not.Null, "the deletion confirms the exported Delete, so the import records which export it confirms");
            Assert.That(edge?.CausePendingExportId, Is.EqualTo(delete.Id), "the edge names the Delete it confirms");
            Assert.That(edge?.CauseConnectedSystemObjectId, Is.EqualTo(leaver.Id));
            Assert.That(ExecutionItemOf(edge)?.ObjectChangeType, Is.EqualTo(ObjectChangeType.Deleted),
                "the confirmation is recorded on the object's deletion");
            Assert.That(edge?.EffectSyncOutcome?.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed));
        }
    }

    [Test]
    public async Task FullImport_ObjectWhoseDeleteWasNeverSentIsFoundDeleted_RecordsNoConfirmationAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var stayer = SeedCso(connectedSystem, type, "Stayer");
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Delete, PendingExportStatus.Pending);

        await RunFullImportAsync(connectedSystem, [Present(type, stayer)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PendingExportOfAsync(leaver), Is.Null, "a Delete for an object already gone has nothing left to do");
            Assert.That(SyncRepo.CausalEdges.Where(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation), Is.Empty,
                "the target deleted the object itself: JIM's Delete was never sent, so nothing of JIM's was confirmed");
        }
    }

    [Test]
    public async Task FullImport_ObjectAlreadyFoundDeletedHasAnExportedDelete_ClearsItAndRecordsNoConfirmationAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var stayer = SeedCso(connectedSystem, type, "Stayer");
        var leaver = SeedCso(connectedSystem, type, "Leaver", ConnectedSystemObjectStatus.Obsolete);
        SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Delete, PendingExportStatus.Exported);

        await RunFullImportAsync(connectedSystem, [Present(type, stayer)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PendingExportOfAsync(leaver), Is.Null, "the stale Delete is cleared, as before");
            Assert.That(SyncRepo.CausalEdges.Where(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation), Is.Empty,
                "an earlier import recorded the deletion, so this one raises no item to record a confirmation on");
        }
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "person",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        return (connectedSystem, type);
    }

    private async Task<Activity> RunFullImportAsync(ConnectedSystem connectedSystem, List<ConnectedSystemImportObject> objects)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector([objects]), connectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private ConnectedSystemObject SeedCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, string displayName,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = Attribute(type, "ExternalId").Id,
            Status = status,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = Attribute(type, "ExternalId").Id, Attribute = Attribute(type, "ExternalId"), GuidValue = Guid.NewGuid(), ConnectedSystemObject = cso
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = Attribute(type, "DisplayName").Id, Attribute = Attribute(type, "DisplayName"), StringValue = displayName, ConnectedSystemObject = cso
        });

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>A whole-object export of <paramref name="cso"/>, with no attribute changes, as a Delete's export leaves it.</summary>
    private PendingExport SeedObjectExport(ConnectedSystem connectedSystem, ConnectedSystemObject cso, PendingExportChangeType changeType,
        PendingExportStatus status)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = connectedSystem.Id, ConnectedSystemObjectId = cso.Id,
            ChangeType = changeType, Status = status, CreatedAt = DateTime.UtcNow
        };
        SyncRepo.SeedPendingExport(export);
        return export;
    }

    private async Task<PendingExport?> PendingExportOfAsync(ConnectedSystemObject cso)
    {
        var byCso = await SyncRepo.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync([cso.Id]);
        return byCso.GetValueOrDefault(cso.Id);
    }

    private ActivityRunProfileExecutionItem? ExecutionItemOf(CausalEdge? edge) =>
        edge == null ? null : SyncRepo.Rpeis.GetValueOrDefault(edge.EffectRunProfileExecutionItemId);

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name) => type.Attributes.Single(a => a.Name == name);

    private static Guid ExternalIdOf(ConnectedSystemObject cso) => cso.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;

    private static ConnectedSystemImportObject Present(ConnectedSystemObjectType type, ConnectedSystemObject cso) => new()
    {
        ObjectType = type.Name,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [ExternalIdOf(cso)] },
            new ConnectedSystemImportObjectAttribute
            {
                Name = "DisplayName", Type = AttributeDataType.Text,
                StringValues = [cso.AttributeValues.Single(v => v.StringValue != null).StringValue!]
            }
        ]
    };

    #endregion
}
