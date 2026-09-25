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
/// <para>
/// The step is two-phase (a lean per-candidate projection decides "was this seen?", and only the
/// genuinely unseen subset is loaded as a full graph and mutated), so several tests below also assert on
/// <see cref="SyncRepository.GetExportedCreatePendingExportsForPendingProvisioningCsosCallCount"/> and
/// <see cref="SyncRepository.GetExportedCreatePendingExportRetryCandidateSummariesCallCount"/>: the whole
/// point of the two-phase design is that the expensive full-graph load is skipped whenever it can be.
/// </para>
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repo.GetExportedCreatePendingExportRetryCandidateSummariesCallCount, Is.EqualTo(1),
                "one lean projection call per selected Object Type");
            Assert.That(repo.GetExportedCreatePendingExportsForPendingProvisioningCsosCallCount, Is.EqualTo(1),
                "the single unseen candidate is promoted to exactly one full-graph load");
        }
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
            Assert.That(repo.GetExportedCreatePendingExportRetryCandidateSummariesCallCount, Is.EqualTo(0),
                "the early exit must skip every repository query for a run type that can never retry anything");
            Assert.That(repo.GetExportedCreatePendingExportsForPendingProvisioningCsosCallCount, Is.EqualTo(0));
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
            Assert.That(repo.GetExportedCreatePendingExportRetryCandidateSummariesCallCount, Is.EqualTo(1),
                "the lean projection still runs once to decide the candidate was seen");
            Assert.That(repo.GetExportedCreatePendingExportsForPendingProvisioningCsosCallCount, Is.EqualTo(0),
                "every candidate was seen, so the expensive full-graph load must never be called");
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
            Assert.That(repo.GetExportedCreatePendingExportRetryCandidateSummariesCallCount, Is.EqualTo(0),
                "the early exit must skip every repository query when nothing was imported this run");
            Assert.That(repo.GetExportedCreatePendingExportsForPendingProvisioningCsosCallCount, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task RetryUnconfirmedExportedCreates_MixOfSeenAndUnseenCandidates_OnlyUnseenAreMarkedForRetryAsync()
    {
        // Two candidates of the same Object Type: one whose External Id this run reported, one it did
        // not. Only the unseen one may be retried; the seen one, and its RPEI, must be untouched.
        var (processor, repo,
            seenCsoId, seenPendingExportId, seenAttrChangeId,
            unseenCsoId, unseenPendingExportId, unseenAttrChangeId) = BuildTwoCandidateFixture();

        var seenImportAttribute = new ConnectedSystemImportObjectAttribute { Name = "id", Type = AttributeDataType.Text };
        seenImportAttribute.StringValues.Add(ExternalIdValue);
        var externalIdsImported = new List<ExternalIdPair>
        {
            new() { ConnectedSystemObjectTypeId = ObjectTypeId, ConnectedSystemImportObjectAttribute = seenImportAttribute }
        };

        await InvokeRetryStepAsync(processor, externalIdsImported, totalObjectsImported: 2);

        var seenExport = repo.PendingExports[seenPendingExportId];
        var unseenExport = repo.PendingExports[unseenPendingExportId];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seenExport.Status, Is.EqualTo(PendingExportStatus.Exported),
                "the seen candidate must be left exactly as it was");
            Assert.That(seenExport.AttributeValueChanges.Single(ac => ac.Id == seenAttrChangeId).Status,
                Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));

            Assert.That(unseenExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(unseenExport.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed),
                "the unseen candidate must be marked for retry");
            Assert.That(unseenExport.AttributeValueChanges.Single(ac => ac.Id == unseenAttrChangeId).Status,
                Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));

            Assert.That(repo.ConnectedSystemObjects[seenCsoId].Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
            Assert.That(repo.ConnectedSystemObjects[unseenCsoId].Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
        }

        // Same RPEI shape as the single-candidate retry case, and only for the unseen candidate: the seen
        // one must generate no RPEI at all.
        var rpeis = GetPrivateActivityRunProfileExecutionItems(processor);
        Assert.That(rpeis, Has.Count.EqualTo(1), "only the unseen candidate gets a retry RPEI");
        var rpei = rpeis[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ConnectedSystemObjectId, Is.EqualTo(unseenCsoId));
            Assert.That(rpei.ObjectChangeType, Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed));
        }

        Assert.That(repo.GetExportedCreatePendingExportsForPendingProvisioningCsosCallCount, Is.EqualTo(1),
            "exactly one chunked full-graph load, scoped to the single unseen candidate");
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

    /// <summary>
    /// Reads the processor's private RPEI accumulator (<c>_activityRunProfileExecutionItems</c>) via
    /// reflection, the same rationale as <see cref="InvokeRetryStepAsync"/>: the field is genuinely
    /// private production state (RPEIs are flushed incrementally, never exposed on the processor), and
    /// reflection lets the fixture assert on it without changing production code just to make it testable.
    /// </summary>
    private static List<ActivityRunProfileExecutionItem> GetPrivateActivityRunProfileExecutionItems(SyncImportTaskProcessor processor)
    {
        const string fieldName = "_activityRunProfileExecutionItems";
        var field = typeof(SyncImportTaskProcessor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"SyncImportTaskProcessor.{fieldName} was not found. Update this fixture if it has been renamed.");

        return (List<ActivityRunProfileExecutionItem>)field.GetValue(processor)!;
    }

    /// <summary>
    /// Second External Id value for <see cref="BuildTwoCandidateFixture"/>'s second (unseen) candidate;
    /// distinct from <see cref="ExternalIdValue"/>, which this run's imported set names as seen.
    /// </summary>
    private const string SecondExternalIdValue = "EMP0002";

    /// <summary>
    /// Two exported Create candidates of the same Object Type, so a single retry step invocation can be
    /// proven to treat them independently: one whose External Id (<see cref="ExternalIdValue"/>) the test
    /// then reports as imported (the "seen" candidate), and one whose External Id
    /// (<see cref="SecondExternalIdValue"/>) it never reports (the "unseen" candidate).
    /// </summary>
    private static (
        SyncImportTaskProcessor Processor, SyncRepository Repo,
        Guid SeenCsoId, Guid SeenPendingExportId, Guid SeenAttrChangeId,
        Guid UnseenCsoId, Guid UnseenPendingExportId, Guid UnseenAttrChangeId) BuildTwoCandidateFixture()
    {
        var repository = new SyncRepository();

        (Guid CsoId, Guid PendingExportId, Guid AttrChangeId) SeedCandidate(string externalIdValue)
        {
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
                        StringValue = externalIdValue
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

            return (cso.Id, pendingExport.Id, attrChangeId);
        }

        var seen = SeedCandidate(ExternalIdValue);
        var unseen = SeedCandidate(SecondExternalIdValue);

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
            Name = "Full Import",
            RunType = ConnectedSystemRunType.FullImport,
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

        return (processor, repository,
            seen.CsoId, seen.PendingExportId, seen.AttrChangeId,
            unseen.CsoId, unseen.PendingExportId, unseen.AttrChangeId);
    }
}
