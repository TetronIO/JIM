using JIM.Models.Activities;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositorySettingsTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    [Test]
    public async Task GetSyncPageSizeAsync_Default_Returns100Async()
    {
        var result = await _repo.GetSyncPageSizeAsync();
        Assert.That(result, Is.EqualTo(100));
    }

    [Test]
    public async Task GetSyncPageSizeAsync_AfterSet_ReturnsCustomValueAsync()
    {
        _repo.SetSyncPageSize(50);
        var result = await _repo.GetSyncPageSizeAsync();
        Assert.That(result, Is.EqualTo(50));
    }

    [Test]
    public async Task GetSyncOutcomeTrackingLevelAsync_Default_ReturnsNoneAsync()
    {
        var result = await _repo.GetSyncOutcomeTrackingLevelAsync();
        Assert.That(result, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None));
    }

    [Test]
    public async Task GetSyncOutcomeTrackingLevelAsync_AfterSet_ReturnsCustomValueAsync()
    {
        _repo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        var result = await _repo.GetSyncOutcomeTrackingLevelAsync();
        Assert.That(result, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed));
    }

    [Test]
    public async Task GetCsoChangeTrackingEnabledAsync_Default_ReturnsTrueAsync()
    {
        var result = await _repo.GetCsoChangeTrackingEnabledAsync();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetCsoChangeTrackingEnabledAsync_AfterSet_ReturnsCustomValueAsync()
    {
        _repo.SetCsoChangeTrackingEnabled(false);
        var result = await _repo.GetCsoChangeTrackingEnabledAsync();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetMvoChangeTrackingEnabledAsync_Default_ReturnsTrueAsync()
    {
        var result = await _repo.GetMvoChangeTrackingEnabledAsync();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task GetMvoChangeTrackingEnabledAsync_AfterSet_ReturnsCustomValueAsync()
    {
        _repo.SetMvoChangeTrackingEnabled(false);
        var result = await _repo.GetMvoChangeTrackingEnabledAsync();
        Assert.That(result, Is.False);
    }

    [Test]
    public void ClearChangeTracker_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _repo.ClearChangeTracker());
    }

    [Test]
    public void SetAutoDetectChangesEnabled_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _repo.SetAutoDetectChangesEnabled(true));
        Assert.DoesNotThrow(() => _repo.SetAutoDetectChangesEnabled(false));
    }

    [Test]
    public async Task RefreshAndAutoSelectContainersWithTriadAsync_IsNoOpAsync()
    {
        var cs = new JIM.Models.Staging.ConnectedSystem { Id = 1, Name = "Test" };
        await _repo.RefreshAndAutoSelectContainersWithTriadAsync(
            cs, null!, new List<string>(),
            JIM.Models.Activities.ActivityInitiatorType.System, null, null);
    }

    [Test]
    public async Task UpdateConnectedSystemWithTriadAsync_IsNoOpAsync()
    {
        var cs = new JIM.Models.Staging.ConnectedSystem { Id = 1, Name = "Test" };
        await _repo.UpdateConnectedSystemWithTriadAsync(
            cs, JIM.Models.Activities.ActivityInitiatorType.System, null, null);
    }
}
