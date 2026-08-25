// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Proves that the same-page reconnect safeguard in FlushPendingMvoDeletionsAsync is mode-aware (#119): a
/// Connected System Object rejoining the Metaverse Object within the same page only rescues it from a
/// queued deletion when that rejoin would cancel the scheduled deletion under the trigger mode semantics
/// (ShouldCancelScheduledDeletion). A reconnect that does not undo the triggering disconnection must not
/// rescue the object.
/// </summary>
[TestFixture]
public class MvoDeletionSamePageReconnectTests
{
    private const int SourceSystemId = 1;
    private const int OtherSourceSystemId = 2;
    private const int MvPersonTypeId = 40;
    private const int CsUserTypeId = 70;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        SyncRepo = new SyncRepository();
        Jim = BuildJimApplication(SyncRepo);
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// The rescue path: the reconnecting system is the recorded triggering system, so the reconnect undoes
    /// the disconnection that queued the deletion. The deletion must be skipped and every deletion marker
    /// cleared, including the triggering system fields and policy snapshot.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_TriggeringSystemReconnectsSamePage_SkipsDeletionAndClearsMarkersAsync()
    {
        var mvo = SeedQueuedDeletionCandidate(deletionTriggeredBySystemId: SourceSystemId);
        var processor = new SamePageReconnectTestProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            new ConnectedSystem { Id = SourceSystemId, Name = "Source HR" },
            new ConnectedSystemRunProfile { Id = 1, Name = "Full Sync", RunType = ConnectedSystemRunType.FullSynchronisation },
            new Activity { Id = Guid.NewGuid() },
            new CancellationTokenSource());
        await processor.PrepareRecallExportEvaluationCacheAsync();
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvo.Id), Is.True,
            "The triggering system reconnecting within the page must rescue the MVO from deletion");
        Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null, "The rescue must clear the disconnection date");
        Assert.That(mvo.DeletionInitiatedByType, Is.EqualTo(ActivityInitiatorType.NotSet),
            "The rescue must clear the deletion initiator type");
        Assert.That(mvo.DeletionTriggeredBySystemId, Is.Null, "The rescue must clear the triggering system id");
        Assert.That(mvo.DeletionTriggeredBySystemName, Is.Null, "The rescue must clear the triggering system name");
        Assert.That(mvo.DeletionPolicySnapshotJson, Is.Null, "The rescue must clear the policy snapshot");
    }

    /// <summary>
    /// The retained path (Specific mode): the deletion was triggered by a DIFFERENT listed source, so the
    /// reconnecting system's rejoin does not undo the triggering disconnection and must not rescue the MVO;
    /// the queued deletion proceeds.
    /// </summary>
    [Test]
    public async Task FlushPendingMvoDeletions_SpecificModeDifferentTriggeringSource_ReconnectDoesNotRescueAsync()
    {
        var mvo = SeedQueuedDeletionCandidate(deletionTriggeredBySystemId: OtherSourceSystemId);
        var processor = new SamePageReconnectTestProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            new ConnectedSystem { Id = SourceSystemId, Name = "Source HR" },
            new ConnectedSystemRunProfile { Id = 1, Name = "Full Sync", RunType = ConnectedSystemRunType.FullSynchronisation },
            new Activity { Id = Guid.NewGuid() },
            new CancellationTokenSource());
        await processor.PrepareRecallExportEvaluationCacheAsync();
        processor.QueueMvoDeletion(mvo);

        await processor.CallFlushPendingMvoDeletionsAsync();

        Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvo.Id), Is.False,
            "A same-page reconnect from a system other than the recorded triggering source must not rescue a " +
            "Specific mode deletion; the queued deletion must proceed");
    }

    #region helpers

    /// <summary>
    /// Builds the application over the given in-memory sync repository, mirroring the deletion-cascade test
    /// harness: Service Setting reads resolve to their defaults instead of throwing.
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
    /// Seeds a Metaverse Object queued for immediate deletion under a Specific mode
    /// WhenAuthoritativeSourceDisconnected rule (sources: both systems), with the given recorded triggering
    /// system and a Joined, non-Obsolete Connected System Object from the syncing system attached; the shape
    /// the same-page reconnect check inspects.
    /// </summary>
    private MetaverseObject SeedQueuedDeletionCandidate(int deletionTriggeredBySystemId)
    {
        var mvPersonType = new MetaverseObjectType
        {
            Id = MvPersonTypeId,
            Name = "Person",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            DeletionTriggerConnectedSystemIds = [SourceSystemId, OtherSourceSystemId],
            DeletionGracePeriod = TimeSpan.Zero
        };

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvPersonType,
            Origin = MetaverseObjectOrigin.Projected,
            CachedDisplayName = "Rita Reconnected",
            DeletionInitiatedByType = ActivityInitiatorType.System,
            DeletionInitiatedByName = "System",
            DeletionTriggeredBySystemId = deletionTriggeredBySystemId,
            DeletionTriggeredBySystemName = deletionTriggeredBySystemId == SourceSystemId ? "Source HR" : "Other HR",
            DeletionPolicySnapshotJson = @"{""deletionRule"":""WhenAuthoritativeSourceDisconnected""}"
        };

        // The same-page reconnect: a Joined, non-Obsolete CSO from the syncing system.
        var reconnectedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = SourceSystemId,
            TypeId = CsUserTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo
        };
        mvo.ConnectedSystemObjects.Add(reconnectedCso);

        SyncRepo.SeedMetaverseObject(mvo);
        SyncRepo.SeedConnectedSystemObject(reconnectedCso);
        return mvo;
    }

    /// <summary>
    /// Exposes the protected deletion-flush seam on the concrete processor so the same-page reconnect check
    /// can be driven directly without standing up a full paged synchronisation run.
    /// </summary>
    private sealed class SamePageReconnectTestProcessor : SyncFullSyncTaskProcessor
    {
        public SamePageReconnectTestProcessor(
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

        public void QueueMvoDeletion(MetaverseObject mvo) => _pendingMvoDeletions.Add((mvo, mvo.AttributeValues.ToList()));

        /// <summary>
        /// Builds the run-scoped recall export evaluation cache the deletion flush uses, exactly as both
        /// concrete processors do at run start (source system 0: deletions consider every target system).
        /// </summary>
        public async Task PrepareRecallExportEvaluationCacheAsync() =>
            _recallExportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync();

        public Task CallFlushPendingMvoDeletionsAsync() => FlushPendingMvoDeletionsAsync();
    }

    #endregion
}
