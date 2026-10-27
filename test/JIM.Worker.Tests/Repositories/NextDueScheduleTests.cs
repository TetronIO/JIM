// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the read behind the Operations page's "next Schedule" line (#2549): the enabled Schedule due to fire soonest.
/// A disabled Schedule, or one with no next run time (a manual Schedule), never fires on its own, so it is not "next".
/// </summary>
[TestFixture]
public class NextDueScheduleTests
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
    public async Task GetNextDueScheduleAsync_SeveralEnabled_ReturnsEarliestAsync()
    {
        var soon = DateTime.UtcNow.AddHours(1);
        AddSchedule("Later", enabled: true, nextRunTime: DateTime.UtcNow.AddDays(2));
        var expected = AddSchedule("Soon", enabled: true, nextRunTime: soon);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Scheduling.GetNextDueScheduleAsync();

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Id, Is.EqualTo(expected.Id));
            Assert.That(result!.Name, Is.EqualTo("Soon"));
            Assert.That(result!.NextRunTime, Is.EqualTo(soon));
        }
    }

    [Test]
    public async Task GetNextDueScheduleAsync_IgnoresDisabledAndManualSchedulesAsync()
    {
        AddSchedule("Disabled", enabled: false, nextRunTime: DateTime.UtcNow.AddMinutes(5));
        AddSchedule("Manual", enabled: true, nextRunTime: null);
        var expected = AddSchedule("Enabled", enabled: true, nextRunTime: DateTime.UtcNow.AddHours(3));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Scheduling.GetNextDueScheduleAsync();

        Assert.That(result?.Id, Is.EqualTo(expected.Id));
    }

    [Test]
    public async Task GetNextDueScheduleAsync_NoneDue_ReturnsNullAsync()
    {
        AddSchedule("Disabled", enabled: false, nextRunTime: DateTime.UtcNow.AddMinutes(5));
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Scheduling.GetNextDueScheduleAsync();

        Assert.That(result, Is.Null);
    }

    private Schedule AddSchedule(string name, bool enabled, DateTime? nextRunTime)
    {
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsEnabled = enabled,
            NextRunTime = nextRunTime,
            TriggerType = nextRunTime.HasValue ? ScheduleTriggerType.Cron : ScheduleTriggerType.Manual
        };
        _dbContext.Schedules.Add(schedule);
        return schedule;
    }
}
