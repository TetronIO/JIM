// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the Activities list initiator-type and date-range filters: auditors need to isolate, for example,
/// user-made changes within a window, so the repository must filter on InitiatedByType and Created.
/// </summary>
[TestFixture]
public class ActivityListFilterTests
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
    public async Task GetActivitiesAsync_InitiatorTypeFilter_ReturnsOnlyMatchingInitiatorsAsync()
    {
        var userActivity = NewActivity(ActivityInitiatorType.User, daysOld: 1);
        var apiKeyActivity = NewActivity(ActivityInitiatorType.ApiKey, daysOld: 1);
        var systemActivity = NewActivity(ActivityInitiatorType.System, daysOld: 1);
        _dbContext.Activities.AddRange(userActivity, apiKeyActivity, systemActivity);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100,
            initiatorTypeFilter: [ActivityInitiatorType.User, ActivityInitiatorType.ApiKey]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { userActivity.Id, apiKeyActivity.Id }),
            "only user- and API-key-initiated activities were requested");
    }

    [Test]
    public async Task GetActivitiesAsync_DateRange_ReturnsOnlyActivitiesWithinRangeAsync()
    {
        var tooOld = NewActivity(ActivityInitiatorType.User, daysOld: 30);
        var inRange = NewActivity(ActivityInitiatorType.User, daysOld: 5);
        var tooNew = NewActivity(ActivityInitiatorType.User, daysOld: 1);
        _dbContext.Activities.AddRange(tooOld, inRange, tooNew);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100,
            createdFrom: DateTime.UtcNow.AddDays(-7),
            createdTo: DateTime.UtcNow.AddDays(-2));

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { inRange.Id }));
    }

    [Test]
    public async Task GetActivitiesAsync_OpenEndedDateRange_AppliesOnlyTheSuppliedBoundAsync()
    {
        var older = NewActivity(ActivityInitiatorType.User, daysOld: 30);
        var newer = NewActivity(ActivityInitiatorType.User, daysOld: 1);
        _dbContext.Activities.AddRange(older, newer);
        await _dbContext.SaveChangesAsync();

        var fromOnly = await _repository.Activity.GetActivitiesAsync(page: 1, pageSize: 100, createdFrom: DateTime.UtcNow.AddDays(-7));
        var toOnly = await _repository.Activity.GetActivitiesAsync(page: 1, pageSize: 100, createdTo: DateTime.UtcNow.AddDays(-7));

        Assert.That(fromOnly.Results.Select(a => a.Id), Is.EquivalentTo(new[] { newer.Id }));
        Assert.That(toOnly.Results.Select(a => a.Id), Is.EquivalentTo(new[] { older.Id }));
    }

    private static Activity NewActivity(ActivityInitiatorType initiatorType, int daysOld) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystem,
        TargetOperationType = ActivityTargetOperationType.Update,
        InitiatedByType = initiatorType,
        InitiatedByName = initiatorType.ToString(),
        Created = DateTime.UtcNow.AddDays(-daysOld)
    };
}
