// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Proves that the worker's idle-time housekeeping (issue #1020) records an auditable Activity for each batch that
/// actually does work: deleting Metaverse Objects whose deletion grace period has expired and staging the resulting
/// reference-recall Pending Exports. Each deleted Metaverse Object and each staged recall Pending Export must appear
/// as a Run Profile Execution Item on the Activity, per-object failures must be recorded as error items with an
/// error-completion status, and a quiet idle tick (nothing to delete) must record no Activity at all.
/// </summary>
[TestFixture]
public class HousekeepingActivityWorkflowTests
{
    private const int TargetSystemId = 5;
    private const string TargetSystemName = "Target LDAP";
    private const int MvPersonTypeId = 40;
    private const int MvGroupTypeId = 50;
    private const int MvMemberAttributeId = 60;
    private const int CsGroupTypeId = 70;
    private const int CsUserTypeId = 71;
    private const int CsMemberAttributeId = 80;
    private const int CsDnAttributeId = 81;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private Worker WorkerInstance { get; set; } = null!;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;

    private List<Activity> _createdActivities = null!;
    private List<ActivityRunProfileExecutionItem> _persistedRpeis = null!;
    private List<Guid> _deletedMvoIds = null!;

    private MetaverseObjectType MvPersonType { get; set; } = null!;
    private MetaverseObjectType MvGroupType { get; set; } = null!;
    private MetaverseAttribute MvMemberAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsMemberAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsDnAttribute { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        _createdActivities = [];
        _persistedRpeis = [];
        _deletedMvoIds = [];

        // Metaverse repository: eligibility and deletion are mocked at the repository boundary (the production
        // implementation is raw-SQL and cannot run against the in-memory provider); the reference recall flow
        // runs for real against the in-memory sync repository.
        _mockMetaverseRepository = new Mock<IMetaverseRepository>();
        _mockMetaverseRepository
            .Setup(r => r.DeleteMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(mvo => _deletedMvoIds.Add(mvo.Id))
            .Returns(Task.CompletedTask);

        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockActivityRepository
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(activity => _createdActivities.Add(activity))
            .Returns(Task.CompletedTask);
        _mockActivityRepository
            .Setup(r => r.CreateActivityRunProfileExecutionItemsAsync(It.IsAny<IReadOnlyCollection<ActivityRunProfileExecutionItem>>()))
            .Callback<IReadOnlyCollection<ActivityRunProfileExecutionItem>>(items => _persistedRpeis.AddRange(items))
            .Returns(Task.CompletedTask);
        _mockActivityRepository
            .Setup(r => r.GetActivityRpeiErrorCountsAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid activityId) =>
            {
                var rpeis = _persistedRpeis.Where(r => r.ActivityId == activityId).ToList();
                return (
                    rpeis.Count(r => r.ErrorType.HasValue && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet),
                    rpeis.Count,
                    rpeis.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError));
            });

        // No settings rows exist, so every Service Setting read falls back to its default
        // (sync outcome tracking: Detailed; CSO and MVO change tracking: enabled).
        _mockServiceSettingsRepository = new Mock<IServiceSettingsRepository>();
        _mockServiceSettingsRepository
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((ServiceSetting?)null);

