// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests that Activity retention is target-type-aware: the general history cleanup must never delete
/// configuration-change Activities (those carrying a versioned configuration snapshot), because they ARE the
/// configuration change history and are governed by their own, longer retention period. A dedicated deletion method
/// removes expired configuration-change Activities only.
/// </summary>
[TestFixture]
public class ChangeHistoryRetentionTests
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
    public async Task DeleteExpiredActivitiesAsync_SparesConfigurationChangeActivitiesAsync()
    {
        var expiredPlain = NewActivity(daysOld: 100);
        var expiredConfigurationChange = NewActivity(daysOld: 100, configurationChangeVersion: 3);
        var currentPlain = NewActivity(daysOld: 1);
        _dbContext.Activities.AddRange(expiredPlain, expiredConfigurationChange, currentPlain);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(1), "only the expired non-configuration Activity is eligible");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        Assert.That(remainingIds, Does.Not.Contain(expiredPlain.Id));
        Assert.That(remainingIds, Does.Contain(expiredConfigurationChange.Id),
            "a configuration-change Activity is the configuration change history and must survive the general retention period");
        Assert.That(remainingIds, Does.Contain(currentPlain.Id));
    }

    [Test]
    public async Task DeleteExpiredConfigurationChangeActivitiesAsync_DeletesOnlyExpiredConfigurationChangesAsync()
    {
        var expiredPlain = NewActivity(daysOld: 100);
        var expiredConfigurationChange = NewActivity(daysOld: 100, configurationChangeVersion: 3);
        var currentConfigurationChange = NewActivity(daysOld: 1, configurationChangeVersion: 4);
        _dbContext.Activities.AddRange(expiredPlain, expiredConfigurationChange, currentConfigurationChange);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredConfigurationChangeActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(1), "only the expired configuration-change Activity is eligible");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        Assert.That(remainingIds, Does.Contain(expiredPlain.Id), "non-configuration Activities are the general cleanup's concern");
        Assert.That(remainingIds, Does.Not.Contain(expiredConfigurationChange.Id));
        Assert.That(remainingIds, Does.Contain(currentConfigurationChange.Id));
    }

    private static Activity NewActivity(int daysOld, int? configurationChangeVersion = null) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = configurationChangeVersion == null ? ActivityTargetType.ConnectedSystemRunProfile : ActivityTargetType.ConnectedSystem,
        TargetOperationType = ActivityTargetOperationType.Update,
        Created = DateTime.UtcNow.AddDays(-daysOld),
        ConfigurationChangeVersion = configurationChangeVersion,
        ConfigurationChangeSnapshot = configurationChangeVersion == null ? null : "{\"objectType\":\"ConnectedSystem\"}"
    };
}
