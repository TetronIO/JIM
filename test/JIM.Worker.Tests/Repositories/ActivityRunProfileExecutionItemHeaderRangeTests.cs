// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count Run Profile Execution Item header range read
/// (<c>GetActivityRunProfileExecutionItemHeadersRangeAsync</c>) that backs the virtualised (infinite-scroll)
/// execution-item grid on an Activity: window correctness at absolute offsets, the skip-the-count contract
/// (a null total, never zero, when the caller already holds the count), the window-size cap, the sort
/// semantics, and that the object-type, error-type and outcome-type filters shared with the paged read apply
/// through the range entry point too.
/// </summary>
/// <remarks>
/// The search predicate, the live-Connected System Object sorts and the outcome-type filter lean on
/// <c>EF.Functions.ILike</c>, correlated subqueries and a captured-token <c>Contains</c> that the in-memory
/// provider cannot execute, so they are covered against a real database by
/// <c>ActivityRunProfileExecutionItemHeaderRangeDatabaseTests</c>. Everything here is seeded with the snapshot
/// columns and no Connected System Object, which is the fallback path the same query takes for a deleted object.
/// </remarks>
[TestFixture]
public class ActivityRunProfileExecutionItemHeaderRangeTests
{
    private const string DisplayNameSortKey = "displayname";

    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 3, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(h => h.DisplayName),
                Is.EqualTo(new[] { "Item 001", "Item 002", "Item 003" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 3, count: 3, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(h => h.DisplayName),
                Is.EqualTo(new[] { "Item 004", "Item 005", "Item 006" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 9, count: 5, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Item 010" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 100, count: 10, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 3, count: 3, sortBy: DisplayNameSortKey, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(h => h.DisplayName),
                Is.EqualTo(new[] { "Item 004", "Item 005", "Item 006" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, sorted query either way.
        var counted = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 5, count: 4, sortBy: DisplayNameSortKey);
        var uncounted = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 5, count: 4, sortBy: DisplayNameSortKey, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(h => h.DisplayName),
                Is.EqualTo(counted.Results.Select(h => h.DisplayName)));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var activityId = await SeedSequentialItemsAsync(505);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 1000, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching item. The
            // cap is 500 rather than the paged reader's 100 because nothing here is a person choosing a page
            // size: the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach
            // silently renders the shortfall as blank rows. See MaxActivityWindowSize in ActivitiesRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetActivityRunProfileExecutionItemHeadersRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
                Guid.NewGuid(), offset: 0, count: 0));
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var activityId = await SeedSequentialItemsAsync(5);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: -10, count: 2, sortBy: DisplayNameSortKey);

        Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Item 001", "Item 002" }));
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_SortDescending_ReversesTheOrderAsync()
    {
        var activityId = await SeedSequentialItemsAsync(3);

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey, sortDescending: true);

        Assert.That(result.Results.Select(h => h.DisplayName),
            Is.EqualTo(new[] { "Item 003", "Item 002", "Item 001" }));
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_ItemsOfAnotherActivity_AreNeverIncludedAsync()
    {
        var mine = await SeedSequentialItemsAsync(2);
        await SeedSequentialItemsAsync(3, displayNamePrefix: "Other");

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            mine, offset: 0, count: 10, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Item 001", "Item 002" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_ObjectTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var activity = await SeedActivityAsync();
        AddItem(activity, "User A", objectTypeSnapshot: "user");
        AddItem(activity, "User B", objectTypeSnapshot: "user");
        AddItem(activity, "Group A", objectTypeSnapshot: "group");
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activity.Id, offset: 0, count: 10, sortBy: DisplayNameSortKey, objectTypeFilter: ["group"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Group A" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_ErrorTypeFilter_RestrictsWindowAndTotalAsync()
    {
        var activity = await SeedActivityAsync();
        AddItem(activity, "Clean");
        AddItem(activity, "Broken", errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activity.Id, offset: 0, count: 10, sortBy: DisplayNameSortKey,
            errorTypeFilter: [ActivityRunProfileExecutionItemErrorType.UnhandledError]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Broken" }));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_TiedSortKey_ProducesNonOverlappingWindowsAsync()
    {
        // Every item shares one display name, so the sort key alone cannot order them. Without the id
        // tie-break the two windows may repeat and skip rows; with it they partition the match set exactly.
        var activity = await SeedActivityAsync();
        for (var i = 0; i < 20; i++)
            AddItem(activity, "Same Name");
        await _dbContext.SaveChangesAsync();

        var first = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activity.Id, offset: 0, count: 10, sortBy: DisplayNameSortKey);
        var second = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activity.Id, offset: 10, count: 10, sortBy: DisplayNameSortKey);

        // Asserted as the exact id order rather than merely as "no duplicates": the windows are only
        // guaranteed to partition the match set if the tie is broken by a total order, and the id order is the
        // observable consequence of that. Insertion order (what an untie-broken query happens to yield here)
        // is not the id order, so this fails without the tie-break.
        var expected = _dbContext.ActivityRunProfileExecutionItems
            .Where(i => i.ActivityId == activity.Id)
            .Select(i => i.Id)
            .OrderBy(id => id)
            .ToList();
        var seen = first.Results.Select(h => h.Id).Concat(second.Results.Select(h => h.Id)).ToList();
        Assert.That(seen, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var activityId = await SeedSequentialItemsAsync(10);

        var range = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset: 0, count: 10, sortBy: DisplayNameSortKey);
        var paged = await _repository.Activity.GetActivityRunProfileExecutionItemHeadersAsync(
            activityId, page: 1, pageSize: 10, sortBy: DisplayNameSortKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(h => h.Id), Is.EqualTo(paged.Results.Select(h => h.Id)));
        }
    }

    /// <summary>
    /// Seeds an Activity and <paramref name="count"/> Run Profile Execution Items whose display-name snapshots
    /// are "Item 001", "Item 002", ... (zero-padded so lexical order matches numeric order under the display
    /// name sort). Returns the Activity's id.
    /// </summary>
    private async Task<Guid> SeedSequentialItemsAsync(int count, string displayNamePrefix = "Item")
    {
        var activity = await SeedActivityAsync();
        for (var i = 1; i <= count; i++)
            AddItem(activity, $"{displayNamePrefix} {i:D3}");

        await _dbContext.SaveChangesAsync();
        return activity.Id;
    }

    private async Task<Activity> SeedActivityAsync()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            Created = DateTime.UtcNow
        };
        _dbContext.Activities.Add(activity);
        await _dbContext.SaveChangesAsync();
        return activity;
    }

    /// <summary>
    /// Adds an execution item carrying only its snapshot columns (no Connected System Object), which is the
    /// shape the header read falls back to for an object that has since been deleted.
    /// </summary>
    private void AddItem(
        Activity activity,
        string displayName,
        string objectTypeSnapshot = "user",
        ActivityRunProfileExecutionItemErrorType? errorType = null,
        string? outcomeSummary = null)
    {
        _dbContext.ActivityRunProfileExecutionItems.Add(new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activity.Id,
            ObjectChangeType = ObjectChangeType.Updated,
            DisplayNameSnapshot = displayName,
            ExternalIdSnapshot = $"ext-{displayName}",
            ObjectTypeSnapshot = objectTypeSnapshot,
            ErrorType = errorType,
            OutcomeSummary = outcomeSummary
        });
    }
}
