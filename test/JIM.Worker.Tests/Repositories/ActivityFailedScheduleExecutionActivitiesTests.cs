// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests GetFailedScheduleExecutionActivitiesAsync (#1787): the Activities of one Schedule Execution whose step did not
/// succeed (it failed outright, completed with errors, or was cancelled), in step order. The Scheduler reads them when a
/// run reaches its end, to decide between Complete and Complete With Error and to name each failed step.
/// </summary>
[TestFixture]
public class ActivityFailedScheduleExecutionActivitiesTests
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
    public async Task GetFailedScheduleExecutionActivitiesAsync_MixedOutcomes_ReturnsOnlyFailedOutcomesInStepOrderAsync()
    {
        var executionId = Guid.NewGuid();
        var cancelled = NewActivity(executionId, 3, ActivityStatus.Cancelled);
        var failed = NewActivity(executionId, 1, ActivityStatus.FailedWithError);
        var completeWithError = NewActivity(executionId, 2, ActivityStatus.CompleteWithError);
        _dbContext.Activities.AddRange(
            cancelled,
            failed,
            completeWithError,
            NewActivity(executionId, 0, ActivityStatus.Complete),
            NewActivity(executionId, 4, ActivityStatus.CompleteWithWarning),
            NewActivity(executionId, 5, ActivityStatus.InProgress));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetFailedScheduleExecutionActivitiesAsync(executionId);

        Assert.That(result.Select(a => a.Id), Is.EqualTo(new[] { failed.Id, completeWithError.Id, cancelled.Id }),
            "warnings are not failures, and the steps come back in step order");
    }

    [Test]
    public async Task GetFailedScheduleExecutionActivitiesAsync_AnotherExecutionsFailure_IsExcludedAsync()
    {
        var executionId = Guid.NewGuid();
        _dbContext.Activities.AddRange(
            NewActivity(Guid.NewGuid(), 0, ActivityStatus.FailedWithError),
            NewActivity(null, 0, ActivityStatus.FailedWithError),
            NewActivity(executionId, 0, ActivityStatus.Complete));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetFailedScheduleExecutionActivitiesAsync(executionId);

        Assert.That(result, Is.Empty);
    }

    private static Activity NewActivity(Guid? scheduleExecutionId, int stepIndex, ActivityStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetContext = "Test Connected System",
        TargetName = "Full Import",
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        Created = DateTime.UtcNow,
        Status = status,
        ScheduleExecutionId = scheduleExecutionId,
        ScheduleStepIndex = scheduleExecutionId.HasValue ? stepIndex : null
    };
}
