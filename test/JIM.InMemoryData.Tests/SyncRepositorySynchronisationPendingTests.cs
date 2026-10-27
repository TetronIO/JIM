// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Sync;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// Parity of the in-memory provider with PostgreSQL for the synchronisation-pending mark (#2092) and the selections
/// it feeds: the Delta Synchronisation predicate is strict, as the production query is; the mark selects an object
/// whatever its timestamps; the Full Synchronisation loader flags unchanged objects exactly as the production loader
/// does; the writers set the mark; and the clear honours the row-version guard. Without these, workflow tests run
/// against selections production never makes.
/// </summary>
[TestFixture]
public class SyncRepositorySynchronisationPendingTests
{
    private SyncRepository _repo = null!;

    private const int CsId = 1;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private static ConnectedSystemObject Cso(DateTime created, DateTime? lastUpdated = null, Guid? metaverseObjectId = null,
        bool synchronisationPending = false) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = CsId,
        TypeId = 100,
        ExternalIdAttributeId = 10,
        Status = ConnectedSystemObjectStatus.Normal,
        Created = created,
        LastUpdated = lastUpdated,
        MetaverseObjectId = metaverseObjectId,
        SynchronisationPending = synchronisationPending,
        AttributeValues = []
    };

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_TimestampEqualToTheWatermark_IsNotCountedAsync()
    {
        // PostgreSQL selects Created > watermark OR LastUpdated > watermark: an object last written at the instant the
        // watermark records was processed by the run that wrote it.
        var watermark = DateTime.UtcNow.AddMinutes(-5);
        _repo.SeedConnectedSystemObject(Cso(created: watermark.AddHours(-1), lastUpdated: watermark));
        _repo.SeedConnectedSystemObject(Cso(created: watermark));

        var count = await _repo.GetConnectedSystemObjectModifiedSinceCountAsync(CsId, watermark);
        var page = await _repo.GetConnectedSystemObjectsModifiedSinceAsync(CsId, watermark, 1, 10, afterId: Guid.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.Zero);
            Assert.That(page.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_MarkedObjectWithOldTimestamps_IsSelectedAsync()
    {
        var watermark = DateTime.UtcNow.AddMinutes(-5);
        var marked = Cso(created: watermark.AddHours(-2), lastUpdated: watermark.AddHours(-1), synchronisationPending: true);
        _repo.SeedConnectedSystemObject(marked);
        _repo.SeedConnectedSystemObject(Cso(created: watermark.AddHours(-2)));

        var count = await _repo.GetConnectedSystemObjectModifiedSinceCountAsync(CsId, watermark);
        var page = await _repo.GetConnectedSystemObjectsModifiedSinceAsync(CsId, watermark, 1, 10, afterId: Guid.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1));
            Assert.That(page.Results.Select(c => c.Id), Is.EqualTo(new[] { marked.Id }));
        }
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_MarksEveryCreatedObjectAsync()
    {
        var cso = Cso(created: DateTime.UtcNow);

        await _repo.CreateConnectedSystemObjectsAsync([cso]);

        Assert.That(_repo.ConnectedSystemObjects[cso.Id].SynchronisationPending, Is.True);
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_MarksEveryUpdatedObjectWhateverTheEntityHoldsAsync()
    {
        var stored = Cso(created: DateTime.UtcNow.AddHours(-1));
        _repo.SeedConnectedSystemObject(stored);
        // An import holds its own copy of the object, as the production writer's callers do.
        var importCopy = Cso(created: stored.Created, lastUpdated: DateTime.UtcNow);
        importCopy.Id = stored.Id;

        await _repo.UpdateConnectedSystemObjectsAsync([importCopy]);

        Assert.That(_repo.ConnectedSystemObjects[stored.Id].SynchronisationPending, Is.True);
    }

    [Test]
    public async Task ClearConnectedSystemObjectMarksAsync_HonoursTheRowVersionGuardAsync()
    {
        var unchanged = Cso(created: DateTime.UtcNow.AddHours(-1), synchronisationPending: true);
        var rewritten = Cso(created: DateTime.UtcNow.AddHours(-1), synchronisationPending: true);
        await _repo.CreateConnectedSystemObjectsAsync([unchanged, rewritten]);
        var seenUnchanged = unchanged.xmin;
        var seenRewritten = rewritten.xmin;

        // An import commits a further change to one of them after the synchronisation loaded both.
        await _repo.UpdateConnectedSystemObjectsAsync([rewritten]);

        var result = await _repo.ClearConnectedSystemObjectMarksAsync(
        [
            new ConnectedSystemObjectMarkClear(unchanged.Id, seenUnchanged, SynchronisationPending: true, DerivedInputChangePending: false),
            new ConnectedSystemObjectMarkClear(rewritten.Id, seenRewritten, SynchronisationPending: true, DerivedInputChangePending: false)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SynchronisationPendingCleared, Is.EqualTo(1));
            Assert.That(_repo.ConnectedSystemObjects[unchanged.Id].SynchronisationPending, Is.False);
            Assert.That(_repo.ConnectedSystemObjects[rewritten.Id].SynchronisationPending, Is.True,
                "a change committed after the load keeps its mark");
        }
    }

    [Test]
    public async Task UpdateConnectedSystemObjectJoinStatesAsync_WithSeenRowVersions_ClearsTheMarkWithTheJoinAsync()
    {
        var cso = Cso(created: DateTime.UtcNow.AddHours(-1));
        await _repo.CreateConnectedSystemObjectsAsync([cso]);
        var seen = new Dictionary<Guid, uint> { [cso.Id] = cso.xmin };

        cso.MetaverseObjectId = Guid.NewGuid();
        cso.JoinType = ConnectedSystemObjectJoinType.Projected;
        var cleared = await _repo.UpdateConnectedSystemObjectJoinStatesAsync([cso], seen);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(_repo.ConnectedSystemObjects[cso.Id].SynchronisationPending, Is.False);
        }
    }
}
