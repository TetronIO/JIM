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
    public async Task DeleteExpiredActivitiesAsync_SparesAuthenticationActivitiesAsync()
    {
        var expiredPlain = NewActivity(daysOld: 100);
        var expiredAuthentication = NewActivity(daysOld: 100, targetType: ActivityTargetType.Authentication);
        _dbContext.Activities.AddRange(expiredPlain, expiredAuthentication);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(1), "only the expired non-security-event Activity is eligible");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        Assert.That(remainingIds, Does.Not.Contain(expiredPlain.Id));
        Assert.That(remainingIds, Does.Contain(expiredAuthentication.Id),
            "an Authentication Activity is a security event and must be governed only by its own retention cutoff");
    }

    [Test]
    public async Task DeleteExpiredActivitiesAsync_SparesPasswordSynchronisationActivitiesAsync()
    {
        var expiredPlain = NewActivity(daysOld: 100);
        var expiredPassword = NewActivity(daysOld: 100, targetType: ActivityTargetType.PasswordSynchronisation);
        _dbContext.Activities.AddRange(expiredPlain, expiredPassword);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(1), "only the expired general Activity is eligible");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remainingIds, Does.Not.Contain(expiredPlain.Id));
            Assert.That(remainingIds, Does.Contain(expiredPassword.Id),
                "a Password Synchronisation Activity answers a question asked long after the sync history around " +
                "it stops mattering, so it is governed only by its own retention cutoff");
        }
    }

    [Test]
    public async Task DeleteExpiredPasswordEventActivitiesAsync_DeletesOnlyExpiredPasswordActivitiesAsync()
    {
        var expiredPlain = NewActivity(daysOld: 100);
        var expiredAuthentication = NewActivity(daysOld: 100, targetType: ActivityTargetType.Authentication);
        var expiredPassword = NewActivity(daysOld: 100, targetType: ActivityTargetType.PasswordSynchronisation);
        var currentPassword = NewActivity(daysOld: 1, targetType: ActivityTargetType.PasswordSynchronisation);
        _dbContext.Activities.AddRange(expiredPlain, expiredAuthentication, expiredPassword, currentPassword);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredPasswordEventActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(1), "only the expired Password Synchronisation Activity is eligible");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remainingIds, Does.Contain(expiredPlain.Id), "general Activities are the general cleanup's concern");
            Assert.That(remainingIds, Does.Contain(expiredAuthentication.Id), "security events have their own cutoff");
            Assert.That(remainingIds, Does.Not.Contain(expiredPassword.Id));
            Assert.That(remainingIds, Does.Contain(currentPassword.Id));
        }
    }

    [Test]
    public async Task DeleteExpiredPasswordEventActivitiesAsync_HonoursTheBatchCapOldestFirstAsync()
    {
        // Requirement 30: every trim is bounded by the shared cleanup batch size, so a deployment that has
        // accumulated a large backlog drains over several passes rather than in one long transaction. Oldest
        // first is what makes successive passes drain rather than churn the same arbitrary slice.
        var oldest = NewActivity(daysOld: 300, targetType: ActivityTargetType.PasswordSynchronisation);
        var middle = NewActivity(daysOld: 200, targetType: ActivityTargetType.PasswordSynchronisation);
        var newest = NewActivity(daysOld: 100, targetType: ActivityTargetType.PasswordSynchronisation);
        _dbContext.Activities.AddRange(newest, oldest, middle);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredPasswordEventActivitiesAsync(DateTime.UtcNow.AddDays(-90), 2);

        Assert.That(deleted, Is.EqualTo(2), "the batch cap bounds the pass");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        Assert.That(remainingIds, Is.EqualTo(new[] { newest.Id }),
            "the two oldest go first, so the next pass continues rather than repeating");
    }

    [Test]
    public async Task DeleteExpiredSecurityEventActivitiesAsync_DeletesOnlyExpiredAuthenticationActivitiesAsync()
    {
        var expiredPlain = NewActivity(daysOld: 100);
        var expiredAuthentication = NewActivity(daysOld: 100, targetType: ActivityTargetType.Authentication);
        var currentAuthentication = NewActivity(daysOld: 1, targetType: ActivityTargetType.Authentication);
        _dbContext.Activities.AddRange(expiredPlain, expiredAuthentication, currentAuthentication);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredSecurityEventActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(1), "only the expired Authentication Activity is eligible");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        Assert.That(remainingIds, Does.Contain(expiredPlain.Id), "non-security-event Activities are the general cleanup's concern");
        Assert.That(remainingIds, Does.Not.Contain(expiredAuthentication.Id));
        Assert.That(remainingIds, Does.Contain(currentAuthentication.Id));
    }

    [Test]
    public async Task DeleteExpiredConfigurationChangeActivitiesAsync_LeavesAuthenticationActivitiesUntouchedAsync()
    {
        var expiredAuthentication = NewActivity(daysOld: 100, targetType: ActivityTargetType.Authentication);
        _dbContext.Activities.Add(expiredAuthentication);
        await _dbContext.SaveChangesAsync();

        var deleted = await _repository.ChangeHistory.DeleteExpiredConfigurationChangeActivitiesAsync(DateTime.UtcNow.AddDays(-90), 100);

        Assert.That(deleted, Is.EqualTo(0), "Authentication Activities carry no configuration snapshot, so this cleanup must never touch them");
        var remainingIds = await _dbContext.Activities.Select(a => a.Id).ToListAsync();
        Assert.That(remainingIds, Does.Contain(expiredAuthentication.Id));
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

    private static Activity NewActivity(int daysOld, int? configurationChangeVersion = null, ActivityTargetType? targetType = null) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = targetType ?? (configurationChangeVersion == null ? ActivityTargetType.ConnectedSystemRunProfile : ActivityTargetType.ConnectedSystem),
        TargetOperationType = ActivityTargetOperationType.Update,
        Created = DateTime.UtcNow.AddDays(-daysOld),
        ConfigurationChangeVersion = configurationChangeVersion,
        ConfigurationChangeSnapshot = configurationChangeVersion == null ? null : "{\"objectType\":\"ConnectedSystem\"}"
    };
}
