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
/// A Delta Import that reports an object deleted confirms every exported removal of a reference to it (#3053). A target
/// that deletes an object drops it from everything that referred to it, and Microsoft Entra ID, for one, then reports no
/// change on those groups: JIM's own removal, sent after the delete, finds the member already gone, so its Delta Import
/// never returns the group, and the removal would wait for the next Full Import. Nothing can still refer to an object the
/// target has deleted, so the deletion is the confirmation.
/// </summary>
[TestFixture]
public class DeltaImportDeletedReferenceRemovalTests : WorkflowTestBase
{
    [Test]
    public async Task DeltaImport_MemberDeleted_ConfirmsTheExportedRemovalOnAGroupTheImportDidNotReturnAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var group = SeedCso(connectedSystem, type, "Engineering", members: [leaver]);
        var removal = SeedRemovalExport(connectedSystem, type, group, leaver);

        var activity = await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        var item = activity.RunProfileExecutionItems.SingleOrDefault(i => i.ConnectedSystemObjectId == group.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PendingExportOfAsync(group), Is.Null, "the removal is confirmed, so the Pending Export is done");
            Assert.That(item, Is.Not.Null, "the confirmation is recorded on an execution item of the group");
            Assert.That(item!.ErrorType, Is.Null.Or.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet));
            Assert.That(removal.Id, Is.Not.EqualTo(Guid.Empty));
        }
    }

    [Test]
    public async Task DeltaImport_MemberDeleted_ConfirmsTheRemovalOnAGroupTheImportAlsoReturnedAsync()
    {
        // The group is returned for another change, still holding the member: its Delta Import said nothing of members.
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var group = SeedCso(connectedSystem, type, "Engineering", members: [leaver]);
        SeedRemovalExport(connectedSystem, type, group, leaver);

        var activity = await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver), Update(type, group, "Engineering Team", stillHolding: leaver)]]);

        var item = activity.RunProfileExecutionItems.Single(i => i.ConnectedSystemObjectId == group.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PendingExportOfAsync(group), Is.Null);
            Assert.That(item.ErrorType, Is.Null.Or.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet), "not reported as unconfirmed");
        }
    }

    [Test]
    public async Task DeltaImport_MemberDeleted_LeavesTheGroupsOtherChangesAwaitingConfirmationAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var group = SeedCso(connectedSystem, type, "Engineering", members: [leaver]);
        var export = SeedRemovalExport(connectedSystem, type, group, leaver, alsoRename: "Engineering Team");

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        var remaining = await PendingExportOfAsync(group);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining, Is.Not.Null);
            Assert.That(remaining!.Id, Is.EqualTo(export.Id));
            Assert.That(remaining.AttributeValueChanges.Select(c => c.ChangeType).ToList(), Is.EqualTo(new[] { PendingExportAttributeChangeType.Update }));
            Assert.That(remaining.AttributeValueChanges.Single().Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "a change the deletion says nothing about is left exactly as it was");
        }
    }

    [Test]
    public async Task DeltaImport_DeletionWithheldByItsLimit_ConfirmsNothingAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var group = SeedCso(connectedSystem, type, "Engineering", members: [leaver]);
        SeedRemovalExport(connectedSystem, type, group, leaver);

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]], maxDeletions: 0);

        var remaining = await PendingExportOfAsync(group);
        Assert.That(remaining?.AttributeValueChanges.Single().Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
    }

    [Test]
    public async Task DeltaImport_MemberDeleted_LeavesARemovalNotYetExportedToBeSentAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var group = SeedCso(connectedSystem, type, "Engineering", members: [leaver]);
        SeedRemovalExport(connectedSystem, type, group, leaver, status: PendingExportAttributeChangeStatus.Pending);

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        var remaining = await PendingExportOfAsync(group);
        Assert.That(remaining?.AttributeValueChanges.Single().Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending),
            "only an exported removal awaits confirmation");
    }

    [Test]
    public async Task DeltaImport_ObjectWhoseDeleteWasExportedIsReportedDeleted_ConfirmsTheDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var delete = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = connectedSystem.Id, ConnectedSystemObjectId = leaver.Id,
            ChangeType = PendingExportChangeType.Delete, Status = PendingExportStatus.Exported, CreatedAt = DateTime.UtcNow
        };
        SyncRepo.SeedPendingExport(delete);

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        Assert.That(await PendingExportOfAsync(leaver), Is.Null, "the target reports the object deleted, which confirms the Delete");
    }

    [Test]
    public async Task DeltaImport_ObjectWhoseDeleteWasExportedIsReportedDeleted_RecordsTheConfirmationOfTheDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var delete = SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Delete, PendingExportStatus.Exported);

        var activity = await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        // The walk from the leaver's import reaches the Delete's confirmation only through this edge (#3087).
        var edge = SyncRepo.CausalEdges.SingleOrDefault(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.PendingExportsConfirmed, Is.EqualTo(1), "the Activity counts the confirmed Delete with its other confirmations");
            Assert.That(edge, Is.Not.Null, "the deletion confirms the exported Delete, so the import records which export it confirms");
            Assert.That(edge?.CausePendingExportId, Is.EqualTo(delete.Id), "the edge names the Delete it confirms");
            Assert.That(edge?.CauseConnectedSystemObjectId, Is.EqualTo(leaver.Id));
            Assert.That(ExecutionItemOf(edge)?.ObjectChangeType, Is.EqualTo(ObjectChangeType.Deleted),
                "the confirmation is recorded on the object's deletion");
            Assert.That(edge?.EffectSyncOutcome?.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed));
        }
    }

    [Test]
    public async Task DeltaImport_ObjectWhoseDeleteWasNotConfirmedBeforeIsReportedDeleted_RecordsTheConfirmationOfTheDeleteAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        var delete = SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Delete, PendingExportStatus.ExportNotConfirmed);

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        var edge = SyncRepo.CausalEdges.SingleOrDefault(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation);
        Assert.That(edge?.CausePendingExportId, Is.EqualTo(delete.Id), "a Delete sent and not yet confirmed is confirmed by the deletion too");
    }

    [Test]
    public async Task DeltaImport_ObjectWhoseDeleteWasNeverSentIsReportedDeleted_RecordsNoConfirmationAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Delete, PendingExportStatus.Pending);

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PendingExportOfAsync(leaver), Is.Null, "a Delete for an object already gone has nothing left to do");
            Assert.That(SyncRepo.CausalEdges.Where(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation), Is.Empty,
                "the target deleted the object itself: JIM's Delete was never sent, so nothing of JIM's was confirmed");
        }
    }

    [Test]
    public async Task DeltaImport_ObjectWithAnExportedUpdateIsReportedDeleted_RecordsNoConfirmationAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        SeedObjectExport(connectedSystem, leaver, PendingExportChangeType.Update, PendingExportStatus.Exported);

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]]);

        Assert.That(SyncRepo.CausalEdges.Where(e => e.EdgeType == CausalEdgeType.ExportCausedImportConfirmation), Is.Empty,
            "a deletion does not confirm an Update: the update's values are not in the target, they are moot");
    }

    [Test]
    public async Task DeltaImport_DeletionWithheldByItsLimit_LeavesTheExportedDeleteAwaitingConfirmationAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var leaver = SeedCso(connectedSystem, type, "Leaver");
        SyncRepo.SeedPendingExport(new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = connectedSystem.Id, ConnectedSystemObjectId = leaver.Id,
            ChangeType = PendingExportChangeType.Delete, Status = PendingExportStatus.Exported, CreatedAt = DateTime.UtcNow
        });

        await RunDeltaImportAsync(connectedSystem, [[Delete(type, leaver)]], maxDeletions: 0);

        Assert.That(await PendingExportOfAsync(leaver), Is.Not.Null, "a deletion the Run Profile withheld confirms nothing");
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Entra ID");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "entity",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
        ]);
        return (connectedSystem, type);
    }

    private async Task<Activity> RunDeltaImportAsync(ConnectedSystem connectedSystem, List<List<ConnectedSystemImportObject>> pages, int? maxDeletions = null)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Delta Import", ConnectedSystemRunType.DeltaImport);
        runProfile.MaxDetectedDeletions = maxDeletions;
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.DeltaImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages), connectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
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

    private ActivityRunProfileExecutionItem? ExecutionItemOf(CausalEdge? edge) =>
        edge == null ? null : SyncRepo.Rpeis.GetValueOrDefault(edge.EffectRunProfileExecutionItemId);

    private async Task<PendingExport?> PendingExportOfAsync(ConnectedSystemObject cso)
    {
        var byCso = await SyncRepo.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync([cso.Id]);
        return byCso.GetValueOrDefault(cso.Id);
    }

    private ConnectedSystemObject SeedCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, string displayName,
        ConnectedSystemObject[]? members = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = Attribute(type, "ExternalId").Id,
            Status = ConnectedSystemObjectStatus.Normal,
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
        foreach (var member in members ?? [])
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = Attribute(type, "member").Id, Attribute = Attribute(type, "member"),
                ReferenceValueId = member.Id, UnresolvedReferenceValue = ExternalIdOf(member).ToString(), ConnectedSystemObject = cso
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>An exported Update of the group taking the member out, awaiting confirmation, as the leaver's export left it.</summary>
    private PendingExport SeedRemovalExport(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, ConnectedSystemObject group,
        ConnectedSystemObject member, PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
        string? alsoRename = null)
    {
        var export = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectId = group.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = status == PendingExportAttributeChangeStatus.Pending ? PendingExportStatus.Pending : PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow
        };
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), PendingExportId = export.Id, AttributeId = Attribute(type, "member").Id, Attribute = Attribute(type, "member"),
            ChangeType = PendingExportAttributeChangeType.Remove, Status = status, ExportAttemptCount = 1,
            ResolvedReferenceCsoId = member.Id, UnresolvedReferenceValue = ExternalIdOf(member).ToString()
        });
        if (alsoRename != null)
        {
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), PendingExportId = export.Id, AttributeId = Attribute(type, "DisplayName").Id, Attribute = Attribute(type, "DisplayName"),
                ChangeType = PendingExportAttributeChangeType.Update, Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                ExportAttemptCount = 1, StringValue = alsoRename
            });
        }

        SyncRepo.SeedPendingExport(export);
        return export;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name) => type.Attributes.Single(a => a.Name == name);

    private static Guid ExternalIdOf(ConnectedSystemObject cso) => cso.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;

    private static ConnectedSystemImportObject Delete(ConnectedSystemObjectType type, ConnectedSystemObject cso) => new()
    {
        ObjectType = type.Name,
        ChangeType = ObjectChangeType.Deleted,
        Attributes = [new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [ExternalIdOf(cso)] }]
    };

    private static ConnectedSystemImportObject Update(ConnectedSystemObjectType type, ConnectedSystemObject cso, string displayName,
        ConnectedSystemObject stillHolding) => new()
    {
        ObjectType = type.Name,
        ChangeType = ObjectChangeType.Updated,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [ExternalIdOf(cso)] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] },
            new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = [ExternalIdOf(stillHolding).ToString()] }
        ]
    };

    #endregion
}
