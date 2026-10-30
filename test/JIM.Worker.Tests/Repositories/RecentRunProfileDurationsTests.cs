// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the read behind the Operations page's estimated start times (#2549): the most recent successful execution
/// durations per Run Profile, newest first, capped per Run Profile. Only executions of a Run Profile count; a failed,
/// cancelled or still-running execution, or a Run Profile configuration change, says nothing about how long a run takes.
/// </summary>
[TestFixture]
public class RecentRunProfileDurationsTests
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
    public async Task GetRecentSuccessfulRunProfileDurationsAsync_ReturnsNewestFirstCappedPerRunProfileAsync()
    {
        for (var i = 1; i <= 7; i++)
            AddExecution(runProfileId: 1, minutes: i, hoursAgo: i);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetRecentSuccessfulRunProfileDurationsAsync([1], 5);

        Assert.That(result[1], Is.EqualTo(new[] { 1, 2, 3, 4, 5 }.Select(m => TimeSpan.FromMinutes(m))));
    }

    [Test]
    public async Task GetRecentSuccessfulRunProfileDurationsAsync_CountsCompleteAndCompleteWithWarningOnlyAsync()
    {
        AddExecution(1, minutes: 10, hoursAgo: 1, status: ActivityStatus.Complete);
        AddExecution(1, minutes: 20, hoursAgo: 2, status: ActivityStatus.CompleteWithWarning);
        AddExecution(1, minutes: 30, hoursAgo: 3, status: ActivityStatus.CompleteWithError);
        AddExecution(1, minutes: 40, hoursAgo: 4, status: ActivityStatus.FailedWithError);
        AddExecution(1, minutes: 50, hoursAgo: 5, status: ActivityStatus.Cancelled);
        AddExecution(1, minutes: 60, hoursAgo: 0, status: ActivityStatus.InProgress);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetRecentSuccessfulRunProfileDurationsAsync([1], 5);

        Assert.That(result[1], Is.EqualTo(new[] { TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20) }));
    }

    [Test]
    public async Task GetRecentSuccessfulRunProfileDurationsAsync_IgnoresConfigurationChangesAndMissingDurationsAsync()
    {
        AddExecution(1, minutes: 10, hoursAgo: 1);
        AddExecution(1, minutes: 20, hoursAgo: 2, operation: ActivityTargetOperationType.Update);
        _dbContext.Activities.Add(NewExecution(1, executionTime: null, executed: DateTime.UtcNow.AddHours(-3)));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetRecentSuccessfulRunProfileDurationsAsync([1], 5);

        Assert.That(result[1], Is.EqualTo(new[] { TimeSpan.FromMinutes(10) }));
    }

    [Test]
    public async Task GetRecentSuccessfulRunProfileDurationsAsync_ReturnsOnlyRequestedRunProfilesAndOmitsThoseWithoutHistoryAsync()
    {
        AddExecution(1, minutes: 10, hoursAgo: 1);
        AddExecution(2, minutes: 20, hoursAgo: 1);
        AddExecution(3, minutes: 30, hoursAgo: 1);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetRecentSuccessfulRunProfileDurationsAsync([1, 2, 4], 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Keys, Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(result[2], Is.EqualTo(new[] { TimeSpan.FromMinutes(20) }));
        }
    }

    [Test]
    public async Task GetRecentSuccessfulRunProfileDurationsAsync_NoRunProfilesRequested_ReturnsEmptyAsync()
    {
        AddExecution(1, minutes: 10, hoursAgo: 1);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetRecentSuccessfulRunProfileDurationsAsync([], 5);

        Assert.That(result, Is.Empty);
    }

    private void AddExecution(int runProfileId, int minutes, int hoursAgo,
        ActivityStatus status = ActivityStatus.Complete,
        ActivityTargetOperationType operation = ActivityTargetOperationType.Execute)
    {
        var activity = NewExecution(runProfileId, TimeSpan.FromMinutes(minutes), DateTime.UtcNow.AddHours(-hoursAgo));
        activity.Status = status;
        activity.TargetOperationType = operation;
        _dbContext.Activities.Add(activity);
    }

    private static Activity NewExecution(int runProfileId, TimeSpan? executionTime, DateTime executed) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetName = $"Run Profile {runProfileId}",
        ConnectedSystemRunProfileId = runProfileId,
        Status = ActivityStatus.Complete,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Test User",
        Created = executed,
        Executed = executed,
        ExecutionTime = executionTime
    };
}
