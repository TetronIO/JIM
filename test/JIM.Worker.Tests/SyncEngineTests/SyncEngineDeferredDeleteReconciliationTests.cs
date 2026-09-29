// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Flush-time reconciliation (#218): before a page's deferred Pending Exports are persisted, each deferred Create
/// or Update is checked against a Delete already persisted for the same Connected System Object earlier in the
/// page. A Create and a Delete cancel each other (the object was never exported, so there is nothing to create
/// or delete); an Update is dropped and the Delete proceeds. Extracted from the worker's
/// <c>ReconcileDeferredExportsAgainstPersistedDeletesAsync</c> so the pairing rules are pinned directly.
/// </summary>
[TestFixture]
public class SyncEngineDeferredDeleteReconciliationTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_CreateAndPendingDelete_CancelsBoth()
    {
        var csoId = Guid.NewGuid();
        var create = Pe(csoId, PendingExportChangeType.Create);
        var delete = Pe(csoId, PendingExportChangeType.Delete);

        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes([create], Persisted(delete));

        var pair = pairs.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pair.Deferred, Is.SameAs(create));
            Assert.That(pair.PersistedDelete, Is.SameAs(delete));
            Assert.That(pair.Outcome, Is.EqualTo(DeferredDeleteReconciliationOutcome.CancelBoth));
        }
    }

    [Test]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_UpdateAndPendingDelete_DropsTheUpdateAndKeepsTheDelete()
    {
        var csoId = Guid.NewGuid();
        var update = Pe(csoId, PendingExportChangeType.Update);
        var delete = Pe(csoId, PendingExportChangeType.Delete);

        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes([update], Persisted(delete));

        var pair = pairs.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pair.Deferred, Is.SameAs(update));
            Assert.That(pair.Outcome, Is.EqualTo(DeferredDeleteReconciliationOutcome.DropDeferredUpdate));
        }
    }

    [TestCase(PendingExportStatus.Exported)]
    [TestCase(PendingExportStatus.Executing)]
    [TestCase(PendingExportStatus.ExportNotConfirmed)]
    [TestCase(PendingExportStatus.Failed)]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_DeleteNotPending_PairsNothing(PendingExportStatus deleteStatus)
    {
        // Only a Delete that has not been attempted is safe to cancel or rely on.
        var csoId = Guid.NewGuid();
        var delete = Pe(csoId, PendingExportChangeType.Delete, deleteStatus);

        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes([Pe(csoId, PendingExportChangeType.Create)], Persisted(delete));

        Assert.That(pairs, Is.Empty);
    }

    [Test]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_PersistedExportIsNotADelete_PairsNothing()
    {
        var csoId = Guid.NewGuid();

        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes(
            [Pe(csoId, PendingExportChangeType.Update)], Persisted(Pe(csoId, PendingExportChangeType.Update)));

        Assert.That(pairs, Is.Empty);
    }

    [Test]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_DeferredExportNotPending_PairsNothing()
    {
        var csoId = Guid.NewGuid();

        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes(
            [Pe(csoId, PendingExportChangeType.Update, PendingExportStatus.Exported)], Persisted(Pe(csoId, PendingExportChangeType.Delete)));

        Assert.That(pairs, Is.Empty);
    }

    [Test]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_DeleteForAnotherObject_PairsNothing()
    {
        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes(
            [Pe(Guid.NewGuid(), PendingExportChangeType.Create)], Persisted(Pe(Guid.NewGuid(), PendingExportChangeType.Delete)));

        Assert.That(pairs, Is.Empty);
    }

    [Test]
    public void ReconcileDeferredExportsAgainstPersistedDeletes_SeveralObjects_PairsEachOnItsOwn()
    {
        var createCso = Guid.NewGuid();
        var updateCso = Guid.NewGuid();
        var untouchedCso = Guid.NewGuid();
        var create = Pe(createCso, PendingExportChangeType.Create);
        var update = Pe(updateCso, PendingExportChangeType.Update);
        var untouched = Pe(untouchedCso, PendingExportChangeType.Update);

        var pairs = _engine.ReconcileDeferredExportsAgainstPersistedDeletes(
            [create, update, untouched],
            Persisted(Pe(createCso, PendingExportChangeType.Delete), Pe(updateCso, PendingExportChangeType.Delete)));

        Assert.That(pairs.Select(p => (p.Deferred, p.Outcome)), Is.EquivalentTo(new[]
        {
            (create, DeferredDeleteReconciliationOutcome.CancelBoth),
            (update, DeferredDeleteReconciliationOutcome.DropDeferredUpdate)
        }));
    }

    private static PendingExport Pe(Guid csoId, PendingExportChangeType changeType, PendingExportStatus status = PendingExportStatus.Pending) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = csoId,
        ChangeType = changeType,
        Status = status
    };

    private static Dictionary<Guid, PendingExport> Persisted(params PendingExport[] pendingExports) =>
        pendingExports.ToDictionary(pe => pe.ConnectedSystemObjectId!.Value);
}
