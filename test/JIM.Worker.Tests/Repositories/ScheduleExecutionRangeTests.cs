// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the offset/count Schedule Execution range read (<c>GetScheduleExecutionsRangeAsync</c>) that backs the
/// virtualised (infinite-scroll) Schedule Execution grids: window correctness at absolute offsets, the
/// skip-the-count contract (a null total, never zero, when the caller already holds the count), the window-size
/// cap, the sort semantics, and that the optional Schedule filter shared with the paged read applies through the
/// range entry point too.
/// </summary>
[TestFixture]
public class ScheduleExecutionRangeTests
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
    public async Task GetScheduleExecutionsRangeAsync_FirstWindow_ReturnsNewestFirstSliceAndFullTotalAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(scheduleId, offset: 0, count: 3);

        using (Assert.EnterMultipleScope())
        {
            // The default order is newest-queued first, matching the paged reader.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(e => e.ScheduleName),
                Is.EqualTo(new[] { "Execution 010", "Execution 009", "Execution 008" }));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: 3, count: 3, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(e => e.ScheduleName),
                Is.EqualTo(new[] { "Execution 004", "Execution 005", "Execution 006" }));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_WindowStraddlingEnd_ReturnsRemainderAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: 9, count: 5, sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(e => e.ScheduleName), Is.EqualTo(new[] { "Execution 010" }));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_OffsetBeyondEnd_ReturnsEmptyButPreservesTotalAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: 100, count: 10);

        using (Assert.EnterMultipleScope())
        {
            // The virtualiser sizes the scroll area from TotalResults, so it must stay correct past the last window.
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results, Is.Empty);
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_TotalCountNotRequested_ReturnsWindowWithNullTotalAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: 3, count: 3, sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches",
            // which is the one wrong answer a caller cannot distinguish from a real result.
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(e => e.ScheduleName),
                Is.EqualTo(new[] { "Execution 004", "Execution 005", "Execution 006" }));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_TotalCountNotRequested_ReturnsTheSameWindowAsACountedReadAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        // Skipping the count must change what the caller is told about the total and nothing else; the window
        // itself comes from the same filtered, sorted query either way.
        var counted = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(scheduleId, offset: 5, count: 4);
        var uncounted = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: 5, count: 4, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counted.TotalResults, Is.EqualTo(10));
            Assert.That(uncounted.TotalResults, Is.Null);
            Assert.That(uncounted.Results.Select(e => e.Id), Is.EqualTo(counted.Results.Select(e => e.Id)));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_CountAboveCap_ClampsToFiveHundredAsync()
    {
        var scheduleId = await SeedExecutionsAsync(505);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: 0, count: 1000);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency, while the total still reflects every execution. The cap
            // is 500 rather than the paged reader's 100 because nothing here is a person choosing a page size:
            // the virtualiser asks for as many rows as the viewport needs, and a clamp it can reach silently
            // renders the shortfall as blank rows. See MaxExecutionWindowSize in SchedulingRepository.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public void GetScheduleExecutionsRangeAsync_CountBelowOne_Throws()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _repository.Scheduling.GetScheduleExecutionsRangeAsync(Guid.NewGuid(), offset: 0, count: 0));
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_NegativeOffset_IsTreatedAsTheTopOfTheListAsync()
    {
        var scheduleId = await SeedExecutionsAsync(5);

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset: -10, count: 2, sortDescending: false);

        Assert.That(result.Results.Select(e => e.ScheduleName),
            Is.EqualTo(new[] { "Execution 001", "Execution 002" }));
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_NoScheduleFilter_ReturnsEveryScheduleExecutionAsync()
    {
        await SeedExecutionsAsync(2, scheduleName: "First");
        await SeedExecutionsAsync(3, scheduleName: "Second");

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId: null, offset: 0, count: 10);

        Assert.That(result.TotalResults, Is.EqualTo(5));
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_ScheduleFilter_RestrictsWindowAndTotalAsync()
    {
        var first = await SeedExecutionsAsync(2, scheduleName: "First");
        await SeedExecutionsAsync(3, scheduleName: "Second");

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(first, offset: 0, count: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(e => e.ScheduleId), Is.All.EqualTo(first));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_SortByStatus_OrdersByStatusAsync()
    {
        // Seeded in the reverse of the status order (Complete = 2 sorts after Queued = 0), so this test fails
        // if the sort key is ignored and the default queued-time order is used instead.
        var schedule = await SeedScheduleAsync("Ordering");
        AddExecution(schedule, "Complete", BaseTime.AddSeconds(1), ScheduleExecutionStatus.Complete);
        AddExecution(schedule, "Queued", BaseTime.AddSeconds(2), ScheduleExecutionStatus.Queued);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(
            schedule.Id, offset: 0, count: 10, sortBy: "status", sortDescending: false);

        Assert.That(result.Results.Select(e => e.Status),
            Is.EqualTo(new[] { ScheduleExecutionStatus.Queued, ScheduleExecutionStatus.Complete }));
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_TiedQueuedTimes_ProduceNonOverlappingWindowsAsync()
    {
        // A Schedule that fans several executions into the queue at once gives every row the same queued time,
        // so the sort key alone cannot order them. Without the id tie-break the two windows may repeat and skip
        // rows; with it they partition the executions exactly.
        var schedule = await SeedScheduleAsync("Simultaneous");
        for (var i = 0; i < 20; i++)
            AddExecution(schedule, "Simultaneous", BaseTime);
        await _dbContext.SaveChangesAsync();

        var first = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(schedule.Id, offset: 0, count: 10);
        var second = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(schedule.Id, offset: 10, count: 10);

        // Asserted as the exact id order rather than merely as "no duplicates": the windows are only
        // guaranteed to partition the executions if the tie is broken by a total order, and the id order is the
        // observable consequence of that. Insertion order (what an untie-broken query happens to yield here)
        // is not the id order, so this fails without the tie-break.
        var expected = _dbContext.ScheduleExecutions
            .Where(e => e.ScheduleId == schedule.Id)
            .Select(e => e.Id)
            .OrderBy(id => id)
            .ToList();
        var seen = first.Results.Select(e => e.Id).Concat(second.Results.Select(e => e.Id)).ToList();
        Assert.That(seen, Is.EqualTo(expected));
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_FullWindow_MatchesPagedReaderAsync()
    {
        var scheduleId = await SeedExecutionsAsync(10);

        var range = await _repository.Scheduling.GetScheduleExecutionsRangeAsync(scheduleId, offset: 0, count: 10);
        var paged = await _repository.Scheduling.GetScheduleExecutionsAsync(scheduleId, page: 1, pageSize: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(e => e.Id), Is.EqualTo(paged.Results.Select(e => e.Id)));
        }
    }

    /// <summary>
    /// Seeds a Schedule with <paramref name="count"/> executions named "Execution 001", "Execution 002", ...
    /// with queued times staggered in that order, so the oldest-first order yields numeric name order and the
    /// default newest-first order reverses it. Returns the Schedule's id.
    /// </summary>
    private async Task<Guid> SeedExecutionsAsync(int count, string scheduleName = "Nightly Full Sync")
    {
        var schedule = await SeedScheduleAsync(scheduleName);

        for (var i = 1; i <= count; i++)
            AddExecution(schedule, $"Execution {i:D3}", BaseTime.AddSeconds(i));

        await _dbContext.SaveChangesAsync();
        return schedule.Id;
    }

    private async Task<Schedule> SeedScheduleAsync(string name)
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = name };
        _dbContext.Schedules.Add(schedule);
        await _dbContext.SaveChangesAsync();
        return schedule;
    }

    private void AddExecution(
        Schedule schedule,
        string scheduleName,
        DateTime queuedAt,
        ScheduleExecutionStatus status = ScheduleExecutionStatus.Complete)
    {
        _dbContext.ScheduleExecutions.Add(new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            ScheduleName = scheduleName,
            Status = status,
            QueuedAt = queuedAt
        });
    }
}
