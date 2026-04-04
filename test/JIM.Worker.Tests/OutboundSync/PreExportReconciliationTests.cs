using JIM.Application.Servers;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for SyncEngine.ReconcileCreateDeletePairs — pure logic that identifies
/// pending export pairs (CREATE+DELETE, UPDATE+DELETE) targeting the same CSO
/// that cancel each other out and should not be exported.
/// </summary>
public class PreExportReconciliationTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _engine = new SyncEngine();
    }

    #region CREATE + DELETE (Pending) → cancel both

    [Test]
    public void ReconcileCreateDeletePairs_CreateAndDeleteForSameCso_ReturnsBothForCancellationAsync()
    {
        // Arrange
        var csoId = Guid.NewGuid();
        var mvoId = Guid.NewGuid();
        var createPe = CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, csoId, mvoId);
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId, mvoId);
        var exports = new List<PendingExportSummary> { createPe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(1), "Should find one reconciled pair");
        Assert.That(result.TotalCancelled, Is.EqualTo(2), "Both CREATE and DELETE should be cancelled");

        var pair = result.ReconciledPairs[0];
        Assert.That(pair.ConnectedSystemObjectId, Is.EqualTo(csoId));
        Assert.That(pair.CancelledExportIds, Contains.Item(createPe.Id));
        Assert.That(pair.CancelledExportIds, Contains.Item(deletePe.Id));
        Assert.That(pair.Reason, Does.Contain("CREATE").IgnoreCase);
        Assert.That(pair.Reason, Does.Contain("DELETE").IgnoreCase);
    }

    #endregion

    #region UPDATE + DELETE (Pending) → remove UPDATE, keep DELETE

    [Test]
    public void ReconcileCreateDeletePairs_UpdateAndDeleteForSameCso_ReturnsUpdateForRemovalKeepsDelete()
    {
        // Arrange
        var csoId = Guid.NewGuid();
        var mvoId = Guid.NewGuid();
        var updatePe = CreateTestSummary(PendingExportChangeType.Update, PendingExportStatus.Pending, csoId, mvoId);
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId, mvoId);
        var exports = new List<PendingExportSummary> { updatePe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(1), "Should find one reconciled pair");
        Assert.That(result.TotalCancelled, Is.EqualTo(1), "Only UPDATE should be cancelled, DELETE is kept");

        var pair = result.ReconciledPairs[0];
        Assert.That(pair.CancelledExportIds, Contains.Item(updatePe.Id), "UPDATE should be cancelled");
        Assert.That(pair.CancelledExportIds, Does.Not.Contain(deletePe.Id), "DELETE should NOT be cancelled");
    }

    #endregion

    #region CREATE (already exported) + DELETE → keep DELETE

    [Test]
    public void ReconcileCreateDeletePairs_CreateAlreadyExported_KeepsDelete()
    {
        // Arrange — CREATE has status Exported (already sent to target system)
        var csoId = Guid.NewGuid();
        var mvoId = Guid.NewGuid();
        var createPe = CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Exported, csoId, mvoId);
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId, mvoId);
        var exports = new List<PendingExportSummary> { createPe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert — no reconciliation because the CREATE was already exported (object may exist in target)
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(0),
            "Should not reconcile when CREATE has already been exported — object may exist in target system");
        Assert.That(result.TotalCancelled, Is.EqualTo(0));
    }

    [Test]
    public void ReconcileCreateDeletePairs_CreateExportNotConfirmed_KeepsDelete()
    {
        // Arrange — CREATE has status ExportNotConfirmed (sent but not confirmed)
        var csoId = Guid.NewGuid();
        var mvoId = Guid.NewGuid();
        var createPe = CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.ExportNotConfirmed, csoId, mvoId);
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId, mvoId);
        var exports = new List<PendingExportSummary> { createPe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert — no reconciliation because the CREATE may have been sent
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(0),
            "Should not reconcile when CREATE status is ExportNotConfirmed — object may exist in target system");
    }

    #endregion

    #region No matching pairs

    [Test]
    public void ReconcileCreateDeletePairs_NoMatchingPairs_ReturnsEmpty()
    {
        // Arrange — CREATE and DELETE for different CSOs
        var createPe = CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, Guid.NewGuid(), Guid.NewGuid());
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, Guid.NewGuid(), Guid.NewGuid());
        var exports = new List<PendingExportSummary> { createPe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(0));
        Assert.That(result.TotalCancelled, Is.EqualTo(0));
    }

    [Test]
    public void ReconcileCreateDeletePairs_EmptyList_ReturnsEmpty()
    {
        // Act
        var result = _engine.ReconcileCreateDeletePairs(new List<PendingExportSummary>());

        // Assert
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(0));
        Assert.That(result.TotalCancelled, Is.EqualTo(0));
    }

    #endregion

    #region Multiple pairs

    [Test]
    public void ReconcileCreateDeletePairs_MultiplePairs_ReconcillesAll()
    {
        // Arrange — two separate CSOs each with CREATE+DELETE
        var csoId1 = Guid.NewGuid();
        var csoId2 = Guid.NewGuid();
        var mvoId1 = Guid.NewGuid();
        var mvoId2 = Guid.NewGuid();

        var exports = new List<PendingExportSummary>
        {
            CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, csoId1, mvoId1),
            CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId1, mvoId1),
            CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, csoId2, mvoId2),
            CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId2, mvoId2),
        };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(2), "Should reconcile both pairs");
        Assert.That(result.TotalCancelled, Is.EqualTo(4), "All four exports should be cancelled");
    }

    #endregion

    #region CREATE with null CsoId

    [Test]
    public void ReconcileCreateDeletePairs_CreateWithNullCsoId_SkipsReconciliation()
    {
        // Arrange — CREATE has no CSO ID (brand new provisioning before CSO creation)
        var mvoId = Guid.NewGuid();
        var createPe = CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, connectedSystemObjectId: null, mvoId);
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, Guid.NewGuid(), mvoId);
        var exports = new List<PendingExportSummary> { createPe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert — cannot match by CSO ID when CREATE has no CSO
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(0),
            "Should not reconcile when CREATE has no ConnectedSystemObjectId");
    }

    #endregion

    #region Mixed scenarios

    [Test]
    public void ReconcileCreateDeletePairs_MixedExportsOnlyReconcilesPendingPairs()
    {
        // Arrange — one reconcilable pair, one standalone UPDATE, one standalone CREATE
        var csoId1 = Guid.NewGuid();
        var csoId2 = Guid.NewGuid();
        var csoId3 = Guid.NewGuid();

        var exports = new List<PendingExportSummary>
        {
            // Reconcilable pair
            CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, csoId1, Guid.NewGuid()),
            CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Pending, csoId1, Guid.NewGuid()),
            // Standalone UPDATE — no DELETE counterpart
            CreateTestSummary(PendingExportChangeType.Update, PendingExportStatus.Pending, csoId2, Guid.NewGuid()),
            // Standalone CREATE — no DELETE counterpart
            CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, csoId3, Guid.NewGuid()),
        };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(1), "Only the CREATE+DELETE pair should be reconciled");
        Assert.That(result.TotalCancelled, Is.EqualTo(2));
        Assert.That(result.ReconciledPairs[0].ConnectedSystemObjectId, Is.EqualTo(csoId1));
    }

    [Test]
    public void ReconcileCreateDeletePairs_DeleteNotPending_SkipsReconciliation()
    {
        // Arrange — DELETE has status Exported (already sent)
        var csoId = Guid.NewGuid();
        var createPe = CreateTestSummary(PendingExportChangeType.Create, PendingExportStatus.Pending, csoId, Guid.NewGuid());
        var deletePe = CreateTestSummary(PendingExportChangeType.Delete, PendingExportStatus.Exported, csoId, Guid.NewGuid());
        var exports = new List<PendingExportSummary> { createPe, deletePe };

        // Act
        var result = _engine.ReconcileCreateDeletePairs(exports);

        // Assert — DELETE was already exported, don't reconcile
        Assert.That(result.ReconciledPairs, Has.Count.EqualTo(0),
            "Should not reconcile when DELETE has already been exported");
    }

    #endregion

    #region Test Helpers

    private static PendingExportSummary CreateTestSummary(
        PendingExportChangeType changeType,
        PendingExportStatus status,
        Guid? connectedSystemObjectId,
        Guid? sourceMetaverseObjectId)
    {
        return new PendingExportSummary
        {
            Id = Guid.NewGuid(),
            ChangeType = changeType,
            Status = status,
            ConnectedSystemObjectId = connectedSystemObjectId,
            SourceMetaverseObjectId = sourceMetaverseObjectId
        };
    }

    #endregion
}
