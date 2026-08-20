// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count Activities range read (<c>GetActivitiesRangeAsync</c>) that backs the virtualised
/// (infinite-scroll) Activity list: window correctness at absolute offsets, the skip-the-count contract
/// (a null total, never zero, when the caller already holds the count), the window-size cap, and that the
/// filters shared with the paged read apply through the range entry point too.
/// </summary>
[TestFixture]
public class ActivityRangeTests
{
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
    public async Task GetActivitiesRangeAsync_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 3, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Activity 001", "Activity 002", "Activity 003" }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 3, count: 3, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Activity 004", "Activity 005", "Activity 006" }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 9, count: 5, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Activity 010" }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 100, count: 10, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 3, count: 3, sortBy: "target", sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Activity 004", "Activity 005", "Activity 006" }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, sorted query either way.
        var counted = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 5, count: 4, sortBy: "target", sortDescending: true);
        var uncounted = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 5, count: 4, sortBy: "target", sortDescending: true, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(a => a.TargetName),
                Is.EqualTo(counted.Results.Select(a => a.TargetName)));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedSequentialActivitiesAsync(505);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 1000, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every matching Activity.
            // The cap is 500 rather than the paged reader's 100 because nothing here is a person choosing a
            // page size: the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach
            // silently renders the shortfall as blank rows. See MaxActivityWindowSize in ActivitiesRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedSequentialActivitiesAsync(10);

        var range = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, sortBy: "target", sortDescending: false);
        var paged = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 10, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(a => a.TargetName),
                Is.EqualTo(paged.Results.Select(a => a.TargetName)));
        }
    }

    [Test]
    public void GetActivitiesRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.Activity.GetActivitiesRangeAsync(startIndex: 0, count: 0));
    }

    [Test]
    public async Task GetActivitiesRangeAsync_NegativeStartIndex_IsTreatedAsTheTopOfTheListAsync()
    {
        await SeedSequentialActivitiesAsync(5);

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: -10, count: 2, sortBy: "target", sortDescending: false);

        Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "Activity 001", "Activity 002" }));
    }

    [Test]
    public async Task GetActivitiesRangeAsync_ChildActivities_AreNeverIncludedAsync()
    {
        var parent = NewActivity(targetName: "Parent");
        var child = NewActivity(targetName: "Child");
        child.ParentActivityId = parent.Id;
        _dbContext.Activities.AddRange(parent, child);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(startIndex: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The range read serves the top-level Activity list, so child Activities are excluded from both
            // the window and the total, exactly as the paged read excludes them.
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { parent.Id }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_HasChildActivitiesFilter_ReturnsOnlyParentsWithChildrenAsync()
    {
        var withChild = NewActivity(targetName: "With Child");
        var child = NewActivity(targetName: "Child");
        child.ParentActivityId = withChild.Id;
        var withoutChild = NewActivity(targetName: "Without Child");
        _dbContext.Activities.AddRange(withChild, child, withoutChild);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, hasChildActivities: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { withChild.Id }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_StatusFilter_NarrowsBothWindowAndTotalAsync()
    {
        var complete = NewActivity(targetName: "Complete");
        complete.Status = ActivityStatus.Complete;
        var failed = NewActivity(targetName: "Failed");
        failed.Status = ActivityStatus.FailedWithError;
        var inProgress = NewActivity(targetName: "In Progress");
        inProgress.Status = ActivityStatus.InProgress;
        _dbContext.Activities.AddRange(complete, failed, inProgress);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10,
            statusFilter: [ActivityStatus.Complete, ActivityStatus.FailedWithError]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { complete.Id, failed.Id }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_TypeOperationAndInitiatorFilters_CombineWithAndAsync()
    {
        var match = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Create,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedByName = "Alice Adams",
            Created = DateTime.UtcNow
        };
        var wrongOperation = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Delete,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedByName = "Alice Adams",
            Created = DateTime.UtcNow
        };
        var wrongInitiator = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Create,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Created = DateTime.UtcNow
        };
        _dbContext.Activities.AddRange(match, wrongOperation, wrongInitiator);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10,
            operationFilter: [ActivityTargetOperationType.Create],
            typeFilter: [ActivityTargetType.SynchronisationRule],
            initiatorTypeFilter: [ActivityInitiatorType.User]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { match.Id }));
    }

    [Test]
    public async Task GetActivitiesRangeAsync_SearchQuery_MatchesTargetAndInitiatorNamesAsync()
    {
        var byTargetName = NewActivity(targetName: "Contoso Full Import");
        var byInitiator = NewActivity(targetName: "Other");
        byInitiator.InitiatedByName = "Connie Contoso";
        var noMatch = NewActivity(targetName: "Fabrikam Export");
        _dbContext.Activities.AddRange(byTargetName, byInitiator, noMatch);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, searchQuery: "contoso");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { byTargetName.Id, byInitiator.Id }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_DateRange_ReturnsOnlyActivitiesWithinRangeAsync()
    {
        var tooOld = NewActivity(targetName: "Too Old");
        tooOld.Created = DateTime.UtcNow.AddDays(-30);
        var inRange = NewActivity(targetName: "In Range");
        inRange.Created = DateTime.UtcNow.AddDays(-5);
        var tooNew = NewActivity(targetName: "Too New");
        tooNew.Created = DateTime.UtcNow.AddDays(-1);
        _dbContext.Activities.AddRange(tooOld, inRange, tooNew);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10,
            createdFrom: DateTime.UtcNow.AddDays(-7),
            createdTo: DateTime.UtcNow.AddDays(-2));

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { inRange.Id }));
    }

    [Test]
    public async Task GetActivitiesRangeAsync_InitiatedById_ReturnsOnlyThatPrincipalsActivitiesAsync()
    {
        var principalId = Guid.NewGuid();
        var mine = NewActivity(targetName: "Mine");
        mine.InitiatedById = principalId;
        var someoneElses = NewActivity(targetName: "Someone Else's");
        someoneElses.InitiatedById = Guid.NewGuid();
        _dbContext.Activities.AddRange(mine, someoneElses);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, initiatedById: principalId);

        using (Assert.EnterMultipleScope())
        {
            // The "My Activity" view narrows the list to the signed-in principal, so the total must narrow too.
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { mine.Id }));
        }
    }

    [Test]
    public async Task GetActivitiesRangeAsync_TiedCreatedTimes_ProduceNonOverlappingWindowsAsync()
    {
        // A Schedule that fans several Run Profiles out at once stamps every Activity with the same created
        // time, so the default sort key alone cannot order them. Without the id tie-break the two windows may
        // repeat and skip rows; with it they partition the match set exactly.
        var created = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 20; i++)
        {
            var activity = NewActivity(targetName: "Simultaneous");
            activity.Created = created;
            _dbContext.Activities.Add(activity);
        }
        await _dbContext.SaveChangesAsync();

        var first = await _repository.Activity.GetActivitiesRangeAsync(startIndex: 0, count: 10);
        var second = await _repository.Activity.GetActivitiesRangeAsync(startIndex: 10, count: 10);

        // Asserted as the exact id order rather than merely as "no duplicates": the windows are only
        // guaranteed to partition the match set if the tie is broken by a total order, and the id order is the
        // observable consequence of that. Insertion order (what an untie-broken query happens to yield here)
        // is not the id order, so this fails without the tie-break.
        var expected = _dbContext.Activities.Select(a => a.Id).OrderBy(id => id).ToList();
        var seen = first.Results.Select(a => a.Id).Concat(second.Results.Select(a => a.Id)).ToList();
        Assert.That(seen, Is.EqualTo(expected));
    }

    /// <summary>
    /// Seeds <paramref name="count"/> top-level Activities named "Activity 001", "Activity 002", ...
    /// (zero-padded so lexical order matches numeric order under the target-name sort).
    /// </summary>
    private async Task SeedSequentialActivitiesAsync(int count)
    {
        var baseline = DateTime.UtcNow.AddDays(-1);
        for (var i = 1; i <= count; i++)
        {
            var activity = NewActivity(targetName: $"Activity {i:D3}");
            activity.Created = baseline.AddSeconds(i);
            _dbContext.Activities.Add(activity);
        }

        await _dbContext.SaveChangesAsync();
    }

    private static Activity NewActivity(string targetName) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystem,
        TargetOperationType = ActivityTargetOperationType.Update,
        TargetName = targetName,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Test User",
        Created = DateTime.UtcNow
    };
}
