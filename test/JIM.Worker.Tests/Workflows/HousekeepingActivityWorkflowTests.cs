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
    private Mock<IJimApplicationFactory> _mockJimFactory = null!;
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
            .Callback<Activity>(activity =>
            {
                // Persisting assigns an Id, mirroring the database, so anything recorded against the Activity can be
                // checked against it.
                if (activity.Id == Guid.Empty)
                    activity.Id = Guid.NewGuid();
                _createdActivities.Add(activity);
            })
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
                return new ActivityRpeiErrorCounts(
                    rpeis.Count(r => r.ErrorType.HasValue && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet),
                    rpeis.Count,
                    rpeis.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
                    rpeis.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged));
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

        // Housekeeping runs each batch on a JimApplication it obtains from the factory (never the main loop's),
        // so the factory hands out the fixture's instance unless a test says otherwise.
        _mockJimFactory = new Mock<IJimApplicationFactory>();
        _mockJimFactory.Setup(f => f.Create()).Returns(() => Jim);

        WorkerInstance = new Worker(
            _mockJimFactory.Object,
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
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert: the batch is recorded as a system-initiated Scheduled Metaverse Object Deletion Activity. The
        // enum member keeps its original name (persisted by ordinal, append-only); only the display name changed.
        var activity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        Assert.That(activity, Is.Not.Null,
            "A housekeeping batch that deletes Metaverse Objects must record a Scheduled Metaverse Object Deletion Activity");
        Assert.That(activity!.TargetName, Is.EqualTo(Constants.ActivityTargetNames.ScheduledMetaverseObjectDeletion));
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
    /// The Deleted change record housekeeping writes must name the housekeeping Activity, so a reader of the object's
    /// history (or the Object Changes page) can open the batch that deleted it (#2460).
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvo_DeletedChangeRecordNamesTheHousekeepingActivityAsync()
    {
        // Arrange
        var (memberMvo, _) = SeedEligibleMemberWithTargetCso("Lena Leaver");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([memberMvo]);
        var recordedChanges = new List<MetaverseObjectChange>();
        _mockMetaverseRepository
            .Setup(r => r.CreateMetaverseObjectChangeDirectAsync(It.IsAny<MetaverseObjectChange>()))
            .Callback<MetaverseObjectChange>(recordedChanges.Add)
            .Returns(Task.CompletedTask);

        // Act
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert
        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var deletedChange = recordedChanges.Single(c => c.ChangeType == ObjectChangeType.Deleted);
        var deletionItem = _persistedRpeis.Single(r => r.ActivityId == activity.Id && r.ObjectChangeType == ObjectChangeType.Deleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deletedChange.ActivityId, Is.EqualTo(activity.Id));
            Assert.That(deletedChange.DeletedByRunProfileExecutionItemId, Is.EqualTo(deletionItem.Id),
                "the deletion record names the housekeeping item that deleted the object");
        }
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

        await WorkerInstance.PerformHousekeepingAsync();

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

        await WorkerInstance.PerformHousekeepingAsync();

        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var recallRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id
            && r.ObjectChangeType == ObjectChangeType.PendingExport
            && r.ConnectedSystemObjectId == groupTargetCso.Id);
        var recallPendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);

        Assert.That(recallPendingExport.QueuedByRunProfileExecutionItemId, Is.EqualTo(recallRpei.Id));
    }

    /// <summary>
    /// The recall item's own PendingExportCreated outcome must record the staged kind (#1561 follow-up),
    /// exactly as every other Pending Export outcome does now: a membership removal is an attribute
    /// update to the referencing group's record, never a create or a delete.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoReferencedByGroup_RecallOutcomeRecordsStagedUpdateAsync()
    {
        var (memberMvo, memberDn) = SeedEligibleMemberWithTargetCso("Lena Leaver");
        var groupMvo = SeedGroupMvoReferencing("Team Alpha", memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberMvo.Id, memberDn);
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([memberMvo]);

        await WorkerInstance.PerformHousekeepingAsync();

        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var recallRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id
            && r.ObjectChangeType == ObjectChangeType.PendingExport
            && r.ConnectedSystemObjectId == groupTargetCso.Id);
        var recallOutcome = recallRpei.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        Assert.That(recallOutcome.StagedChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "A membership removal is an attribute update to the referencing group's own record");
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
        await WorkerInstance.PerformHousekeepingAsync();

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
        Assert.That(cascadeOutcome.StagedChangeType, Is.EqualTo(PendingExportChangeType.Delete),
            "Every Pending Export outcome records its staged kind (#1561 follow-up), DeprovisionQueued included");
        Assert.That(cascadeOutcome.TargetEntityId, Is.EqualTo(deletePendingExport.Id));
        Assert.That(cascadeOutcome.TargetEntityDescription, Is.EqualTo(TargetSystemName),
            "The outcome must name the Connected System the account is being deleted from; the identity is named by the item it hangs off");
        Assert.That(activity.TotalPendingExports, Is.EqualTo(1),
            "TotalPendingExports must count the staged deprovisioning export");
    }

    /// <summary>
    /// The queued deprovision names the export Synchronisation Rule whose Deprovisioning Action said Delete, so the
    /// housekeeping Activity says why the account is being removed rather than left in place (#1811).
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoWithDeleteDeprovisionRule_NamesTheRuleThatQueuedTheDeleteAsync()
    {
        // Arrange
        var (personMvo, _) = SeedEligiblePersonWithDeprovisionableTargetCso("Dan Deprovisioned");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([personMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert
        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var deletionRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id && r.ObjectChangeType == ObjectChangeType.Deleted);
        var deprovisionOutcome = deletionRpei.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deprovisionOutcome.SyncRuleId, Is.EqualTo(910));
            Assert.That(deprovisionOutcome.SyncRuleName, Is.EqualTo("Target Export Users"));
        }
    }

    /// <summary>
    /// A grace-period deletion under a Disconnect Deprovisioning Action leaves the account in place and queues
    /// nothing. It used to record nothing about the account either, so it read as untouched when it was in fact now
    /// unmanaged; it is reported as a Target Disconnected outcome beneath the deletion, naming the rule (#1811).
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoWithDisconnectDeprovisionRule_ReportsTheAccountLeftInPlaceAsync()
    {
        // Arrange
        SeedTargetExportUsersRule(SyncRepo, OutboundDeprovisionAction.Disconnect);
        var personMvo = CreateEligiblePersonMvo("Dan Disconnected");
        SyncRepo.SeedMetaverseObject(personMvo);
        var personTargetCso = SeedProvisionedTargetCso(SyncRepo, personMvo, "uid=dan.disconnected,ou=People,dc=glitterband,dc=local");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([personMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert
        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var deletionRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id && r.ObjectChangeType == ObjectChangeType.Deleted);
        var mvoDeletedOutcome = deletionRpei.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        var disconnectedOutcome = mvoDeletedOutcome.Children.SingleOrDefault();
        Assert.That(disconnectedOutcome, Is.Not.Null, "The account left in place must be reported, not silently disconnected");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(disconnectedOutcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected));
            Assert.That(disconnectedOutcome.TargetEntityId, Is.EqualTo(personTargetCso.Id));
            Assert.That(disconnectedOutcome.TargetEntityDescription, Is.EqualTo(TargetSystemName));
            Assert.That(disconnectedOutcome.SyncRuleId, Is.EqualTo(910));
            Assert.That(disconnectedOutcome.SyncRuleName, Is.EqualTo("Target Export Users"));
            Assert.That(activity.TotalPendingExports, Is.Zero, "Nothing was queued, so nothing is counted as a Pending Export");
        }
    }

    /// <summary>
    /// Housekeeping must evaluate every batch against the configuration as it stands now, not as the worker first
    /// saw it. The main loop's JimApplication lives for the worker's lifetime on a change-tracking DbContext, and
    /// EF Core serves a tracked entity back from its identity map rather than refreshing it from a later query, so
    /// a Synchronisation Rule loaded for one batch was being reused, as it then stood, by every batch after it: an
    /// export rule switched to Disconnect after the first batch still had its directory objects deleted (found by
    /// Scenario 004, Test 9). Modelled as two repositories: the main loop's still says Delete; the one the factory
    /// hands out, standing in for the database as it now is, says Disconnect. The current action must win.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_RuleActionChangedSinceWorkerStarted_HonoursTheCurrentActionAsync()
    {
        // Arrange: the main loop's instance (the fixture's Jim) still holds the rule as Delete.
        var (personMvo, personTargetCso) = SeedEligiblePersonWithDeprovisionableTargetCso("Dan Disconnected");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([personMvo]);

        // The database as it now stands: the same object graph, the rule since switched to Disconnect.
        var currentSyncRepo = new SyncRepository();
        using var currentJim = new JimApplication(_mockRepository.Object, syncRepository: currentSyncRepo);
        SeedTargetExportUsersRule(currentSyncRepo, OutboundDeprovisionAction.Disconnect);
        currentSyncRepo.SeedMetaverseObject(personMvo);
        var currentTargetCso = SeedProvisionedTargetCso(currentSyncRepo, personMvo, "uid=dan.disconnected,ou=People,dc=glitterband,dc=local", personTargetCso.Id);
        _mockJimFactory.Setup(f => f.Create()).Returns(currentJim);

        // Act
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert: the Metaverse Object was deleted and its target object disconnected, and nothing was staged to
        // delete it from the directory, on either view of the data.
        Assert.That(_deletedMvoIds, Is.EqualTo(new[] { personMvo.Id }));
        Assert.That(currentTargetCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined),
            "A Disconnect action breaks the join and leaves the directory object in place");
        Assert.That(currentSyncRepo.PendingExports.Values.Any(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.False,
            "The rule now says Disconnect, so no delete Pending Export may be staged");
        Assert.That(SyncRepo.PendingExports.Values.Any(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.False,
            "The main loop's stale view of the rule must play no part in the batch");
    }

    /// <summary>
    /// The mechanism behind the test above: a housekeeping tick obtains its JimApplication from the factory and
    /// releases it when the batch is done, so no entity read for one batch can be served, stale, to the next.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_Batch_RunsOnAnInstanceFromTheFactoryAndReleasesItAsync()
    {
        // Arrange
        var (personMvo, _) = SeedEligiblePersonWithDeprovisionableTargetCso("Dan Deprovisioned");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([personMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert
        _mockJimFactory.Verify(f => f.Create(), Times.Once,
            "Each housekeeping tick evaluates on a JimApplication of its own");
        _mockRepository.Verify(r => r.Dispose(), Times.Once,
            "The tick's JimApplication, and with it its DbContext, is released once the batch is done");
    }

    /// <summary>
    /// Deletion cascade: a grace-period-expired Metaverse Object whose target Connected
    /// System Object is still Pending Provisioning with no Pending Export at all (nothing was ever exported)
    /// has its provisioning cancelled outright rather than deprovisioned. The cancellation is a consequence of
    /// the deletion, so it must be recorded on the housekeeping Activity as a ProvisioningCancelled outcome
    /// nested beneath the deleted object's MvoDeleted outcome, exactly as a genuinely staged delete export is.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_EligibleMvoWithNeverExportedProvisioningTargetCso_NestsProvisioningCancelledUnderMvoDeletedAsync()
    {
        // Arrange
        var (personMvo, targetCso) = SeedEligiblePersonWithNeverExportedProvisioningTargetCso("Nadia NeverExported");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([personMvo]);

        // Act
        await WorkerInstance.PerformHousekeepingAsync();

        // Assert: nothing exists in the target system, so nothing was staged and the never-provisioned CSO
        // (and its unsent Create) are both gone.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.PendingExports.Values, Is.Empty, "Nothing exists in the target system, so nothing may be exported");
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(targetCso.Id), Is.False,
                "The never-provisioned CSO must be removed, not left stranded in the connector space");
        }

        // Assert: the cancellation is still recorded as a consequence of the deletion on the deleted object's item.
        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var rpeis = _persistedRpeis.Where(r => r.ActivityId == activity.Id).ToList();
        var deletionRpei = rpeis.Single(r => r.ObjectChangeType == ObjectChangeType.Deleted);
        var mvoDeletedOutcome = deletionRpei.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        var cascadeOutcome = mvoDeletedOutcome.Children.SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cascadeOutcome, Is.Not.Null,
                "The cancelled provisioning must be recorded as a consequence of the Metaverse Object deletion");
            Assert.That(cascadeOutcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled),
                "Nothing was ever exported, so this is a cancellation, not a queued deprovision");
            Assert.That(cascadeOutcome.TargetEntityDescription, Is.EqualTo(TargetSystemName),
                "The outcome must name the Connected System the cancelled provisioning targeted");
            Assert.That(activity.TotalPendingExports, Is.EqualTo(0),
                "A cancellation is not a Pending Export and must not count towards the total");
        }
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
        await WorkerInstance.PerformHousekeepingAsync();

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
    /// The grace-period link (#1811): housekeeping deletes the object in its own Activity, days or weeks after the
    /// run that scheduled the deletion, and the object carries the item that scheduled it. The deletion must record
    /// that item as its cause, or the deletion cannot say which run decided on it and the run cannot say what its
    /// decision led to. Recorded whatever the tracking level, because the deletion item exists either way.
    /// </summary>
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)]
    public async Task PerformHousekeeping_MvoCarryingItsSchedulingItem_RecordsTheSchedulingItemAsTheDeletionsCauseAsync(
        ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel trackingLevel)
    {
        UseSyncOutcomeTrackingLevel(trackingLevel);
        var schedulingItemId = Guid.NewGuid();
        var scheduledMvo = CreateEligiblePersonMvo("Sana Scheduled");
        scheduledMvo.DeletionScheduledByRunProfileExecutionItemId = schedulingItemId;
        scheduledMvo.DeletionTriggeredBySystemId = 1;
        scheduledMvo.DeletionTriggeredBySystemName = "Source HR";
        scheduledMvo.DeletionPolicySnapshotJson = new MvoDeletionPolicySnapshot
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected,
            TriggeringSystemId = 9,
            TriggeringSystemName = "Yellowstone APAC"
        }.ToJson();
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([scheduledMvo]);

        await WorkerInstance.PerformHousekeepingAsync();

        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var deletionRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id && r.ObjectChangeType == ObjectChangeType.Deleted);
        var edge = deletionRpei.CausalEdges.SingleOrDefault(e => e.EdgeType == CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion);
        Assert.That(edge, Is.Not.Null, "the deletion names the run that scheduled it");
        var mvoDeleted = deletionRpei.SyncOutcomes.SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(edge!.CauseRunProfileExecutionItemId, Is.EqualTo(schedulingItemId));
            Assert.That(edge!.CauseMetaverseObjectId, Is.EqualTo(scheduledMvo.Id));
            Assert.That(edge!.CauseDisplayName, Is.EqualTo("Sana Scheduled"));
            Assert.That(edge!.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected),
                "the reason comes from the decision-time snapshot, as on the deletion's other edges");
            Assert.That(edge!.ConnectedSystemId, Is.EqualTo(9), "the snapshot's triggering system wins over the object's own marker");
            Assert.That(edge!.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(edge!.EffectSyncOutcome, Is.SameAs(mvoDeleted),
                "the edge explains the MvoDeleted outcome where one is recorded, and the item as a whole where none is");
        }
    }

    /// <summary>
    /// An object scheduled before the link was recorded carries no scheduling item. Its deletion goes ahead exactly
    /// as before and records no edge: an edge pointing at nothing would read as a cause that has aged out of history.
    /// </summary>
    [Test]
    public async Task PerformHousekeeping_MvoWithoutASchedulingItem_RecordsNoSchedulingEdgeAsync()
    {
        var scheduledMvo = CreateEligiblePersonMvo("Sana Scheduled");
        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsEligibleForDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync([scheduledMvo]);

        await WorkerInstance.PerformHousekeepingAsync();

        var activity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.MetaverseObjectHousekeeping);
        var deletionRpei = _persistedRpeis.Single(r => r.ActivityId == activity.Id && r.ObjectChangeType == ObjectChangeType.Deleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_deletedMvoIds, Does.Contain(scheduledMvo.Id));
            Assert.That(deletionRpei.CausalEdges.Where(e => e.EdgeType == CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion), Is.Empty);
        }
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
        await WorkerInstance.PerformHousekeepingAsync();

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
        await WorkerInstance.PerformHousekeepingAsync();

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
    /// Sets the sync outcome tracking level every Service Setting read in the batch sees.
    /// </summary>
    private void UseSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel level)
    {
        _mockServiceSettingsRepository
            .Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel,
                DisplayName = "Sync outcome tracking level",
                Category = ServiceSettingCategory.History,
                ValueType = ServiceSettingValueType.Enum,
                DefaultValue = level.ToString()
            });
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
        SeedTargetExportUsersRule(SyncRepo, OutboundDeprovisionAction.Delete);

        var personMvo = CreateEligiblePersonMvo(displayName);
        SyncRepo.SeedMetaverseObject(personMvo);

        var targetCso = SeedProvisionedTargetCso(SyncRepo, personMvo, "uid=dan.deprovisioned,ou=People,dc=glitterband,dc=local");
        return (personMvo, targetCso);
    }

    /// <summary>
    /// Seeds the export Synchronisation Rule matching Person Metaverse Objects to the target's user type, with
    /// the given deprovisioning action, into the given repository.
    /// </summary>
    private static void SeedTargetExportUsersRule(SyncRepository into, OutboundDeprovisionAction deprovisionAction)
    {
        into.SeedSyncRule(new SyncRule
        {
            Id = 910,
            Name = "Target Export Users",
            Enabled = true,
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = TargetSystemId,
            ConnectedSystem = new ConnectedSystem { Id = TargetSystemId, Name = TargetSystemName },
            ConnectedSystemObjectTypeId = CsUserTypeId,
            MetaverseObjectTypeId = MvPersonTypeId,
            OutboundDeprovisionAction = deprovisionAction
        });
    }

    /// <summary>
    /// Seeds a provisioned target Connected System Object joined to the given Metaverse Object, carrying the given
    /// DN as its secondary external ID, into the given repository.
    /// </summary>
    private ConnectedSystemObject SeedProvisionedTargetCso(SyncRepository into, MetaverseObject mvo, string dn, Guid? csoId = null)
    {
        var targetCso = new ConnectedSystemObject
        {
            Id = csoId ?? Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsUserTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = mvo.Id,
            SecondaryExternalIdAttributeId = CsDnAttributeId
        };
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = CsDnAttribute,
            AttributeId = CsDnAttributeId,
            StringValue = dn
        });
        into.SeedConnectedSystemObject(targetCso);
        return targetCso;
    }

    /// <summary>
    /// Seeds an eligible Person Metaverse Object joined to a target Connected System Object that is still
    /// Pending Provisioning and still carries its unsent Create Pending Export (the only proof that nothing was
    /// ever exported), so the deletion cascades into a cancellation rather than a delete Pending Export.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystemObject TargetCso) SeedEligiblePersonWithNeverExportedProvisioningTargetCso(string displayName)
    {
        SyncRepo.SeedSyncRule(new SyncRule
        {
            Id = 911,
            Name = "Target Export Users (Never Exported)",
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
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = personMvo.Id
        };
        SyncRepo.SeedConnectedSystemObject(targetCso);
        SyncRepo.SeedPendingExport(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = personMvo.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

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
