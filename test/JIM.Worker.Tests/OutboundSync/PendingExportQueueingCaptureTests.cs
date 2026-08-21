// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves the capture half of the queueing-to-executing causal seam (#1223): the synchronisation that stages a
/// Pending Export names itself on it, so the export run that carries it out days later can say what caused it.
///
/// Nothing else can supply this. The export run holds only a queue of changes; the decision that put one in the
/// queue was taken in a different Activity, and the Pending Export row carrying the link is deleted the moment
/// the export succeeds. Captured here or not at all.
/// </summary>
[TestFixture]
public class PendingExportQueueingCaptureTests
{
    private const int TargetSystemId = 5;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        SyncRepo = new SyncRepository();
        var mockJimDbContext = new Mock<JimDbContext>();
        Jim = new JimApplication(new PostgresDataRepository(mockJimDbContext.Object), syncRepository: SyncRepo);
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// The base case: an export staged while synchronising a Metaverse Object carries that object's execution
    /// item, which is the answer to "why did this export happen".
    /// </summary>
    [Test]
    public void StampQueueingItemOnPendingExports_ItemForTheObject_NamesItOnEveryExport()
    {
        var processor = CreateProcessor();
        var mvoId = Guid.NewGuid();
        var rpei = processor.RegisterRpeiFor(mvoId);
        var exports = new List<PendingExport> { Export(), Export() };

        processor.CallStampQueueingItemOnPendingExports(mvoId, exports);

        Assert.That(exports.Select(e => e.QueuedByRunProfileExecutionItemId),
            Is.All.EqualTo(rpei.Id));
    }

    /// <summary>
    /// A Run Profile Execution Item is assigned its id as it is persisted, which happens after the Pending
    /// Exports of the same page are written. Reading the id as it stands would therefore store an empty Guid on
    /// every export, so the capture assigns the id early rather than waiting for the flush to do it.
    /// </summary>
    [Test]
    public void StampQueueingItemOnPendingExports_ItemNotYetPersisted_AssignsItsIdRatherThanStoringAnEmptyOne()
    {
        var processor = CreateProcessor();
        var mvoId = Guid.NewGuid();
        var rpei = processor.RegisterRpeiFor(mvoId, assignId: false);
        var exports = new List<PendingExport> { Export() };

        processor.CallStampQueueingItemOnPendingExports(mvoId, exports);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.Id, Is.Not.EqualTo(Guid.Empty), "the item must be given its id here, since the flush runs too late for this");
            Assert.That(exports[0].QueuedByRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        }
    }

    /// <summary>
    /// An id already assigned is the item's identity and must not be replaced, or the export would point at an
    /// item that never existed.
    /// </summary>
    [Test]
    public void StampQueueingItemOnPendingExports_ItemAlreadyHasAnId_KeepsIt()
    {
        var processor = CreateProcessor();
        var mvoId = Guid.NewGuid();
        var rpei = processor.RegisterRpeiFor(mvoId);
        var originalId = rpei.Id;
        var exports = new List<PendingExport> { Export() };

        processor.CallStampQueueingItemOnPendingExports(mvoId, exports);

        Assert.That(rpei.Id, Is.EqualTo(originalId));
    }

    /// <summary>
    /// Some staging paths run with no execution item for the object (a run recording nothing per object). The
    /// export is still staged; it simply carries no queueing item, and the causality chain later ends at the
    /// Metaverse Object instead of walking on.
    /// </summary>
    [Test]
    public void StampQueueingItemOnPendingExports_NoItemForTheObject_LeavesTheExportsUnstamped()
    {
        var processor = CreateProcessor();
        var exports = new List<PendingExport> { Export() };

        processor.CallStampQueueingItemOnPendingExports(Guid.NewGuid(), exports);

        Assert.That(exports[0].QueuedByRunProfileExecutionItemId, Is.Null);
    }

    private static PendingExport Export() => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = TargetSystemId,
        ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Pending
    };

    private QueueingCaptureTestProcessor CreateProcessor()
    {
        var connectedSystem = new ConnectedSystem { Id = TargetSystemId, Name = "Glitterband EMEA" };
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 1,
            Name = "Full Sync",
            RunType = ConnectedSystemRunType.FullSynchronisation
        };
        return new QueueingCaptureTestProcessor(
            new SyncEngine(),
            new JIM.Application.Servers.SyncServer(Jim),
            SyncRepo,
            connectedSystem,
            runProfile,
            new Activity { Id = Guid.NewGuid() },
            new CancellationTokenSource());
    }

    /// <summary>
    /// Exposes the capture seam and the per-object execution item map, so the capture can be driven without
    /// standing up a full paged synchronisation run.
    /// </summary>
    private sealed class QueueingCaptureTestProcessor : SyncFullSyncTaskProcessor
    {
        public QueueingCaptureTestProcessor(
            ISyncEngine syncEngine,
            JIM.Application.Interfaces.ISyncServer syncServer,
            ISyncRepository syncRepository,
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile connectedSystemRunProfile,
            Activity activity,
            CancellationTokenSource cancellationTokenSource)
            : base(syncEngine, syncServer, syncRepository, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource)
        {
        }

        public ActivityRunProfileExecutionItem RegisterRpeiFor(Guid metaverseObjectId, bool assignId = true)
        {
            var rpei = new ActivityRunProfileExecutionItem { ObjectChangeType = ObjectChangeType.AttributeFlow };
            if (assignId)
                rpei.Id = Guid.NewGuid();

            _mvoIdToRpei[metaverseObjectId] = rpei;
            return rpei;
        }

        public void CallStampQueueingItemOnPendingExports(Guid metaverseObjectId, List<PendingExport> pendingExports)
            => StampQueueingItemOnPendingExports(metaverseObjectId, pendingExports);
    }
}
