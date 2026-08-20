// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for GetScheduleHeadersAsync in SchedulingRepository (issue #1196). The Schedules list needs each Schedule's
/// most recent execution outcome, not just when it last ran, so the query projects into a ScheduleHeader carrying the
/// last execution's status, progress and error alongside the Schedule's own fields.
/// </summary>
/// <remarks>
/// CAVEAT: MockQueryable runs the query as LINQ-to-Objects, so these tests prove the projection's SHAPE (which fields
/// are populated, from which execution, and that a Schedule with no executions yields nulls). They do NOT prove that
/// EF Core can translate the projection to SQL, nor that the correlated last-execution subqueries are emitted as a
/// single round trip rather than one query per row. That is a property of the real Npgsql provider; it is guarded by
/// the composite (ScheduleId, QueuedAt) index on ScheduleExecution and verified against a real database.
/// </remarks>
[TestFixture]
public class SchedulingRepositoryHeaderTests
{
    private List<Schedule> _schedulesData = null!;
    private Mock<JimDbContext> _mockDbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _schedulesData = new List<Schedule>();
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    private void BuildRepository()
    {
        var mockDbSet = _schedulesData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.Schedules).Returns(mockDbSet.Object);
        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    private static Schedule Schedule(string name, DateTime created, string? description = null)
    {
        return new Schedule
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Created = created,
            IsEnabled = true,
            TriggerType = ScheduleTriggerType.Cron
        };
    }

    private static ScheduleExecution Execution(
        Schedule schedule,
        ScheduleExecutionStatus status,
        DateTime queuedAt,
        int currentStepIndex = 0,
        int totalSteps = 0,
        string? errorMessage = null,
        DateTime? completedAt = null)
    {
        return new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Schedule = schedule,
            ScheduleName = schedule.Name,
            Status = status,
            QueuedAt = queuedAt,
            CurrentStepIndex = currentStepIndex,
            TotalSteps = totalSteps,
            ErrorMessage = errorMessage,
            CompletedAt = completedAt
        };
    }

    [Test]
    public async Task GetScheduleHeadersAsync_ScheduleWithFailedLastExecution_ProjectsOutcomeAsync()
    {
        var schedule = Schedule("Nightly Sync", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var completedAt = new DateTime(2026, 2, 1, 3, 5, 0, DateTimeKind.Utc);
        var execution = Execution(
            schedule,
            ScheduleExecutionStatus.Failed,
            new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc),
            currentStepIndex: 2,
            totalSteps: 6,
            errorMessage: "Connected System unreachable",
            completedAt: completedAt);
        schedule.Executions.Add(execution);
        _schedulesData.Add(schedule);
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        Assert.That(result.Results, Has.Count.EqualTo(1));
        var header = result.Results[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Id, Is.EqualTo(schedule.Id));
            Assert.That(header.Name, Is.EqualTo("Nightly Sync"));
            Assert.That(header.LastExecutionId, Is.EqualTo(execution.Id));
            Assert.That(header.LastExecutionStatus, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(header.LastExecutionCurrentStepIndex, Is.EqualTo(2));
            Assert.That(header.LastExecutionTotalSteps, Is.EqualTo(6));
            Assert.That(header.LastExecutionCompletedAt, Is.EqualTo(completedAt));
            Assert.That(header.LastExecutionErrorMessage, Is.EqualTo("Connected System unreachable"));
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_ScheduleNeverRun_ProjectsNullLastExecutionFieldsAsync()
    {
        var schedule = Schedule("Never Run", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _schedulesData.Add(schedule);
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        Assert.That(result.Results, Has.Count.EqualTo(1));
        var header = result.Results[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.LastExecutionId, Is.Null);
            Assert.That(header.LastExecutionStatus, Is.Null);
            Assert.That(header.LastExecutionCurrentStepIndex, Is.Null);
            Assert.That(header.LastExecutionTotalSteps, Is.Null);
            Assert.That(header.LastExecutionCompletedAt, Is.Null);
            Assert.That(header.LastExecutionErrorMessage, Is.Null);
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_MultipleExecutions_PicksMostRecentByQueuedAtAsync()
    {
        var schedule = Schedule("Busy Schedule", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        // Deliberately added out of order, and with the OLDEST execution failing, so a projection that took the
        // first-in-collection execution rather than the newest by QueuedAt would report the wrong outcome.
        var newest = Execution(schedule, ScheduleExecutionStatus.Complete, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var oldest = Execution(schedule, ScheduleExecutionStatus.Failed, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = Execution(schedule, ScheduleExecutionStatus.Cancelled, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        schedule.Executions.AddRange(new[] { oldest, middle, newest });
        _schedulesData.Add(schedule);
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        var header = result.Results[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.LastExecutionId, Is.EqualTo(newest.Id));
            Assert.That(header.LastExecutionStatus, Is.EqualTo(ScheduleExecutionStatus.Complete));
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_ScheduleWithSteps_ProjectsStepCountAsync()
    {
        // The projection counts steps rather than Include-ing them, so a page of schedules never materialises every
        // step row. The count must still be right.
        var schedule = Schedule("Three Steps", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        schedule.Steps.AddRange(new[]
        {
            new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = schedule.Id, StepIndex = 0, Name = "One" },
            new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = schedule.Id, StepIndex = 1, Name = "Two" },
            new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = schedule.Id, StepIndex = 2, Name = "Three" }
        });
        var stepless = Schedule("No Steps", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _schedulesData.AddRange(new[] { schedule, stepless });
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results.Single(h => h.Id == schedule.Id).StepCount, Is.EqualTo(3));
            Assert.That(result.Results.Single(h => h.Id == stepless.Id).StepCount, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_WithSearchQuery_FiltersOnNameAndDescriptionAsync()
    {
        _schedulesData.Add(Schedule("Nightly Sync", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        _schedulesData.Add(Schedule("Hourly Import", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "runs a nightly reconciliation"));
        _schedulesData.Add(Schedule("Weekly Export", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10, "nightly");

        Assert.That(result.TotalResults, Is.EqualTo(2));
        Assert.That(result.Results.Select(h => h.Name), Is.EquivalentTo(new[] { "Nightly Sync", "Hourly Import" }));
    }

    [Test]
    public async Task GetScheduleHeadersAsync_SortByName_OrdersAscendingAndDescendingAsync()
    {
        _schedulesData.Add(Schedule("Charlie", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        _schedulesData.Add(Schedule("Alpha", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        _schedulesData.Add(Schedule("Bravo", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
        BuildRepository();

        var ascending = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10, null, "name");
        var descending = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10, null, "name", true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ascending.Results.Select(h => h.Name), Is.EqualTo(new[] { "Alpha", "Bravo", "Charlie" }));
            Assert.That(descending.Results.Select(h => h.Name), Is.EqualTo(new[] { "Charlie", "Bravo", "Alpha" }));
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_DefaultSort_OrdersByCreatedAsync()
    {
        _schedulesData.Add(Schedule("Second", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        _schedulesData.Add(Schedule("First", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        Assert.That(result.Results.Select(h => h.Name), Is.EqualTo(new[] { "First", "Second" }));
    }

    [Test]
    public async Task GetScheduleHeadersAsync_SecondPage_ReturnsRemainingResultsAsync()
    {
        for (var i = 0; i < 5; i++)
            _schedulesData.Add(Schedule($"Schedule {i}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)));
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(2, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(5));
            Assert.That(result.CurrentPage, Is.EqualTo(2));
            Assert.That(result.Results.Select(h => h.Name), Is.EqualTo(new[] { "Schedule 2", "Schedule 3" }));
        }
    }

    [Test]
    public async Task GetScheduleHeadersAsync_PageBeyondEnd_ReturnsEmptyResultSetAsync()
    {
        _schedulesData.Add(Schedule("Only One", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        BuildRepository();

        var result = await _repository.Scheduling.GetScheduleHeadersAsync(5, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Results, Is.Empty);
            Assert.That(result.TotalResults, Is.EqualTo(0));
        }
    }

    [Test]
    public void GetScheduleHeadersAsync_InvalidPageSize_ThrowsAsync()
    {
        BuildRepository();

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await _repository.Scheduling.GetScheduleHeadersAsync(1, 0));
    }
}
