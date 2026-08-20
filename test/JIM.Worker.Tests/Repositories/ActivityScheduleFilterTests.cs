// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Utility;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the Operations > History Schedule attribution filters (#1196): an administrator needs to ask
/// "has this Schedule's LDAP import been flaky all week, or was last night a one-off?", which requires
/// narrowing the Worker Task Activity history to the Activities a Schedule produced, either any Schedule
/// or specific ones. The attribution is denormalised onto the Activity, so the filters are plain indexed
/// predicates over ScheduledByScheduleId rather than a join through Schedule Executions (which a deleted
/// Schedule would silently blank out, on a permanent audit record).
///
/// These cases originally covered the separate Worker Task query; they now run against the unified
/// Activity query with the Worker Task preset supplied by the caller, so the behaviour stays pinned
/// after the two queries were collapsed into one. <see cref="ActivityWorkerTaskPresetTests"/> covers the
/// preset itself.
/// </summary>
[TestFixture]
public class ActivityScheduleFilterTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    private static readonly Guid NightlyScheduleId = Guid.NewGuid();
    private static readonly Guid HourlyScheduleId = Guid.NewGuid();
    private static readonly Guid WeeklyScheduleId = Guid.NewGuid();

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
    public async Task GetActivitiesAsync_InitiatedByScheduleTrue_ReturnsOnlyScheduledActivitiesAsync()
    {
        var nightly = NewActivity(NightlyScheduleId, "Nightly Sync");
        var hourly = NewActivity(HourlyScheduleId, "Hourly Import");
        var manual = NewActivity(null, null);
        _dbContext.Activities.AddRange(nightly, hourly, manual);
        await _dbContext.SaveChangesAsync();

        var result = await QueryHistoryAsync(initiatedBySchedule: true);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { nightly.Id, hourly.Id }),
            "only Activities a Schedule produced were requested");
    }

    [Test]
    public async Task GetActivitiesAsync_InitiatedByScheduleFalse_ReturnsOnlyUnscheduledActivitiesAsync()
    {
        var nightly = NewActivity(NightlyScheduleId, "Nightly Sync");
        var manual = NewActivity(null, null);
        _dbContext.Activities.AddRange(nightly, manual);
        await _dbContext.SaveChangesAsync();

        var result = await QueryHistoryAsync(initiatedBySchedule: false);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { manual.Id }),
            "only Activities no Schedule produced were requested");
    }

    [Test]
    public async Task GetActivitiesAsync_SingleScheduleFilter_ReturnsOnlyThatSchedulesActivitiesAsync()
    {
        var nightly = NewActivity(NightlyScheduleId, "Nightly Sync");
        var hourly = NewActivity(HourlyScheduleId, "Hourly Import");
        var manual = NewActivity(null, null);
        _dbContext.Activities.AddRange(nightly, hourly, manual);
        await _dbContext.SaveChangesAsync();

        var result = await QueryHistoryAsync(scheduleFilter: [NightlyScheduleId]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { nightly.Id }),
            "only the nightly Schedule's Activities were requested");
    }

    [Test]
    public async Task GetActivitiesAsync_MultipleScheduleFilter_ReturnsActivitiesFromAnyOfThemAsync()
    {
        var nightly = NewActivity(NightlyScheduleId, "Nightly Sync");
        var hourly = NewActivity(HourlyScheduleId, "Hourly Import");
        var weekly = NewActivity(WeeklyScheduleId, "Weekly Export");
        var manual = NewActivity(null, null);
        _dbContext.Activities.AddRange(nightly, hourly, weekly, manual);
        await _dbContext.SaveChangesAsync();

        var result = await QueryHistoryAsync(scheduleFilter: [NightlyScheduleId, WeeklyScheduleId]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { nightly.Id, weekly.Id }),
            "the Schedule filter is additive/OR within itself");
    }

    [Test]
    public async Task GetActivitiesAsync_NoScheduleFilters_ReturnsEveryWorkerTaskActivityAsync()
    {
        var nightly = NewActivity(NightlyScheduleId, "Nightly Sync");
        var manual = NewActivity(null, null);
        _dbContext.Activities.AddRange(nightly, manual);
        await _dbContext.SaveChangesAsync();

        var result = await QueryHistoryAsync();

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { nightly.Id, manual.Id }),
            "omitting the new filters must not change the existing unfiltered behaviour");
    }

    [Test]
    public async Task GetActivitiesAsync_EmptyScheduleFilter_ReturnsEveryWorkerTaskActivityAsync()
    {
        var nightly = NewActivity(NightlyScheduleId, "Nightly Sync");
        var manual = NewActivity(null, null);
        _dbContext.Activities.AddRange(nightly, manual);
        await _dbContext.SaveChangesAsync();

        var result = await QueryHistoryAsync(scheduleFilter: []);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { nightly.Id, manual.Id }),
            "an empty Schedule selection means no Schedule filtering, matching the sibling filters");
    }

    [Test]
    public async Task GetWorkerTaskActivityFilterOptionsAsync_ReturnsTheSchedulesPresentInTheHistoryAsync()
    {
        _dbContext.Activities.AddRange(
            NewActivity(NightlyScheduleId, "Nightly Sync"),
            NewActivity(NightlyScheduleId, "Nightly Sync"),
            NewActivity(HourlyScheduleId, "Hourly Import"),
            NewActivity(null, null));
        await _dbContext.SaveChangesAsync();

        var options = await _repository.Activity.GetWorkerTaskActivityFilterOptionsAsync();

        Assert.That(options.Schedules.Select(s => s.Id), Is.EquivalentTo(new[] { NightlyScheduleId, HourlyScheduleId }),
            "the options are projected from the Activity history itself, so every option returns rows");
        Assert.That(options.Schedules.Select(s => s.Name), Is.EquivalentTo(new[] { "Nightly Sync", "Hourly Import" }),
            "the dropdown needs the Schedule name alongside its id");
    }

    [Test]
    public async Task GetWorkerTaskActivityFilterOptionsAsync_RenamedSchedule_ReturnsOneOptionWithTheLatestNameAsync()
    {
        var older = NewActivity(NightlyScheduleId, "Nightly Sync", daysOld: 10);
        var newer = NewActivity(NightlyScheduleId, "Overnight Synchronisation", daysOld: 1);
        _dbContext.Activities.AddRange(older, newer);
        await _dbContext.SaveChangesAsync();

        var options = await _repository.Activity.GetWorkerTaskActivityFilterOptionsAsync();

        Assert.That(options.Schedules.Count, Is.EqualTo(1),
            "a renamed Schedule must yield one option, not two sharing an id (a MudSelect with duplicate ids misbehaves)");
        Assert.That(options.Schedules[0].Name, Is.EqualTo("Overnight Synchronisation"),
            "the most recently recorded name is the one an administrator recognises");
    }

    /// <summary>
    /// Runs the unified Activity query the way Operations > History does: with the Worker Task preset
    /// (top-level Run Profile executions and Connected System Execute/Clear/Delete operations) supplied
    /// by the caller rather than baked into the query.
    /// </summary>
    private async Task<PagedResultSet<Activity>> QueryHistoryAsync(
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null)
    {
        return await _repository.Activity.GetActivitiesAsync(
            page: 1,
            pageSize: 100,
            operationFilter: [ActivityTargetOperationType.Execute, ActivityTargetOperationType.Clear, ActivityTargetOperationType.Delete],
            typeFilter: [ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetType.ConnectedSystem],
            initiatedBySchedule: initiatedBySchedule,
            scheduleFilter: scheduleFilter);
    }

    private static Activity NewActivity(Guid? scheduleId, string? scheduleName, int daysOld = 1) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetContext = "Test Connected System",
        TargetName = "Full Import",
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        Created = DateTime.UtcNow.AddDays(-daysOld),
        ScheduledByScheduleId = scheduleId,
        ScheduledByScheduleName = scheduleName
    };
}
