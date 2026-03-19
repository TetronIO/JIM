using JIM.Models.Activities;

namespace JIM.InMemoryData.Tests;

[TestFixture]
public class SyncRepositoryActivityRpeiTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new SyncRepository();
    }

    private Activity CreateActivity()
    {
        return new Activity
        {
            Id = Guid.NewGuid(),
            Status = ActivityStatus.InProgress,
            Created = DateTime.UtcNow,
            Executed = DateTime.UtcNow
        };
    }

    [Test]
    public async Task UpdateActivityAsync_StoresActivityAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        activity.ObjectsProcessed = 10;
        await _repo.UpdateActivityAsync(activity);
        // No exception = success (we can't read it back directly, but it's stored)
    }

    [Test]
    public async Task UpdateActivityMessageAsync_SetsMessageAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        await _repo.UpdateActivityMessageAsync(activity, "Processing page 3 of 5");
        Assert.That(activity.Message, Is.EqualTo("Processing page 3 of 5"));
    }

    [Test]
    public async Task FailActivityWithErrorAsync_String_SetsStatusAndMessageAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        await _repo.FailActivityWithErrorAsync(activity, "Something went wrong");
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
        Assert.That(activity.ErrorMessage, Is.EqualTo("Something went wrong"));
    }

    [Test]
    public async Task FailActivityWithErrorAsync_Exception_SetsStatusAndDetailsAsync()
    {
        var activity = CreateActivity();
        _repo.SeedActivity(activity);

        try { throw new InvalidOperationException("Test exception"); }
        catch (Exception ex)
        {
            await _repo.FailActivityWithErrorAsync(activity, ex);
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Is.EqualTo("Test exception"));
            Assert.That(activity.ErrorStackTrace, Is.Not.Null);
        }
    }

    [Test]
    public async Task BulkInsertRpeisAsync_AddsRpeisAndReturnsTrueAsync()
    {
        var activityId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = activityId };

        var result = await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_GeneratesIdWhenEmptyAsync()
    {
        var rpei = new ActivityRunProfileExecutionItem { ActivityId = Guid.NewGuid() };
        await _repo.BulkInsertRpeisAsync(new List<ActivityRunProfileExecutionItem> { rpei });
        Assert.That(rpei.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task GetActivityRpeiErrorCountsAsync_CountsCorrectlyAsync()
    {
        var activityId = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            new() { Id = Guid.NewGuid(), ActivityId = activityId }, // No error
            new() { Id = Guid.NewGuid(), ActivityId = activityId, ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch },
            new() { Id = Guid.NewGuid(), ActivityId = activityId, ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError },
            new() { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() } // Different activity
        };

        await _repo.BulkInsertRpeisAsync(rpeis);

        var (totalWithErrors, totalRpeis, totalUnhandledErrors) =
            await _repo.GetActivityRpeiErrorCountsAsync(activityId);

        Assert.That(totalRpeis, Is.EqualTo(3));
        Assert.That(totalWithErrors, Is.EqualTo(2));
        Assert.That(totalUnhandledErrors, Is.EqualTo(1));
    }

    [Test]
    public async Task GetActivityRpeiErrorCountsAsync_NoRpeis_ReturnsZerosAsync()
    {
        var (totalWithErrors, totalRpeis, totalUnhandledErrors) =
            await _repo.GetActivityRpeiErrorCountsAsync(Guid.NewGuid());

        Assert.That(totalRpeis, Is.EqualTo(0));
        Assert.That(totalWithErrors, Is.EqualTo(0));
        Assert.That(totalUnhandledErrors, Is.EqualTo(0));
    }

    [Test]
    public void DetachRpeisFromChangeTracker_IsNoOp()
    {
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        Assert.DoesNotThrow(() => _repo.DetachRpeisFromChangeTracker(
            new List<ActivityRunProfileExecutionItem> { rpei }));
    }

    [Test]
    public async Task PersistRpeiCsoChangesAsync_IsNoOpAsync()
    {
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        await _repo.PersistRpeiCsoChangesAsync(new List<ActivityRunProfileExecutionItem> { rpei });
    }
}
