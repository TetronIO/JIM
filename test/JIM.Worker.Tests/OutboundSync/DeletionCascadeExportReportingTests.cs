// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves that the deletion-cascade delete Pending Exports staged when a synchronisation run deletes
/// Metaverse Objects (0-grace-period deletion rules) are reported on the run's Activity (#1044), not
/// only in the service log. Deprovisioning is the most consequential thing a run can stage, and the
/// Synchronisation Integrity rules require every outcome to surface via Run Profile Execution Items,
/// so each staged (or reused) delete Pending Export must appear on the Causality Tree as a consequence
/// of the deletion that caused it: a PendingExportCreated outcome nested beneath the MvoDeleted outcome.
/// </summary>
[TestFixture]
public class DeletionCascadeExportReportingTests
{
    private const int SourceSystemId = 1;
    private const int TargetSystemId = 5;
    private const string TargetSystemName = "Target LDAP";
    private const int MvPersonTypeId = 40;
    private const int CsUserTypeId = 70;
    private const int CsExternalIdAttributeId = 80;
    private const int CsDnAttributeId = 81;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;

    private MetaverseObjectType MvPersonType { get; set; } = null!;
    private ConnectedSystemObjectType CsUserType { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsExternalIdAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsDnAttribute { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        SyncRepo = new SyncRepository();
        Jim = BuildJimApplication(SyncRepo);

        MvPersonType = new MetaverseObjectType { Id = MvPersonTypeId, Name = "Person" };
        CsUserType = new ConnectedSystemObjectType { Id = CsUserTypeId, Name = "user" };
        CsExternalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsExternalIdAttributeId,
            Name = "objectGUID",
            Type = AttributeDataType.Text,
            IsExternalId = true,
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
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// The set-based fast path: a Metaverse Object deleted inline by the run, whose target Connected
    /// System Object is matched by an export Synchronisation Rule with a Delete deprovisioning action,
    /// must record the staged delete Pending Export as a consequence of the deletion: a
    /// PendingExportCreated outcome nested beneath the MvoDeleted outcome on the disconnecting object's
    /// execution item, naming the Connected System the account is being deleted from.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_DeleteExportStaged_NestsPendingExportUnderMvoDeletedAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Delete);
        var (mvo, _) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        Activity activity = null!;
        var processor = await CreateProcessorAsync(a => activity = a);
        var (deletionRpei, mvoDeletedOutcome) = processor.RecordSourceDisconnection(mvo);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var deletePendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ChangeType == PendingExportChangeType.Delete);

        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ObjectChangeType == ObjectChangeType.PendingExport),
            Is.False, "The staged export belongs on the deletion's causality tree, not on an execution item of its own");

        var cascadeOutcome = mvoDeletedOutcome.Children.SingleOrDefault();
        Assert.That(cascadeOutcome, Is.Not.Null,
            "The staged delete Pending Export must be recorded as a consequence of the Metaverse Object deletion");
        Assert.That(cascadeOutcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        Assert.That(cascadeOutcome.ParentSyncOutcome, Is.SameAs(mvoDeletedOutcome));
        Assert.That(cascadeOutcome.TargetEntityId, Is.EqualTo(deletePendingExport.Id));
        Assert.That(cascadeOutcome.TargetEntityDescription, Is.EqualTo(TargetSystemName),
            "The outcome must name the Connected System the account is being deleted from; the identity is named by the parent node");
        Assert.That(cascadeOutcome.DetailMessage, Is.EqualTo(TargetSystemId.ToString()),
            "The outcome must carry the target Connected System id so the Activity can name the system being deprovisioned");
        Assert.That(deletionRpei.SyncOutcomes, Does.Contain(cascadeOutcome),
            "The outcome must be attached to the disconnecting object's execution item so it persists with it");
    }

    /// <summary>
    /// Each deleted Metaverse Object's cascade lands on its own deletion, not pooled onto the first:
    /// the graph must attribute every downstream deprovisioning to the identity that caused it.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_MultipleDeletions_NestsEachExportUnderItsOwnDeletionAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Delete);
        var (mvoOne, targetCsoOne) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        var (mvoTwo, targetCsoTwo) = SeedDeletionCandidate("Larry Leaver", "uid=larry.leaver,ou=People,dc=corp");
        var processor = await CreateProcessorAsync();
        var (_, mvoOneDeletedOutcome) = processor.RecordSourceDisconnection(mvoOne);
        var (_, mvoTwoDeletedOutcome) = processor.RecordSourceDisconnection(mvoTwo);
        processor.QueueMvoDeletion(mvoOne);
        processor.QueueMvoDeletion(mvoTwo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var pendingExportsByCsoId = SyncRepo.PendingExports.Values
            .Where(pe => pe.ChangeType == PendingExportChangeType.Delete)
            .ToDictionary(pe => pe.ConnectedSystemObjectId!.Value, pe => pe.Id);

        Assert.That(mvoOneDeletedOutcome.Children.Select(c => c.TargetEntityId),
            Is.EquivalentTo(new[] { (Guid?)pendingExportsByCsoId[targetCsoOne.Id] }));
        Assert.That(mvoTwoDeletedOutcome.Children.Select(c => c.TargetEntityId),
            Is.EquivalentTo(new[] { (Guid?)pendingExportsByCsoId[targetCsoTwo.Id] }));
    }

    /// <summary>
    /// Error-isolation parity: when the set-based deletion fails and the flush falls back to per-object
    /// deletion, the delete Pending Exports that path stages must still be reported, and reported once
    /// (the bulk attempt's evaluation is re-run by the fallback, which reuses the same Pending Exports).
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_BulkDeleteFails_ReportsFallbackDeleteExportsExactlyOnceAsync()
    {
        SyncRepo = new BulkDeleteFailingSyncRepository();
        Jim.Dispose();
        Jim = BuildJimApplication(SyncRepo);

        SeedExportSyncRule(OutboundDeprovisionAction.Delete);
        var (mvo, _) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        var processor = await CreateProcessorAsync();
        var (_, mvoDeletedOutcome) = processor.RecordSourceDisconnection(mvo);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var deletePendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ChangeType == PendingExportChangeType.Delete);
        Assert.That(mvoDeletedOutcome.Children, Has.Count.EqualTo(1),
            "The per-object fallback must report its delete Pending Export exactly once, not once per attempt");
        Assert.That(mvoDeletedOutcome.Children[0].TargetEntityId, Is.EqualTo(deletePendingExport.Id));
    }

    /// <summary>
    /// Synchronisation Integrity backstop: with no MvoDeleted outcome to hang the export off (outcome
    /// tracking disabled, or a deletion path that recorded none), the staged deprovisioning must still be
    /// reported as a standalone execution item rather than going unreported.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_NoDeletionOutcomeToNestUnder_ReportsStandaloneItemAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Delete);
        var (mvo, targetCso) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        Activity activity = null!;
        var processor = await CreateProcessorAsync(a => activity = a);
        processor.SetOutcomeTracking(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var deletePendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ChangeType == PendingExportChangeType.Delete);
        var pendingExportRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport)
            .ToList();

        Assert.That(pendingExportRpeis, Has.Count.EqualTo(1),
            "A staged deprovisioning export must never go unreported, even with nothing to nest it under");
        Assert.That(pendingExportRpeis[0].PendingExportId, Is.EqualTo(deletePendingExport.Id));
        Assert.That(pendingExportRpeis[0].ConnectedSystemObjectId, Is.EqualTo(targetCso.Id));
        Assert.That(pendingExportRpeis[0].DisplayNameSnapshot, Is.EqualTo("Lena Leaver"));
        Assert.That(pendingExportRpeis[0].ExternalIdSnapshot, Is.EqualTo("lena-leaver-guid"),
            "The standalone item must snapshot the target object's external ID so it stays identifiable after deletion");
        Assert.That(pendingExportRpeis[0].ObjectTypeSnapshot, Is.EqualTo("user"));
    }

    /// <summary>
    /// A Disconnect deprovisioning action stages no delete Pending Export, so there is nothing to report:
    /// the flush must not manufacture empty outcomes or execution items.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_DisconnectActionOnly_ReportsNothingAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Disconnect);
        var (mvo, _) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        Activity activity = null!;
        var processor = await CreateProcessorAsync(a => activity = a);
        var (_, mvoDeletedOutcome) = processor.RecordSourceDisconnection(mvo);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        Assert.That(mvoDeletedOutcome.Children, Is.Empty,
            "A disconnect-only deprovisioning action stages no delete Pending Export to report");
        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ObjectChangeType == ObjectChangeType.PendingExport),
            Is.False);
    }

    #region helpers

    /// <summary>
    /// Builds the application over the given in-memory sync repository. The general repository is mocked at
    /// its interface rather than over a mock DbContext so Service Setting reads resolve to their defaults
    /// (outcome tracking: Detailed, change tracking: enabled) instead of throwing, which would divert the
    /// deletion flush into its per-object error-isolation fallback and mask what these tests assert.
    /// </summary>
    private static JimApplication BuildJimApplication(SyncRepository syncRepository)
    {
        var mockServiceSettingsRepository = new Mock<IServiceSettingsRepository>();
        mockServiceSettingsRepository
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((ServiceSetting?)null);

        var mockRepository = new Mock<IRepository>();
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepository.Object);

        return new JimApplication(mockRepository.Object, syncRepository: syncRepository);
    }

    /// <summary>
    /// Seeds the enabled export Synchronisation Rule that matches the deletion candidates' target
    /// Connected System Objects, with the given deprovisioning action (issue #655).
    /// </summary>
    private void SeedExportSyncRule(OutboundDeprovisionAction outboundDeprovisionAction)
    {
        SyncRepo.SeedSyncRule(new SyncRule
        {
            Id = 900,
            Name = "Target Export Users",
            Enabled = true,
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = TargetSystemId,
            ConnectedSystem = new ConnectedSystem { Id = TargetSystemId, Name = TargetSystemName },
            ConnectedSystemObjectTypeId = CsUserTypeId,
            MetaverseObjectTypeId = MvPersonTypeId,
            OutboundDeprovisionAction = outboundDeprovisionAction
        });
    }

    /// <summary>
    /// Seeds a Metaverse Object about to be deleted inline by the run, joined to a provisioned target
    /// Connected System Object carrying an external ID and a Distinguished Name.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystemObject TargetCso) SeedDeletionCandidate(string displayName, string dn)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = MvPersonType,
            Origin = MetaverseObjectOrigin.Projected,
            CachedDisplayName = displayName
        };

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsUserTypeId,
            Type = CsUserType,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            ExternalIdAttributeId = CsExternalIdAttributeId,
            SecondaryExternalIdAttributeId = CsDnAttributeId
        };
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = CsExternalIdAttribute,
            AttributeId = CsExternalIdAttributeId,
            StringValue = $"{displayName.ToLowerInvariant().Replace(' ', '-')}-guid"
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = CsDnAttribute,
            AttributeId = CsDnAttributeId,
            StringValue = dn
        });
        mvo.ConnectedSystemObjects.Add(targetCso);

        SyncRepo.SeedMetaverseObject(mvo);
        SyncRepo.SeedConnectedSystemObject(targetCso);
        return (mvo, targetCso);
    }

    private async Task<DeletionCascadeTestProcessor> CreateProcessorAsync(Action<Activity>? captureActivity = null)
    {
        var connectedSystem = new ConnectedSystem { Id = SourceSystemId, Name = "Source HR" };
        var runProfile = new ConnectedSystemRunProfile { Id = 1, Name = "Full Sync", RunType = ConnectedSystemRunType.FullSynchronisation };
        var activity = new Activity { Id = Guid.NewGuid() };
        captureActivity?.Invoke(activity);
        var processor = new DeletionCascadeTestProcessor(
            new SyncEngine(),
            new SyncServer(Jim),
            SyncRepo,
            connectedSystem,
            runProfile,
            activity,
            new CancellationTokenSource());
        processor.SetOutcomeTracking(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        processor.SetCsoChangeTrackingEnabled(false);
        await processor.PrepareRecallExportEvaluationCacheAsync();
        return processor;
    }

    /// <summary>
    /// Exposes the protected deletion-flush seam on the concrete processor so the cascade reporting can
    /// be driven directly without standing up a full paged synchronisation run.
    /// </summary>
    private sealed class DeletionCascadeTestProcessor : SyncFullSyncTaskProcessor
    {
        public DeletionCascadeTestProcessor(
            ISyncEngine syncEngine,
            ISyncServer syncServer,
            ISyncRepository syncRepository,
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile connectedSystemRunProfile,
            Activity activity,
            CancellationTokenSource cancellationTokenSource)
            : base(syncEngine, syncServer, syncRepository, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource)
        {
        }

        public void SetOutcomeTracking(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel level) => _syncOutcomeTrackingLevel = level;

        public void SetCsoChangeTrackingEnabled(bool enabled) => _csoChangeTrackingEnabled = enabled;

        public void QueueMvoDeletion(MetaverseObject mvo) => _pendingMvoDeletions.Add((mvo, mvo.AttributeValues.ToList()));

        /// <summary>
        /// Builds the run-scoped recall export evaluation cache the deletion flush uses, exactly as both
        /// concrete processors do at run start (source system 0: deletions consider every target system).
        /// </summary>
        public async Task PrepareRecallExportEvaluationCacheAsync() =>
            _recallExportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync(sourceConnectedSystemId: 0);

        /// <summary>
        /// Records what Pass 1 or Pass 2 records when a disconnection triggers an immediate deletion: an
        /// execution item for the disconnecting Connected System Object carrying a Disconnected root outcome
        /// with an MvoDeleted child, which the deletion flush then hangs its cascade exports off.
        /// </summary>
        public (ActivityRunProfileExecutionItem Rpei, ActivityRunProfileExecutionItemSyncOutcome MvoDeletedOutcome) RecordSourceDisconnection(MetaverseObject mvo)
        {
            var rpei = new ActivityRunProfileExecutionItem
            {
                Id = Guid.NewGuid(),
                ObjectChangeType = ObjectChangeType.Disconnected,
                DisplayNameSnapshot = mvo.DisplayName
            };
            var disconnectedOutcome = SyncOutcomeBuilder.AddRootOutcome(rpei,
                ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected,
                targetEntityId: mvo.Id,
                targetEntityDescription: mvo.DisplayName);
            var mvoDeletedOutcome = SyncOutcomeBuilder.AddChildOutcome(rpei, disconnectedOutcome,
                ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
                targetEntityId: mvo.Id,
                targetEntityDescription: mvo.DisplayName);
            _activity.RunProfileExecutionItems.Add(rpei);
            return (rpei, mvoDeletedOutcome);
        }

        public Task CallFlushPendingMvoDeletionsAsync() => FlushPendingMvoDeletionsAsync();
    }

    /// <summary>
    /// Forces the set-based Metaverse Object deletion to fail so the flush takes its per-object
    /// error-isolation fallback.
    /// </summary>
    private sealed class BulkDeleteFailingSyncRepository : SyncRepository
    {
        public override Task DeleteMetaverseObjectsAsync(IReadOnlyCollection<MetaverseObject> metaverseObjects)
            => throw new InvalidOperationException("Simulated bulk deletion failure");
    }

    #endregion
}
