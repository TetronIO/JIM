// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Models;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// <c>SyncImportTaskProcessor.RetryUnconfirmedExportedCreatesAsync</c>: the sibling step to deletion
/// detection that finds an exported Create Pending Export a Full Import never confirmed. Deletion
/// detection excludes Pending Provisioning Connected System Objects outright
/// (<c>ConnectedSystemRepository.BuildDeletionDetectionQuery</c>), because they have no External Id yet
/// to compare, so without this step an object whose Create was exported and then never reported back by
/// any subsequent import sits Pending Provisioning forever, its Pending Export stuck Status Exported
/// (<c>ExportExecutionServer.IsReadyForExecution</c> refuses to re-execute a Create with Status
/// Exported). Invokes the private method directly via reflection, the same pattern as
/// <see cref="DeselectedObjectTypeDeletionDetectionTests"/>, so the fixture drives production code
/// rather than reimplementing its object-type/partition scoping here.
/// </summary>
[TestFixture]
public class UnconfirmedExportedCreateRetryTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 100;
    private const int ExternalIdAttributeId = 10;
    private const int DisplayNameAttributeId = 20;
    private const string ExternalIdValue = "EMP0001";

    [Test]
    public async Task RetryUnconfirmedExportedCreates_FullImportDidNotSeeTheObject_MarksTheCreateForRetryAsync()
    {
        var (processor, repo, csoId, pendingExportId, attrChangeId) = BuildFixture(ConnectedSystemRunType.FullImport);

        // Nothing in the imported set names this object, but the run did read something (totalObjectsImported
        // > 0), so the "no objects imported means do nothing" guard does not suppress the step.
        await InvokeRetryStepAsync(processor, externalIdsImported: [], totalObjectsImported: 1);

        var pendingExport = repo.PendingExports[pendingExportId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
                "the retry must stay shaped as a Create - nothing has ever confirmed the object exists");
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed),
                "ExportNotConfirmed is what ExportExecutionServer.IsReadyForExecution treats as exportable again, " +
                "unlike the Exported status that got it stuck");
            var attrChange = pendingExport.AttributeValueChanges.Single(ac => ac.Id == attrChangeId);
            Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed),
                "the same per-change retry accounting reconciliation applies to an unconfirmed change");
        }

        Assert.That(repo.ConnectedSystemObjects[csoId].Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "the CSO's own status is untouched by this step - only the Pending Export retries");
    }

    [Test]
    public async Task RetryUnconfirmedExportedCreates_DeltaImportDidNotSeeTheObject_LeavesItUntouchedAsync()
    {
        // A Delta Import only reports changes, so absence from its payload proves nothing about whether
        // the object still exists; marking a retry from it would be wrong.
        var (processor, repo, _, pendingExportId, attrChangeId) = BuildFixture(ConnectedSystemRunType.DeltaImport);

        await InvokeRetryStepAsync(processor, externalIdsImported: [], totalObjectsImported: 1);

        var pendingExport = repo.PendingExports[pendingExportId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported),
                "unchanged: a Delta Import must never trigger this retry");
            var attrChange = pendingExport.AttributeValueChanges.Single(ac => ac.Id == attrChangeId);
            Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    [Test]
    public async Task RetryUnconfirmedExportedCreates_FullImportSawTheObject_LeavesItUntouchedAsync()
    {
        // The object's External Id appears in this run's own imported set: it was seen, so it must not
        // be marked for retry (ordinary reconciliation - not this step - is what handles a seen object).
        var (processor, repo, _, pendingExportId, attrChangeId) = BuildFixture(ConnectedSystemRunType.FullImport);

        var seenImportAttribute = new ConnectedSystemImportObjectAttribute { Name = "id", Type = AttributeDataType.Text };
        seenImportAttribute.StringValues.Add(ExternalIdValue);
        var externalIdsImported = new List<ExternalIdPair>
        {
            new() { ConnectedSystemObjectTypeId = ObjectTypeId, ConnectedSystemImportObjectAttribute = seenImportAttribute }
        };

        await InvokeRetryStepAsync(processor, externalIdsImported, totalObjectsImported: 1);

        var pendingExport = repo.PendingExports[pendingExportId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported),
                "unchanged: the object was seen this run, so it is not a retry candidate");
            var attrChange = pendingExport.AttributeValueChanges.Single(ac => ac.Id == attrChangeId);
            Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    [Test]
    public async Task RetryUnconfirmedExportedCreates_NoObjectsImportedAtAll_LeavesItUntouchedAsync()
    {
        // Mirrors deletion detection's own guard: a Full Import that read literally nothing must not be
        // read as proof that every outstanding Create is now absent.
        var (processor, repo, _, pendingExportId, attrChangeId) = BuildFixture(ConnectedSystemRunType.FullImport);

        await InvokeRetryStepAsync(processor, externalIdsImported: [], totalObjectsImported: 0);

        var pendingExport = repo.PendingExports[pendingExportId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Exported));
            var attrChange = pendingExport.AttributeValueChanges.Single(ac => ac.Id == attrChangeId);
            Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    /// <summary>
    /// Invokes the private retry step exactly as a Full Import calls it at the end of deletion detection,
    /// with no CSOs already processed this run (an empty <c>connectedSystemObjectsToBeUpdated</c>) and no
    /// partition scoping.
    /// </summary>
    private static async Task InvokeRetryStepAsync(
        SyncImportTaskProcessor processor, IReadOnlyCollection<ExternalIdPair> externalIdsImported, int totalObjectsImported)
    {
        const string methodName = "RetryUnconfirmedExportedCreatesAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. If it has been renamed or its signature has " +
                "changed, update this fixture to invoke the current production method; do not reimplement its " +
                "unseen-Create scoping here, because that scoping is the behaviour under test.");

        await (Task)method.Invoke(processor, [externalIdsImported, new List<ConnectedSystemObject>(), null, totalObjectsImported])!;
    }

    private static (SyncImportTaskProcessor Processor, SyncRepository Repo, Guid CsoId, Guid PendingExportId, Guid AttrChangeId) BuildFixture(
        ConnectedSystemRunType runType)
    {
        var repository = new SyncRepository();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            Created = DateTime.UtcNow,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    AttributeId = ExternalIdAttributeId,
                    StringValue = ExternalIdValue
                }
            ]
        };
        repository.SeedConnectedSystemObject(cso);

        var attrChangeId = Guid.NewGuid();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = attrChangeId,
                    AttributeId = DisplayNameAttributeId,
                    ChangeType = PendingExportAttributeChangeType.Add,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    StringValue = "Not Yet Confirmed"
                }
            ]
        };
        repository.SeedPendingExport(pendingExport);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Yellowstone",
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = ObjectTypeId,
                    Name = "User",
                    ConnectedSystemId = ConnectedSystemId,
                    Selected = true,
                    Attributes =
                    [
                        new ConnectedSystemObjectTypeAttribute
                        {
                            Id = ExternalIdAttributeId,
                            Name = "id",
                            Type = AttributeDataType.Text,
                            IsExternalId = true,
                            Selected = true
                        }
                    ]
                }
            ]
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            Name = runType == ConnectedSystemRunType.FullImport ? "Full Import" : "Delta Import",
            RunType = runType,
            ConnectedSystemId = ConnectedSystemId
        };

        var workerTask = TestUtilities.CreateTestWorkerTask(new Activity(), initiatedBy: null);
        var cancellationTokenSource = new CancellationTokenSource();

        var processor = new SyncImportTaskProcessor(
            null!,
            repository,
            null!,
            new SyncEngine(),
            new MockFileConnector(),
            connectedSystem,
            runProfile,
            workerTask,
            cancellationTokenSource);

        return (processor, repository, cso.Id, pendingExport.Id, attrChangeId);
    }
}
