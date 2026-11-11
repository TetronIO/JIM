// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// A confirming import reconciles each object once (#2908), however many times its save phase holds it. Reconciliation runs
/// as each batch is saved, so an object held twice (updated, then reported deleted under another Object Type, #2049) would
/// otherwise be reconciled by each batch holding it: its Pending Export judged and deleted twice, two items raised and the
/// confirmation counted twice. Such an object is reconciled once, after the save phase, as every object was before #2908.
/// </summary>
[TestFixture]
public class ReconcileSavedMoreThanOnceTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 100;
    private const int MailAttributeId = 40;

    [Test]
    public async Task ReconcileSavedBatches_ObjectHeldInTwoBatches_IsReconciledOnceAsync()
    {
        var (processor, repository, cso, pendingExportId) = BuildFixture();

        await ImportReconciliationInvoker.ReconcileAsync(processor, [cso, cso], [[cso], [cso]]);

        var activity = (Activity)typeof(SyncImportTaskProcessor).GetField("_activity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Select(item => item.ErrorType),
                Is.EqualTo(new ActivityRunProfileExecutionItemErrorType?[] { ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed }),
                "the miss is reported once, by one item");
            Assert.That(repository.PendingExports[pendingExportId].AttributeValueChanges.Single().Status,
                Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed), "kept, to be sent again");
        }
    }

    /// <summary>
    /// A user whose mail is not what was exported, with no Metaverse Object, and its Pending Export awaiting confirmation: a
    /// miss, which reconciliation reports on an item of its own and keeps, so judging it twice would report it twice.
    /// </summary>
    private static (SyncImportTaskProcessor Processor, SyncRepository Repository, ConnectedSystemObject Cso, Guid PendingExportId) BuildFixture()
    {
        var repository = new SyncRepository();
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = MailAttributeId, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId, TypeId = ObjectTypeId, Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            AttributeValues = [new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = MailAttributeId, Attribute = attribute, StringValue = "hedy@old.example" }]
        };
        repository.SeedConnectedSystemObject(cso);

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId, ConnectedSystemObjectId = cso.Id, ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), AttributeId = MailAttributeId, Attribute = attribute, ChangeType = PendingExportAttributeChangeType.Update,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, ExportAttemptCount = 1, StringValue = "hedy@new.example"
                }
            ]
        };
        repository.SeedPendingExport(pendingExport);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId, Name = "Panoply",
            ObjectTypes = [new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId, Selected = true, Attributes = [attribute] }]
        };
        var runProfile = new ConnectedSystemRunProfile { Name = "Delta Import", RunType = ConnectedSystemRunType.DeltaImport, ConnectedSystemId = ConnectedSystemId };

        var syncServer = new Mock<ISyncServer>();
        syncServer.Setup(s => s.GetSyncPageSizeAsync()).ReturnsAsync(500);

        var processor = new SyncImportTaskProcessor(null!, repository, syncServer.Object, new SyncEngine(), new MockFileConnector(),
            connectedSystem, runProfile, TestUtilities.CreateTestWorkerTask(new Activity(), initiatedBy: null), new CancellationTokenSource());

        return (processor, repository, cso, pendingExport.Id);
    }
}
