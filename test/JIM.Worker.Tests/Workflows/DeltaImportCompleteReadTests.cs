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
/// #2720: a Delta Import whose Connector read every object in the Run Profile's scope instead of its changes
/// (<see cref="ConnectedSystemImportResult.CompleteRead"/>), as the LDAP, SQL and SCIM Connectors do when they cannot read
/// changes from the watermark, detects deletions by absence exactly as a Full Import does: within the Run Profile's
/// deletion limits, scoped to the Partition it targets, and with an exported Create nothing confirmed marked for retry.
/// Its deletions are found by absence rather than read from a change log, so a run that could not finish deletion
/// detection keeps the watermark it started with, and the next Delta Import reads everything and checks again.
/// </summary>
[TestFixture]
public class DeltaImportCompleteReadTests : WorkflowTestBase
{
    [Test]
    public async Task DeltaImport_ConnectorReadEveryObject_MarksAnObjectItNoLongerReturnedAsDeletedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        // Read across two pages, as a paged full read is; the object missing from both is gone.
        var (activity, _) = await RunDeltaImportAsync(connectedSystem,
            [[Present(type, held[0])], [Present(type, held[1])]], completeReadByPage: [true, true]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held),
                Is.EqualTo(new[] { ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Obsolete }));
            Assert.That(DeletionItems(activity), Has.Count.EqualTo(1));
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
            Assert.That(activity.Message, Does.Contain("1 no longer found and marked as deleted"));
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadOnlyChanges_MarksNoObjectMissingFromTheRunAsDeletedAsync()
    {
        // An ordinary Delta Import returns only what changed, so an object it did not return says nothing.
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0], "Changed")]], firstPageWatermark: "new-watermark");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(DeletionItems(activity), Is.Empty);
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("new-watermark"));
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadEveryObjectOverTheDeletionLimit_MarksNothingDeletedAndSaysSoAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 4);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0]), Present(type, held[1])]],
            completeReadByPage: [true], maxCount: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal), "a refused detection marks nothing");
            Assert.That(DeletionItems(activity), Is.Empty);
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(2));
            Assert.That(activity.WarningMessage, Does.Contain("Deletion detection found 2 objects").And.Contain("limit of 1"));
            Assert.That(activity.WarningMessage, Does.Not.Contain("run the Full Import again"),
                "the remedy is for the run that read everything, which is this Delta Import");
            Assert.That(activity.WarningMessage, Does.Contain("Each Delta Import will read every object and check again"));
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadEveryObjectOverTheDeletionLimit_KeepsTheWatermarkTheRunStartedWithAsync()
    {
        // Advancing it would leave the next Delta Import reading only changes after it, and the objects gone before it
        // would stay in JIM until someone ran a Full Import.
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 4);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0]), Present(type, held[1])]],
            completeReadByPage: [true], maxPercent: 25, firstPageWatermark: "new-watermark");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(2));
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"));
            Assert.That(activity.ImportWatermarkTo, Is.Null, "the run recorded no watermark, so its Activity must not claim one");
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadEveryObjectWithinTheDeletionLimits_RecordsTheNewWatermarkAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 4);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0]), Present(type, held[1]), Present(type, held[2])]],
            completeReadByPage: [true], maxCount: 1, firstPageWatermark: "new-watermark");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held.Skip(3)), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("new-watermark"));
            Assert.That(activity.ImportWatermarkTo, Is.EqualTo("new-watermark"));
        }
    }

    [Test]
    public async Task DeltaImport_PartitionTargetedRunReadEveryObject_DetectsDeletionsOnlyInItsPartitionAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var targeted = await CreatePartitionAsync(connectedSystem, "dc=corp,dc=local");
        var other = await CreatePartitionAsync(connectedSystem, "dc=partners,dc=local");
        var inTargeted = SeedHeld(connectedSystem, type, 2, targeted.Id);
        var inOther = SeedHeld(connectedSystem, type, 2, other.Id);

        // The run reads its own Partition only, and returns one of its two objects.
        await RunDeltaImportAsync(connectedSystem, [[Present(type, inTargeted[0])]], completeReadByPage: [true], partition: targeted);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, inTargeted),
                Is.EqualTo(new[] { ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Obsolete }));
            Assert.That(await StatusesAsync(connectedSystem, inOther), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal),
                "objects in a Partition the run did not read are not missing from it");
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadEveryObject_RetriesAnExportedCreateItDidNotSeeAsync()
    {
        // A complete read proves the object is not there, as a Full Import does, so its exported Create is sent again.
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 1);
        var provisioned = SeedCso(connectedSystem, type, Guid.NewGuid(), "Provisioned", ConnectedSystemObjectStatus.PendingProvisioning);
        var create = SeedExportedCreate(connectedSystem, provisioned);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0])]], completeReadByPage: [true]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExports[create.Id].Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadOnlyChanges_LeavesAnExportedCreateItDidNotSeeAloneAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 1);
        var provisioned = SeedCso(connectedSystem, type, Guid.NewGuid(), "Provisioned", ConnectedSystemObjectStatus.PendingProvisioning);
        var create = SeedExportedCreate(connectedSystem, provisioned);

        await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0], "Changed")]]);

        Assert.That(SyncRepo.PendingExports[create.Id].Status, Is.EqualTo(PendingExportStatus.Exported));
    }

    [Test]
    public async Task DeltaImport_ConnectorReadEveryObjectButAnExternalIdWasUnreadable_MarksNothingAndKeepsTheWatermarkAsync()
    {
        // The Object Type is left out of deletion detection, as on a Full Import, so this run did not check it: the next
        // Delta Import must read everything again rather than read only changes after a point that skips the check.
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunDeltaImportAsync(connectedSystem, [[Present(type, held[0]), UnreadableExternalId(type)]],
            completeReadByPage: [true], firstPageWatermark: "new-watermark");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(activity.WarningMessage, Does.Contain("was not checked for deleted objects"));
            Assert.That(activity.WarningMessage, Does.Not.Contain("run the Full Import again"));
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"));
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReadEveryObjectButReturnedNothing_MarksNothingAndKeepsTheWatermarkAsync()
    {
        // A read of everything that returns nothing more likely went wrong than found every object gone; deletion
        // detection does not run, as on a Full Import, and the run is not a reason to stop reading everything.
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 2);

        await RunDeltaImportAsync(connectedSystem, [[]], completeReadByPage: [true], firstPageWatermark: "new-watermark");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"));
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task DeltaImport_LaterPageContradictsTheFirstOnReadingEveryObject_FailsTheRunAndSavesNothingAsync(bool firstPage, bool secondPage)
    {
        // Whether an object missing from the run is gone depends on the answer, so a Connector that changes it part-way
        // leaves JIM unable to tell; the run stops before anything is staged.
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 3);

        var failure = Assert.ThrowsAsync<InvalidDataException>(() => RunDeltaImportAsync(connectedSystem,
            [[Present(type, held[0], "Changed")], [Present(type, held[1], "Changed")]], completeReadByPage: [firstPage, secondPage],
            firstPageWatermark: "new-watermark"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("page 2").And.Contain("every object"));
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(await DisplayNamesAsync(connectedSystem, held[0]), Is.EqualTo(new[] { "User 1" }), "nothing read was staged");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"));
        }
    }

    [Test]
    public async Task FullImport_LaterPageContradictsTheFirstOnReadingEveryObject_CompletesAndDetectsDeletionsAsync()
    {
        // A Full Import reads every object whatever its Connector says, so the answer cannot change what the run does, and
        // a Connector need not give one there; a contradiction is no reason to fail it.
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedHeld(connectedSystem, type, 3);

        var (activity, _) = await RunImportAsync(connectedSystem, [[Present(type, held[0])], [Present(type, held[1])]],
            completeReadByPage: [true, false], runType: ConnectedSystemRunType.FullImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.EqualTo(new[]
            {
                ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Obsolete
            }));
            Assert.That(DeletionItems(activity), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task DeltaImport_CancelledWhileALaterPageContradictsTheFirst_IsCancelledNotFailedAsync()
    {
        // A Connector that notices the cancellation hands back an empty page that says nothing about how it reads. The
        // run is abandoned before anything uses the answer, so it ends as cancelled, not as a fault in the Connector.
        var (connectedSystem, type) = await ArrangeAsync();
        connectedSystem.PersistedConnectorData = "original-watermark";
        var held = SeedHeld(connectedSystem, type, 3);
        using var cancellation = new CancellationTokenSource();

        await RunImportAsync(connectedSystem, [[Present(type, held[0], "Changed")], []], completeReadByPage: [true, false],
            firstPageWatermark: "new-watermark", cancellation: cancellation, beforePage: page =>
            {
                if (page == 1)
                    cancellation.Cancel();
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StatusesAsync(connectedSystem, held), Is.All.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(await DisplayNamesAsync(connectedSystem, held[0]), Is.EqualTo(new[] { "User 1" }), "nothing read was staged");
            Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("original-watermark"));
        }
    }

    #region Helpers

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        return (connectedSystem, type);
    }

    private async Task<ConnectedSystemPartition> CreatePartitionAsync(ConnectedSystem connectedSystem, string distinguishedName)
    {
        var partition = new ConnectedSystemPartition { ConnectedSystem = connectedSystem, ExternalId = distinguishedName, Name = distinguishedName, Selected = true };
        DbContext.ConnectedSystemPartitions.Add(partition);
        await DbContext.SaveChangesAsync();
        return partition;
    }

    private Task<(Activity Activity, ConnectedSystemRunProfile RunProfile)> RunDeltaImportAsync(ConnectedSystem connectedSystem,
        List<List<ConnectedSystemImportObject>> pages, IReadOnlyList<bool>? completeReadByPage = null, int? maxCount = null, int? maxPercent = null,
        string? firstPageWatermark = null, ConnectedSystemPartition? partition = null) =>
        RunImportAsync(connectedSystem, pages, completeReadByPage, maxCount, maxPercent, firstPageWatermark, partition);

    private async Task<(Activity Activity, ConnectedSystemRunProfile RunProfile)> RunImportAsync(ConnectedSystem connectedSystem,
        List<List<ConnectedSystemImportObject>> pages, IReadOnlyList<bool>? completeReadByPage = null, int? maxCount = null, int? maxPercent = null,
        string? firstPageWatermark = null, ConnectedSystemPartition? partition = null,
        ConnectedSystemRunType runType = ConnectedSystemRunType.DeltaImport, CancellationTokenSource? cancellation = null,
        Action<int>? beforePage = null)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, runType == ConnectedSystemRunType.FullImport ? "Full Import" : "Delta Import", runType);
        runProfile.MaxDetectedDeletions = maxCount;
        runProfile.MaxDetectedDeletionsPercent = maxPercent;
        runProfile.Partition = partition;
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages, firstPageWatermark, completeReadByPage, beforePage), connectedSystem, runProfile, workerTask,
            cancellation ?? new CancellationTokenSource());
        await processor.PerformImportAsync();
        return (activity, runProfile);
    }

    private static List<ActivityRunProfileExecutionItem> DeletionItems(Activity activity) =>
        activity.RunProfileExecutionItems.Where(i => i.ObjectChangeType == ObjectChangeType.Deleted).ToList();

    private async Task<List<ConnectedSystemObjectStatus>> StatusesAsync(ConnectedSystem connectedSystem, IEnumerable<ConnectedSystemObject> csos)
    {
        var statuses = new List<ConnectedSystemObjectStatus>();
        foreach (var cso in csos)
            statuses.Add((await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, cso.Id))!.Status);
        return statuses;
    }

    private async Task<string?[]> DisplayNamesAsync(ConnectedSystem connectedSystem, ConnectedSystemObject cso) =>
        (await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, cso.Id))!.AttributeValues
            .Where(v => v.Attribute?.Name == "DisplayName").Select(v => v.StringValue).ToArray();

    private List<ConnectedSystemObject> SeedHeld(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, int count, int? partitionId = null) =>
        Enumerable.Range(1, count).Select(i => SeedCso(connectedSystem, type, Guid.NewGuid(), $"User {i}", partitionId: partitionId)).ToList();

    private ConnectedSystemObject SeedCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, Guid externalId, string displayName,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal, int? partitionId = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id,
            Status = status,
            PartitionId = partitionId,
            Created = DateTime.UtcNow
        };
        foreach (var (attribute, guidValue, stringValue) in new[]
                 {
                     (type.Attributes.Single(a => a.IsExternalId), (Guid?)externalId, (string?)null),
                     (type.Attributes.Single(a => a.Name == "DisplayName"), null, displayName)
                 })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = attribute.Id,
                Attribute = attribute,
                GuidValue = guidValue,
                StringValue = stringValue,
                ConnectedSystemObject = cso
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>A Create exported to the Connected System that no import has yet confirmed, with its one exported value.</summary>
    private PendingExport SeedExportedCreate(ConnectedSystem connectedSystem, ConnectedSystemObject cso)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = cso.Type.Attributes.Single(a => a.Name == "DisplayName").Id,
                    ChangeType = PendingExportAttributeChangeType.Add,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    StringValue = "Provisioned"
                }
            ]
        };
        SyncRepo.SeedPendingExport(pendingExport);
        return pendingExport;
    }

    private static Guid ExternalIdOf(ConnectedSystemObject cso) =>
        cso.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;

    /// <summary>The object as the Connector read it: its External ID and Display Name, its own unless <paramref name="displayName"/> is given.</summary>
    private static ConnectedSystemImportObject Present(ConnectedSystemObjectType type, ConnectedSystemObject cso, string? displayName = null) =>
        new()
        {
            ObjectType = type.Name,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = type.Attributes.Single(a => a.IsExternalId).Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = [ExternalIdOf(cso)]
                },
                new ConnectedSystemImportObjectAttribute
                {
                    Name = "DisplayName",
                    Type = AttributeDataType.Text,
                    StringValues = [displayName ?? cso.AttributeValues.Single(v => v.StringValue != null).StringValue!]
                }
            ]
        };

    /// <summary>An object whose External ID the Connector could not read, so JIM cannot tell which object it is.</summary>
    private static ConnectedSystemImportObject UnreadableExternalId(ConnectedSystemObjectType type) =>
        new()
        {
            ObjectType = type.Name,
            Attributes = [new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = ["Unknown"] }],
            AttributeValueErrors =
            [
                new ConnectedSystemImportObjectAttributeValueError
                {
                    AttributeName = type.Attributes.Single(a => a.IsExternalId).Name,
                    Value = "not-a-guid",
                    Reason = "Not a valid GUID."
                }
            ]
        };

    #endregion
}
