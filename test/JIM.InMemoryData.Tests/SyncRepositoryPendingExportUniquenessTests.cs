// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory repository's half of PostgreSQL's IX_PendingExports_ConnectedSystemObjectId_Unique:
/// at most one Pending Export per Connected System Object, with rows carrying no Connected System
/// Object exempt because the index is filtered on "ConnectedSystemObjectId" IS NOT NULL.
/// </summary>
/// <remarks>
/// Written because the invariant existed only in PostgreSQL (#1331). Two outbound Synchronisation Rules
/// resolving to one Connected System Object staged two Pending Exports for it, the in-memory double
/// accepted both without complaint, and the whole unit suite passed while the sync run died on a raw
/// 23505 in the integration environment. A test double that accepts what the database rejects cannot
/// prove anything about the engine's write paths, so it rejects it here too.
/// </remarks>
[TestFixture]
public class SyncRepositoryPendingExportUniquenessTests
{
    private SyncRepository _repo = null!;
    private const int CsId = 1;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static PendingExport CreatePe(Guid? csoId)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = CsId,
            ConnectedSystemObjectId = csoId,
            AttributeValueChanges = []
        };
    }

    [Test]
    public void CreatePendingExportsAsync_TwoForTheSameCsoInOneBatch_ThrowsAsync()
    {
        var csoId = Guid.NewGuid();
        var batch = new[] { CreatePe(csoId), CreatePe(csoId) };

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _repo.CreatePendingExportsAsync(batch));

        Assert.That(ex!.Message, Does.Contain(csoId.ToString()),
            "The failure must name the Connected System Object so the offending object is identifiable.");
    }

    [Test]
    public async Task CreatePendingExportsAsync_ForACsoThatAlreadyHasOne_ThrowsAsync()
    {
        var csoId = Guid.NewGuid();
        await _repo.CreatePendingExportsAsync(new[] { CreatePe(csoId) });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _repo.CreatePendingExportsAsync(new[] { CreatePe(csoId) }));

        Assert.That(ex!.Message, Does.Contain(csoId.ToString()));
    }

    [Test]
    public async Task CreatePendingExportsAsync_DifferentCsos_BothPersistAsync()
    {
        var batch = new[] { CreatePe(Guid.NewGuid()), CreatePe(Guid.NewGuid()) };

        await _repo.CreatePendingExportsAsync(batch);

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task CreatePendingExportsAsync_MultipleWithNoConnectedSystemObject_AllPersistAsync()
    {
        // The database index is filtered on NOT NULL, so Pending Exports for unresolved references that
        // have not been matched to a Connected System Object yet must not collide with each other.
        var batch = new[] { CreatePe(null), CreatePe(null), CreatePe(null) };

        await _repo.CreatePendingExportsAsync(batch);

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public async Task CreatePendingExportsAsync_AfterTheOriginalWasDeleted_SucceedsAsync()
    {
        var csoId = Guid.NewGuid();
        var first = CreatePe(csoId);
        await _repo.CreatePendingExportsAsync(new[] { first });
        await _repo.DeletePendingExportsAsync(new[] { first });

        await _repo.CreatePendingExportsAsync(new[] { CreatePe(csoId) });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void SeedPendingExport_RemainsPermissive_ForDuplicateRepairFixtures()
    {
        // Seeding is how a fixture reproduces a database that already holds duplicates, which is exactly
        // what the self-heal path exists to repair. Its database-side equivalent drops the index to do
        // the same thing, so seeding must not enforce what the write path enforces.
        var csoId = Guid.NewGuid();

        Assert.DoesNotThrow(() =>
        {
            _repo.SeedPendingExport(CreatePe(csoId));
            _repo.SeedPendingExport(CreatePe(csoId));
        });
    }
}
