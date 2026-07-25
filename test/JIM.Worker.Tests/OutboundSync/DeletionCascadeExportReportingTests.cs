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
/// so each staged (or reused) delete Pending Export must produce one Pending Export execution item
/// carrying a PendingExportCreated outcome, exactly like the reference-recall exports beside it.
/// </summary>
[TestFixture]
public class DeletionCascadeExportReportingTests
{
    private const int SourceSystemId = 1;
    private const int TargetSystemId = 5;
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
    /// must report the staged delete Pending Export as its own execution item, carrying the Pending
    /// Export id, the target Connected System Object, its external-ID and object type snapshots, and a
    /// PendingExportCreated outcome naming the target Connected System.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_DeleteExportStaged_ReportsPendingExportRpeiAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Delete);
        var (mvo, targetCso) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        var processor = CreateProcessor(out var activity);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var deletePendingExport = SyncRepo.PendingExports.Values
            .Single(pe => pe.ChangeType == PendingExportChangeType.Delete);
        var pendingExportRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport)
            .ToList();

        Assert.That(pendingExportRpeis, Has.Count.EqualTo(1),
            "Each staged deletion-cascade delete Pending Export must be reported as an execution item");

        var rpei = pendingExportRpeis[0];
        Assert.That(rpei.PendingExportId, Is.EqualTo(deletePendingExport.Id));
        Assert.That(rpei.ConnectedSystemObjectId, Is.EqualTo(targetCso.Id));
        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("Lena Leaver"),
            "The execution item must name the deprovisioned identity, which no longer exists to be looked up");
        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("lena-leaver-guid"),
            "The execution item must snapshot the target object's external ID so it stays identifiable after deletion");
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("user"));

        var outcome = rpei.SyncOutcomes.SingleOrDefault();
        Assert.That(outcome, Is.Not.Null, "Detailed outcome tracking must record a PendingExportCreated outcome");
        Assert.That(outcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
        Assert.That(outcome.TargetEntityId, Is.EqualTo(deletePendingExport.Id));
        Assert.That(outcome.DetailMessage, Is.EqualTo(TargetSystemId.ToString()),
            "The outcome must carry the target Connected System id so the Activity can name the system being deprovisioned");
    }

    /// <summary>
    /// Multiple deleted Metaverse Objects each cascading to their own target object must report one
    /// execution item each; reporting is per staged Pending Export, not per batch.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_MultipleDeletions_ReportsOneRpeiPerDeleteExportAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Delete);
        var (mvoOne, targetCsoOne) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        var (mvoTwo, targetCsoTwo) = SeedDeletionCandidate("Larry Leaver", "uid=larry.leaver,ou=People,dc=corp");
        var processor = CreateProcessor(out var activity);
        processor.QueueMvoDeletion(mvoOne);
        processor.QueueMvoDeletion(mvoTwo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var pendingExportRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport)
            .ToList();
        Assert.That(pendingExportRpeis, Has.Count.EqualTo(2));
        Assert.That(pendingExportRpeis.Select(r => r.ConnectedSystemObjectId),
            Is.EquivalentTo(new[] { (Guid?)targetCsoOne.Id, targetCsoTwo.Id }));
        Assert.That(pendingExportRpeis.Select(r => r.DisplayNameSnapshot),
            Is.EquivalentTo(new[] { "Lena Leaver", "Larry Leaver" }));
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
        var (mvo, targetCso) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        var processor = CreateProcessor(out var activity);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        var pendingExportRpeis = activity.RunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.PendingExport)
            .ToList();
        Assert.That(pendingExportRpeis, Has.Count.EqualTo(1),
            "The per-object fallback must report its delete Pending Export exactly once, not once per attempt");
        Assert.That(pendingExportRpeis[0].ConnectedSystemObjectId, Is.EqualTo(targetCso.Id));
    }

    /// <summary>
    /// A Disconnect deprovisioning action stages no delete Pending Export, so there is nothing to report:
    /// the flush must not manufacture empty Pending Export execution items.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_DisconnectActionOnly_ReportsNoPendingExportRpeisAsync()
    {
        SeedExportSyncRule(OutboundDeprovisionAction.Disconnect);
        var (mvo, _) = SeedDeletionCandidate("Lena Leaver", "uid=lena.leaver,ou=People,dc=corp");
        var processor = CreateProcessor(out var activity);
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        Assert.That(activity.RunProfileExecutionItems.Any(r => r.ObjectChangeType == ObjectChangeType.PendingExport),
            Is.False, "A disconnect-only deprovisioning action stages no delete Pending Export to report");
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

    private DeletionCascadeTestProcessor CreateProcessor(out Activity activity)
    {
        var connectedSystem = new ConnectedSystem { Id = SourceSystemId, Name = "Source HR" };
        var runProfile = new ConnectedSystemRunProfile { Id = 1, Name = "Full Sync", RunType = ConnectedSystemRunType.FullSynchronisation };
        activity = new Activity { Id = Guid.NewGuid() };
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