        _mockRepository = new Mock<IRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepository.Object);

        SyncRepo = new SyncRepository();
        Jim = new JimApplication(_mockRepository.Object, syncRepository: SyncRepo);

        WorkerInstance = new Worker(
            new Mock<IJimApplicationFactory>().Object,
            new Mock<IConnectorFactory>().Object,
            new Mock<IDbContextFactory<JimDbContext>>().Object);

        SeedMetaverseTypesAndExportRule();
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
        WorkerInstance?.Dispose();
    }

    /// <summary>
    /// The core batch: one grace-period-expired Metaverse Object, referenced by a group with a provisioned target
    /// CSO, is deleted by housekeeping. The batch must be recorded as a Complete Metaverse Object Housekeeping
    /// Activity carrying one Deleted item for the Metaverse Object and one Pending Export item (with the Pending
    /// Export id) for the staged recall export, with TotalPendingExports reflecting the staged export.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoReferencedByGroup_RecordsCompleteActivityWithItemsAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedEligibleMemberWithTargetCso("Lena Leaver");
        var groupMvo = SeedGroupMvoReferencing("Team Alpha", memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberMvo.Id, memberDn);
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([memberMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync(Jim);

        // Assert: the batch is recorded as a system-initiated Scheduled Identity Deletion Activity. The enum
        // member keeps its original name (persisted by ordinal, append-only); only the display name changed.
        var activity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        Assert.That(activity, Is.Not.Null,
            "A housekeeping batch that deletes Metaverse Objects must record a Scheduled Identity Deletion Activity");
        Assert.That(activity!.TargetName, Is.EqualTo("Scheduled Identity Deletion"));
        Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Execute));
        Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(activity.InitiatedByName, Is.EqualTo("System"));
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete),
            "A fully successful batch must complete cleanly");
        Assert.That(activity.ObjectsToProcess, Is.EqualTo(1));
        Assert.That(activity.ObjectsProcessed, Is.EqualTo(1));

        // Assert: the Metaverse Object was actually deleted.
        Assert.That(_deletedMvoIds, Is.EquivalentTo(new[] { memberMvo.Id }));

        // Assert: one RPEI per deleted Metaverse Object.
        var rpeis = _persistedRpeis.Where(r => r.ActivityId == activity.Id).ToList();
        var deletedRpeis = rpeis.Where(r => r.ObjectChangeType == ObjectChangeType.Deleted).ToList();
        Assert.That(deletedRpeis, Has.Count.EqualTo(1),
            "Each deleted Metaverse Object must be recorded as a Deleted execution item");
        Assert.That(deletedRpeis[0].DisplayNameSnapshot, Is.EqualTo("Lena Leaver"));

        // Assert: one RPEI per staged recall Pending Export, carrying the Pending Export id.
        var stagedRecallPendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        var pendingExportRpeis = rpeis.Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport).ToList();
        Assert.That(pendingExportRpeis, Has.Count.EqualTo(1),
            "Each staged recall Pending Export must be recorded as a Pending Export execution item");
        Assert.That(pendingExportRpeis[0].PendingExportId, Is.EqualTo(stagedRecallPendingExport.Id));
        Assert.That(pendingExportRpeis[0].ConnectedSystemObjectId, Is.EqualTo(groupTargetCso.Id));

        // Assert: summary stats reflect the staged recall export and the deletion itself.
        Assert.That(activity.TotalPendingExports, Is.GreaterThanOrEqualTo(1),
            "TotalPendingExports must reflect the staged recall Pending Export");
        Assert.That(activity.TotalDeleted, Is.EqualTo(1),
            "TotalDeleted must count MvoDeleted outcomes so list views show what the batch deleted");
    }

    /// <summary>
    /// Causal provenance (#1223): the group's recall item must record which deleted object caused its removal.
    ///
    /// This is the grace-period path, and it is the case with no other answer available. Housekeeping runs in
    /// its own Activity, minutes or weeks after the run that scheduled the deletion; the removal is recorded
    /// against the referencing group, not against anything deleted; and deletion has already nulled the
    /// reference foreign keys that connected them. Without the edge, an administrator looking at "Team Alpha
    /// lost a member" has nothing at all to go on, which is the exact defect the feature exists to fix.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoReferencedByGroup_RecordsWhatCausedTheRecallAsync()
    {
        var (memberMvo, memberDn) = SeedEligibleMemberWithTargetCso("Lena Leaver");
        // The decision-time snapshot rides across from the run that scheduled the deletion, and is the only
        // surviving source of the reason and the system that triggered it.
        memberMvo.DeletionPolicySnapshotJson = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected,
            TriggeringSystemId = 9,
            TriggeringSystemName = "Yellowstone APAC"
        }.ToJson();
        var groupMvo = SeedGroupMvoReferencing("Team Alpha", memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberMvo.Id, memberDn);
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([memberMvo]);

        await WorkerInstance.PerformHousekeepingAsync(Jim);

        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var recallRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id
            && r.ObjectChangeType == ObjectChangeType.PendingExport
            && r.ConnectedSystemObjectId == groupTargetCso.Id);

        var edge = recallRpei.CausalEdges.SingleOrDefault();
        Assert.That(edge, Is.Not.Null,
            "the group's removal item is the only record of the change and nothing on it says why; the edge is what supplies the cause");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(edge!.EdgeType, Is.EqualTo(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval));
            Assert.That(edge!.CauseMetaverseObjectId, Is.EqualTo(memberMvo.Id));
            Assert.That(edge!.CauseDisplayName, Is.EqualTo("Lena Leaver"),
                "the deleted object is already gone, so the chain can only name it from the snapshot");
            Assert.That(edge!.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected),
                "the reason comes from the decision-time snapshot; the deciding run ended in a previous Activity");
            Assert.That(edge!.ConnectedSystemId, Is.EqualTo(9));
            Assert.That(edge!.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(edge!.CauseSyncOutcome, Is.Not.Null,
                "the cause must point at the deletion outcome recorded in this same batch, not just at the object");
        }
    }

    /// <summary>
    /// The queueing-provenance stamp (#1223): housekeeping's recall item is what queued the group's removal
    /// export, and the Pending Export must record it, or the export run that carries the removal out has
    /// nothing to walk back along and the removal reads as having no cause at all.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoReferencedByGroup_StampsTheRecallItemOnItsPendingExportAsync()
    {
        var (memberMvo, memberDn) = SeedEligibleMemberWithTargetCso("Lena Leaver");
        var groupMvo = SeedGroupMvoReferencing("Team Alpha", memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberMvo.Id, memberDn);
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([memberMvo]);

        await WorkerInstance.PerformHousekeepingAsync(Jim);

        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var recallRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id
            && r.ObjectChangeType == ObjectChangeType.PendingExport
            && r.ConnectedSystemObjectId == groupTargetCso.Id);
        var recallPendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);

        Assert.That(recallPendingExport.QueuedByRunProfileExecutionItemId, Is.EqualTo(recallRpei.Id));
    }

    /// <summary>
    /// Deletion cascade (#1044): a grace-period-expired Metaverse Object whose target Connected System Object is
    /// matched by an export Synchronisation Rule with a Delete deprovisioning action stages a delete Pending Export.
    /// That export deprovisions a real account, so it must be recorded on the housekeeping Activity as a consequence
    /// of the deletion: a DeprovisionQueued outcome nested beneath the deleted object's MvoDeleted outcome, not
    /// left visible only in the service log.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoWithDeleteDeprovisionRule_NestsDeleteExportUnderMvoDeletedAsync()
    {
        // Arrange
        var (personMvo, personTargetCso) = SeedEligiblePersonWithDeprovisionableTargetCso("Dan Deprovisioned");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([personMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync(Jim);

        // Assert: the delete Pending Export was staged for the target Connected System Object.
        var deletePendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ChangeType == PendingExportChangeType.Delete);
        Assert.That(deletePendingExport.ConnectedSystemObjectId, Is.EqualTo(personTargetCso.Id));

        // Assert: and that it is recorded as a consequence of the deletion on the deleted object's item.
        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var rpeis = _persistedRpeis.Where(r => r.ActivityId == activity.Id).ToList();
        Assert.That(rpeis.Any(r => r.ObjectChangeType == ObjectChangeType.PendingExport), Is.False,
            "The staged export belongs on the deletion's causality tree, not on an execution item of its own");

        var deletionRpei = rpeis.Single(r => r.ObjectChangeType == ObjectChangeType.Deleted);
        var mvoDeletedOutcome = deletionRpei.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        var cascadeOutcome = mvoDeletedOutcome.Children.SingleOrDefault();
        Assert.That(cascadeOutcome, Is.Not.Null,
            "The staged delete Pending Export must be recorded as a consequence of the Metaverse Object deletion");
        Assert.That(cascadeOutcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued),
            "A staged Delete Pending Export is a queued deprovision, not an attribute update");
        Assert.That(cascadeOutcome.TargetEntityId, Is.EqualTo(deletePendingExport.Id));
        Assert.That(cascadeOutcome.TargetEntityDescription, Is.EqualTo(TargetSystemName),
            "The outcome must name the Connected System the account is being deleted from; the identity is named by the item it hangs off");
        Assert.That(activity.TotalPendingExports, Is.EqualTo(1),
            "TotalPendingExports must count the staged deprovisioning export");
    }

    /// <summary>
    /// Decision-time policy snapshot carry-through (#119): when housekeeping deletes a Metaverse Object that
    /// carries a DeletionPolicySnapshotJson captured at mark-time, the snapshot must be copied onto the
    /// deletion record it writes, so the final record reflects the policy that scheduled the deletion, not
    /// the configuration at execution time.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_MvoCarryingPolicySnapshot_CopiesSnapshotOntoDeletionRecordAsync()
    {
        // Arrange: an eligible Metaverse Object carrying the mark-time snapshot and triggering system fields.
        var markTimeSnapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            TriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            SelectedSourceSystemIds = [1],
            SelectedSourceSystemNames = ["Source HR"],
            GracePeriod = TimeSpan.FromMinutes(1),
            TriggeringSystemId = 1,
            TriggeringSystemName = "Source HR"
        };
        var scheduledMvo = CreateEligiblePersonMvo("Sana Scheduled");
        scheduledMvo.DeletionTriggeredBySystemId = 1;
        scheduledMvo.DeletionTriggeredBySystemName = "Source HR";
        scheduledMvo.DeletionPolicySnapshotJson = markTimeSnapshot.ToJson();
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([scheduledMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync(Jim);

        // Assert: the deletion record carries the mark-time snapshot verbatim.
        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var deletionRpei = _persistedRpeis
            .Where(r => r.ActivityId == activity.Id)
            .Single(r => r.ObjectChangeType == ObjectChangeType.Deleted);
        Assert.That(deletionRpei.DeletionPolicySnapshotJson, Is.EqualTo(scheduledMvo.DeletionPolicySnapshotJson),
            "The housekeeping deletion record must carry the policy snapshot captured when the deletion was scheduled");

        var carried = MvoDeletionPolicySnapshot.FromJson(deletionRpei.DeletionPolicySnapshotJson);
        Assert.That(carried, Is.Not.Null, "The carried snapshot must remain deserialisable");
        Assert.That(carried!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
        Assert.That(carried!.TriggeringSystemId, Is.EqualTo(1));
        Assert.That(carried!.TriggeringSystemName, Is.EqualTo("Source HR"));
        Assert.That(carried!.GracePeriod, Is.EqualTo(TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// Idle-tick silence pin: a housekeeping pass with nothing to delete must create no Activity at all, so a
    /// quiet worker does not fill the Activities list with no-op entries.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_NothingToDelete_CreatesNoActivityAsync()
    {
        // Arrange
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync(Jim);

        // Assert
        Assert.That(_createdActivities, Is.Empty, "An idle housekeeping tick must not create an Activity");
        _mockActivityRepository.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    /// <summary>
    /// Failure path: a Metaverse Object whose deletion throws must be recorded as an error execution item, the
    /// remaining objects must still be processed, and the Activity must finish with an error-completion status
    /// rather than silent success.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_OneDeletionFails_RecordsErrorItemAndErrorCompletionAsync()
    {
        // Arrange: two eligible Metaverse Objects; deleting the first throws.
        var failingMvo = CreateEligiblePersonMvo("Fiona Failure");
        var okMvo = CreateEligiblePersonMvo("Olive Okay");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([failingMvo, okMvo]);
        _mockMetaverseRepository
            .Setup(r => r.DeleteMetaverseObjectAsync(It.Is<MetaverseObject>(m => m.Id == failingMvo.Id)))
            .ThrowsAsync(new InvalidOperationException("Simulated deletion failure"));

        // Act
        await WorkerInstance.PerformHousekeepingAsync(Jim);

        // Assert: the batch Activity finishes with an error completion, not silent success.
        var activity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        Assert.That(activity, Is.Not.Null, "A batch with work to do must record an Activity even when items fail");
        Assert.That(activity!.Status, Is.EqualTo(ActivityStatus.CompleteWithError),
            "A batch with a failed deletion must finish with an error-completion status");
        Assert.That(activity.ObjectsProcessed, Is.EqualTo(2));
        Assert.That(activity.TotalErrors, Is.EqualTo(1));

        // Assert: the failure is recorded as an error execution item.
        var rpeis = _persistedRpeis.Where(r => r.ActivityId == activity.Id).ToList();
        var errorRpeis = rpeis
            .Where(r => r.ErrorType.HasValue && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();
        Assert.That(errorRpeis, Has.Count.EqualTo(1), "The failed deletion must be recorded as an error execution item");
        Assert.That(errorRpeis[0].ErrorMessage, Does.Contain("Simulated deletion failure"));
        Assert.That(errorRpeis[0].DisplayNameSnapshot, Is.EqualTo("Fiona Failure"));

        // Assert: the successful deletion still happened and is still recorded.
        Assert.That(_deletedMvoIds, Is.EquivalentTo(new[] { okMvo.Id }));
        Assert.That(rpeis.Count(r => r.ObjectChangeType == ObjectChangeType.Deleted && r.ErrorType is null or ActivityRunProfileExecutionItemErrorType.NotSet),
            Is.EqualTo(1), "The successful deletion must still be recorded as a Deleted execution item");
    }

    #region helpers

    /// <summary>
    /// Seeds the Metaverse Object Types (a Person type with an expired-grace-period deletion rule and a Group type
    /// with a multi-valued reference attribute) and the export Synchronisation Rule that maps the group's Static
    /// Members to the target system's member attribute, mirroring the reference recall test shapes.
    /// </summary>
    private void SeedMetaverseTypesAndExportRule()
    {
        MvPersonType = new MetaverseObjectType
        {
            Id = MvPersonTypeId,
            Name = "Person",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromMinutes(1)
        };

        MvMemberAttribute = new MetaverseAttribute
        {
            Id = MvMemberAttributeId,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        MvGroupType = new MetaverseObjectType
        {
            Id = MvGroupTypeId,
            Name = "Group",
            Attributes = [MvMemberAttribute]
        };

        CsMemberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsMemberAttributeId,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
        CsDnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsDnAttributeId,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            IsSecondaryExternalId = true,
            Selected = true
        };

        var exportRule = new SyncRule
        {
            Id = 900,
            Name = "Target Export Groups",
            Enabled = true,
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = TargetSystemId,
            MetaverseObjectTypeId = MvGroupTypeId,
            AttributeFlowRules =
            {
                new SyncRuleMapping
                {
                    Id = 901,
                    TargetConnectedSystemAttribute = CsMemberAttribute,
                    TargetConnectedSystemAttributeId = CsMemberAttribute.Id,
                    Sources =
                    {
                        new SyncRuleMappingSource
                        {
                            Id = 902,
                            Order = 0,
                            MetaverseAttribute = MvMemberAttribute,
                            MetaverseAttributeId = MvMemberAttribute.Id
                        }
                    }
                }
            }
        };
        SyncRepo.SeedSyncRule(exportRule);
    }

    /// <summary>
    /// Creates a Person Metaverse Object marked for deletion with an expired grace period, as the eligibility
    /// query would return it.
    /// </summary>
    private MetaverseObject CreateEligiblePersonMvo(string displayName)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = MvPersonType,
            Origin = MetaverseObjectOrigin.Projected,
            CachedDisplayName = displayName,
            LastConnectorDisconnectedDate = DateTime.UtcNow.AddMinutes(-10),
            DeletionInitiatedByType = ActivityInitiatorType.System,
            DeletionInitiatedByName = "System"
        };
    }

    /// <summary>
    /// Seeds an eligible member Metaverse Object joined to a provisioned target CSO whose DN attribute carries
    /// the given value, so reference recall can pre-resolve the removal value before deletion.
    /// </summary>
    private (MetaverseObject Mvo, string Dn) SeedEligibleMemberWithTargetCso(string displayName)
    {
        var memberMvo = CreateEligiblePersonMvo(displayName);
        SyncRepo.SeedMetaverseObject(memberMvo);

        const string dn = "uid=lena.leaver,ou=People,dc=glitterband,dc=local";
        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsGroupTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = memberMvo.Id
        };
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = CsDnAttribute,
            AttributeId = CsDnAttribute.Id,
            StringValue = dn
        });
        SyncRepo.SeedConnectedSystemObject(targetCso);

        return (memberMvo, dn);
    }

    /// <summary>
    /// Seeds an eligible Person Metaverse Object joined to a provisioned target Connected System Object of a user
    /// type that an export Synchronisation Rule deprovisions by deleting (issue #655), so the deletion cascades
    /// into a delete Pending Export.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystemObject TargetCso) SeedEligiblePersonWithDeprovisionableTargetCso(string displayName)
    {
        SyncRepo.SeedSyncRule(new SyncRule
        {
            Id = 910,
            Name = "Target Export Users",
            Enabled = true,
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = TargetSystemId,
            ConnectedSystem = new ConnectedSystem { Id = TargetSystemId, Name = TargetSystemName },
            ConnectedSystemObjectTypeId = CsUserTypeId,
            MetaverseObjectTypeId = MvPersonTypeId,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        });

        var personMvo = CreateEligiblePersonMvo(displayName);
        SyncRepo.SeedMetaverseObject(personMvo);

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsUserTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = personMvo.Id,
            SecondaryExternalIdAttributeId = CsDnAttributeId
        };
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = CsDnAttribute,
            AttributeId = CsDnAttributeId,
            StringValue = "uid=dan.deprovisioned,ou=People,dc=glitterband,dc=local"
        });
        SyncRepo.SeedConnectedSystemObject(targetCso);

        return (personMvo, targetCso);
    }

    /// <summary>
    /// Seeds a group Metaverse Object whose Static Members reference the given Metaverse Object.
    /// </summary>
    private MetaverseObject SeedGroupMvoReferencing(string displayName, Guid referencedMvoId)
    {
        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = MvGroupType,
            CachedDisplayName = displayName
        };
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = MvMemberAttribute,
            AttributeId = MvMemberAttribute.Id,
            ReferenceValueId = referencedMvoId
        });
        SyncRepo.SeedMetaverseObject(groupMvo);
        return groupMvo;
    }

    /// <summary>
    /// Seeds the group's provisioned target CSO with a member attribute value referencing the member's target CSO.
    /// </summary>
    private ConnectedSystemObject SeedGroupTargetCso(MetaverseObject groupMvo, Guid memberMvoId, string memberDn)
    {
        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsGroupTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = groupMvo.Id
        };
        var memberCso = SyncRepo.ConnectedSystemObjects.Values
            .FirstOrDefault(c => c.MetaverseObjectId == memberMvoId && c.ConnectedSystemId == TargetSystemId);
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso,
            Attribute = CsMemberAttribute,
            AttributeId = CsMemberAttribute.Id,
            ReferenceValueId = memberCso?.Id,
            UnresolvedReferenceValue = memberDn
        });
        SyncRepo.SeedConnectedSystemObject(groupCso);
        return groupCso;
    }

    #endregion
}
