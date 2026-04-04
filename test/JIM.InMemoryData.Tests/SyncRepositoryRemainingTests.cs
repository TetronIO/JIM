using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryRemainingTests
{
    private SyncRepository _repo = null!;
    private const int CsId = 1;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    #region CSO Lookup Cache

    [Test]
    public void AddCsoToCache_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            _repo.AddCsoToCache(CsId, 10, "ext-id-1", Guid.NewGuid()));
    }

    [Test]
    public void EvictCsoFromCache_DoesNotThrow()
    {
        _repo.AddCsoToCache(CsId, 10, "ext-id-1", Guid.NewGuid());
        Assert.DoesNotThrow(() =>
            _repo.EvictCsoFromCache(CsId, 10, "ext-id-1"));
    }

    [Test]
    public void EvictCsoFromCache_NonExistent_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
            _repo.EvictCsoFromCache(CsId, 10, "nonexistent"));
    }

    #endregion

    #region Sync Rules and Configuration

    [Test]
    public async Task GetSyncRulesAsync_ReturnsForSystemAsync()
    {
        _repo.SeedSyncRule(new SyncRule { Id = 1, ConnectedSystemId = CsId, Enabled = true });
        _repo.SeedSyncRule(new SyncRule { Id = 2, ConnectedSystemId = CsId, Enabled = false });
        _repo.SeedSyncRule(new SyncRule { Id = 3, ConnectedSystemId = 2, Enabled = true });

        var enabledOnly = await _repo.GetSyncRulesAsync(CsId, includeDisabled: false);
        Assert.That(enabledOnly, Has.Count.EqualTo(1));
        Assert.That(enabledOnly[0].Id, Is.EqualTo(1));

        var all = await _repo.GetSyncRulesAsync(CsId, includeDisabled: true);
        Assert.That(all, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetAllSyncRulesAsync_ReturnsAllRulesAsync()
    {
        _repo.SeedSyncRule(new SyncRule { Id = 1, ConnectedSystemId = CsId });
        _repo.SeedSyncRule(new SyncRule { Id = 2, ConnectedSystemId = 2 });

        var result = await _repo.GetAllSyncRulesAsync();
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetObjectTypesAsync_ReturnsForSystemAsync()
    {
        _repo.SeedObjectType(new ConnectedSystemObjectType { Id = 1, ConnectedSystemId = CsId, Name = "User" });
        _repo.SeedObjectType(new ConnectedSystemObjectType { Id = 2, ConnectedSystemId = 2, Name = "Group" });

        var result = await _repo.GetObjectTypesAsync(CsId);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("User"));
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_UpdatesInStoreAsync()
    {
        var cs = new ConnectedSystem { Id = CsId, Name = "HR" };
        _repo.SeedConnectedSystem(cs);

        cs.LastSyncCompletedAt = DateTime.UtcNow;
        await _repo.UpdateConnectedSystemAsync(cs);
        // No exception = success
    }

    #endregion

    #region MVO Change History

    [Test]
    public async Task CreateMetaverseObjectChangeDirectAsync_AddsChangeAsync()
    {
        var change = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeTime = DateTime.UtcNow
        };

        await _repo.CreateMetaverseObjectChangeDirectAsync(change);
        // No exception = success
    }

    [Test]
    public async Task CreateMetaverseObjectChangeDirectAsync_GeneratesIdWhenEmptyAsync()
    {
        var change = new MetaverseObjectChange
        {
            ChangeTime = DateTime.UtcNow
        };

        await _repo.CreateMetaverseObjectChangeDirectAsync(change);
        Assert.That(change.Id, Is.Not.EqualTo(Guid.Empty));
    }

    #endregion
}
