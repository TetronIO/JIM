// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count child Activity range read (<c>GetChildActivitiesRangeAsync</c>) that backs the
/// virtualised (infinite-scroll) child-Activity grid on an Activity: window correctness at absolute offsets,
/// the skip-the-count contract (a null total, never zero, when the caller already holds the count), the
/// window-size cap, the oldest-first order it shares with the paged read, and that only the direct children of
/// the named parent are ever listed.
/// </summary>
[TestFixture]
public class ChildActivityRangeTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task GetChildActivitiesRangeAsync_FirstWindow_ReturnsOldestFirstSliceAndFullTotalAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Child 001", "Child 002", "Child 003" }));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 3, count: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Child 004", "Child 005", "Child 006" }));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 9, count: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 010" }));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(
            parentId, offset: 3, count: 3, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Child 004", "Child 005", "Child 006" }));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, sorted query either way.
        var counted = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 5, count: 4);
        var uncounted = await _repository.Activity.GetChildActivitiesRangeAsync(
            parentId, offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(a => a.TargetName),
                Is.EqualTo(counted.Results.Select(a => a.TargetName)));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var parentId = await SeedChildrenAsync(505);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every child. The cap is
            // 500 rather than the paged reader's 100 because nothing here is a person choosing a page size:
            // the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently
            // renders the shortfall as blank rows. See MaxActivityWindowSize in ActivitiesRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetChildActivitiesRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.Activity.GetChildActivitiesRangeAsync(Guid.NewGuid(), offset: 0, count: 0));
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var parentId = await SeedChildrenAsync(5);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: -10, count: 2);

        Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 001", "Child 002" }));
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_ChildrenOfAnotherParent_AreNeverIncludedAsync()
    {
        var mine = await SeedChildrenAsync(2);
        await SeedChildrenAsync(3, namePrefix: "Other Child");

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(mine, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 001", "Child 002" }));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_GrandchildActivities_AreNeverIncludedAsync()
    {
        var parentId = await SeedChildrenAsync(1);
        var child = _dbContext.Activities.Single(a => a.ParentActivityId == parentId);
        var grandchild = NewActivity("Grandchild", BaseTime.AddSeconds(100));
        grandchild.ParentActivityId = child.Id;
        _dbContext.Activities.Add(grandchild);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The read lists direct children only, exactly as the paged read does.
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 001" }));
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_TiedCreatedTimes_ProduceNonOverlappingWindowsAsync()
    {
        // Every child was created at the same instant, which is what a run that spawns its children in one
        // batch produces. Without the id tie-break the two windows may repeat and skip rows; with it they
        // partition the children exactly.
        var parent = NewActivity("Parent", BaseTime);
        _dbContext.Activities.Add(parent);
        for (var i = 0; i < 20; i++)
        {
            var child = NewActivity("Simultaneous", BaseTime.AddMinutes(1));
            child.ParentActivityId = parent.Id;
            _dbContext.Activities.Add(child);
        }
        await _dbContext.SaveChangesAsync();

        var first = await _repository.Activity.GetChildActivitiesRangeAsync(parent.Id, offset: 0, count: 10);
        var second = await _repository.Activity.GetChildActivitiesRangeAsync(parent.Id, offset: 10, count: 10);

        // Asserted as the exact id order rather than merely as "no duplicates": the windows are only
        // guaranteed to partition the children if the tie is broken by a total order, and the id order is the
        // observable consequence of that. Insertion order (what an untie-broken query happens to yield here)
        // is not the id order, so this fails without the tie-break.
        var expected = _dbContext.Activities
            .Where(a => a.ParentActivityId == parent.Id)
            .Select(a => a.Id)
            .OrderBy(id => id)
            .ToList();
        var seen = first.Results.Select(a => a.Id).Concat(second.Results.Select(a => a.Id)).ToList();
        Assert.That(seen, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var parentId = await SeedChildrenAsync(10);

        var range = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, offset: 0, count: 10);
        var paged = await _repository.Activity.GetChildActivitiesAsync(parentId, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(a => a.Id), Is.EqualTo(paged.Results.Select(a => a.Id)));
        }
    }


    [Test]
    public async Task GetChildActivitiesRangeAsync_SearchQuery_ReturnsOnlyMatchingChildrenAsync()
    {
        // The child Activities table carries a search box like every other list, and a search must run over all
        // the children rather than the window on screen; the page cannot filter what it has not fetched.
        var parentId = await SeedChildrenAsync(30);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, 0, 10, searchQuery: "Child 007");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 007" }));
            Assert.That(result.TotalResults, Is.EqualTo(1), "the total must describe the match set, not every child");
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_SearchQuery_IsCaseInsensitiveAsync()
    {
        var parentId = await SeedChildrenAsync(3);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, 0, 10, searchQuery: "cHiLd 002");

        Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 002" }));
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_SearchQueryMatchingNothing_ReturnsAnEmptyWindowAndAZeroTotalAsync()
    {
        // Zero here is a real answer, unlike the null a skipped count returns.
        var parentId = await SeedChildrenAsync(5);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, 0, 10, searchQuery: "nothing matches this");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results, Is.Empty);
            Assert.That(result.TotalResults, Is.Zero);
        }
    }

    [Test]
    public async Task GetChildActivitiesRangeAsync_SearchQueryWindowed_PagesTheMatchSetNotTheChildrenAsync()
    {
        // Offsets address the match set: the second window of a search must continue that search, not resume
        // the unfiltered list at the same index.
        await SeedChildrenAsync(5, "Other");
        var parentId = await SeedChildrenAsync(30);

        var result = await _repository.Activity.GetChildActivitiesRangeAsync(parentId, 1, 2, searchQuery: "Child 01");

        Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Child 011", "Child 012" }));
    }

    /// <summary>
    /// Seeds a parent Activity with <paramref name="count"/> direct children named "Child 001", "Child 002", ...
    /// with creation times staggered in that order, so the oldest-first order yields numeric name order.
    /// Returns the parent's id.
    /// </summary>
    private async Task<Guid> SeedChildrenAsync(int count, string namePrefix = "Child")
    {
        var parent = NewActivity($"{namePrefix} Parent", BaseTime);
        _dbContext.Activities.Add(parent);

        for (var i = 1; i <= count; i++)
        {
            var child = NewActivity($"{namePrefix} {i:D3}", BaseTime.AddSeconds(i));
            child.ParentActivityId = parent.Id;
            _dbContext.Activities.Add(child);
        }

        await _dbContext.SaveChangesAsync();
        return parent.Id;
    }

    private static Activity NewActivity(string targetName, DateTime created) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetName = targetName,
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        Created = created
    };
}
