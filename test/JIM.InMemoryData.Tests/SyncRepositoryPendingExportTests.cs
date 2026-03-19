using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryPendingExportTests
{
    private SyncRepository _repo = null!;
    private const int CsId = 1;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private PendingExport CreatePe(Guid? csoId = null, int connectedSystemId = CsId)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = csoId,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>()
        };
    }

    [Test]
    public async Task GetPendingExportsAsync_ReturnsForSystemAsync()
    {
        _repo.SeedPendingExport(CreatePe());
        _repo.SeedPendingExport(CreatePe(connectedSystemId: 2));

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetPendingExportsCountAsync_ReturnsCountAsync()
    {
        _repo.SeedPendingExport(CreatePe());
        _repo.SeedPendingExport(CreatePe());

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public async Task CreatePendingExportsAsync_AddsToStoreAsync()
    {
        var pe = CreatePe();
        await _repo.CreatePendingExportsAsync(new[] { pe });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task DeletePendingExportsAsync_RemovesFromStoreAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        await _repo.DeletePendingExportsAsync(new[] { pe });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UpdatePendingExportsAsync_UpdatesInStoreAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        pe.ErrorCount = 3;
        await _repo.UpdatePendingExportsAsync(new[] { pe });

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result[0].ErrorCount, Is.EqualTo(3));
    }

    [Test]
    public async Task DeletePendingExportsByConnectedSystemObjectIdsAsync_DeletesAndReturnsCountAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedPendingExport(CreatePe(csoId: csoId));

        var deleted = await _repo.DeletePendingExportsByConnectedSystemObjectIdsAsync(new[] { csoId });
        Assert.That(deleted, Is.EqualTo(1));

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetPendingExportByConnectedSystemObjectIdAsync_FindsPeAsync()
    {
        var csoId = Guid.NewGuid();
        var pe = CreatePe(csoId: csoId);
        _repo.SeedPendingExport(pe);

        var result = await _repo.GetPendingExportByConnectedSystemObjectIdAsync(csoId);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(pe.Id));
    }

    [Test]
    public async Task GetPendingExportByConnectedSystemObjectIdAsync_NotFound_ReturnsNullAsync()
    {
        var result = await _repo.GetPendingExportByConnectedSystemObjectIdAsync(Guid.NewGuid());
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPendingExportsByConnectedSystemObjectIdsAsync_ReturnsDictionaryAsync()
    {
        var csoId1 = Guid.NewGuid();
        var csoId2 = Guid.NewGuid();
        _repo.SeedPendingExport(CreatePe(csoId: csoId1));
        _repo.SeedPendingExport(CreatePe(csoId: csoId2));

        var result = await _repo.GetPendingExportsByConnectedSystemObjectIdsAsync(new[] { csoId1, Guid.NewGuid() });
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey(csoId1), Is.True);
    }

    [Test]
    public async Task GetCsoIdsWithPendingExportsByConnectedSystemAsync_ReturnsHashSetAsync()
    {
        var csoId = Guid.NewGuid();
        _repo.SeedPendingExport(CreatePe(csoId: csoId));
        _repo.SeedPendingExport(CreatePe()); // No CSO ID

        var result = await _repo.GetCsoIdsWithPendingExportsByConnectedSystemAsync(CsId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Contains(csoId), Is.True);
    }

    [Test]
    public async Task DeleteUntrackedPendingExportsAsync_BehavesLikeDeleteAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        await _repo.DeleteUntrackedPendingExportsAsync(new[] { pe });

        var count = await _repo.GetPendingExportsCountAsync(CsId);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_BehavesLikeUpdateAsync()
    {
        var pe = CreatePe();
        _repo.SeedPendingExport(pe);

        pe.ErrorCount = 5;
        await _repo.UpdateUntrackedPendingExportsAsync(new[] { pe });

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result[0].ErrorCount, Is.EqualTo(5));
    }

    [Test]
    public async Task DeleteUntrackedPendingExportAttributeValueChangesAsync_RemovesChangesAsync()
    {
        var pe = CreatePe();
        var avc = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            PendingExportId = pe.Id,
            AttributeId = 10
        };
        pe.AttributeValueChanges.Add(avc);
        _repo.SeedPendingExport(pe);

        await _repo.DeleteUntrackedPendingExportAttributeValueChangesAsync(new[] { avc });

        var result = await _repo.GetPendingExportsAsync(CsId);
        Assert.That(result[0].AttributeValueChanges, Is.Empty);
    }
}
