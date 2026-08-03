// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Pins the behaviour of the unified Activity query when it is handed the Worker Task preset.
///
/// JIM used to carry two Activity queries: a general one behind the Activities list, and a second,
/// Worker-Task-only one behind Operations > History. They drifted, which is why they were collapsed into
/// one. The Worker Task query was the general query plus a fixed preset (top-level Activities only, whose
/// Target Type is Connected System Run Profile or Connected System, and whose operation is Execute, Clear
/// or Delete), so Operations > History now passes that preset explicitly.
///
/// These tests are the regression net for the collapse: they seed a deliberately mixed Activity history
/// (Run Profile executions, Connected System operations, configuration changes and a child Activity) and
/// assert the preset returns exactly the rows the deleted method returned, for every filter it supported.
/// </summary>
[TestFixture]
public class ActivityWorkerTaskPresetTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    private static readonly Guid NightlyScheduleId = Guid.NewGuid();
    private static readonly Guid HourlyScheduleId = Guid.NewGuid();

    // The Worker Task preset: what Operations > History passes to the unified query, and what the deleted
    // GetWorkerTaskActivitiesAsync hard-coded. Note that ExampleDataTemplate and HistoryRetentionCleanup
    // are deliberately not Worker Task target types.
    private static readonly ActivityTargetType[] WorkerTaskTargetTypes =
    [
        ActivityTargetType.ConnectedSystemRunProfile,
        ActivityTargetType.ConnectedSystem
    ];

    private static readonly ActivityTargetOperationType[] WorkerTaskOperations =
    [
        ActivityTargetOperationType.Execute,
        ActivityTargetOperationType.Clear,
        ActivityTargetOperationType.Delete
    ];

    // The seeded history. Named fields rather than locals so every test reads against the same fixture.
    private Activity _fullImport = null!;
    private Activity _deltaImport = null!;
    private Activity _fullExport = null!;
    private Activity _clearConnectedSystem = null!;
    private Activity _deleteConnectedSystem = null!;
    private Activity _syncRuleUpdate = null!;
    private Activity _connectedSystemUpdate = null!;
    private Activity _runProfileCreate = null!;
    private Activity _childActivity = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);

        // Worker Task Activities: what Operations > History exists to show.
        _fullImport = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute,
            "Contoso AD", "Full Import", ActivityStatus.Complete, "Alice Adams", NightlyScheduleId, "Nightly Sync", daysOld: 5);
        _deltaImport = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute,
            "Contoso AD", "Delta Import", ActivityStatus.FailedWithError, "System", HourlyScheduleId, "Hourly Import", daysOld: 4);
        _fullExport = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute,
            "Fabrikam HR", "Full Export", ActivityStatus.Complete, "Bob Brown", null, null, daysOld: 3);
        _clearConnectedSystem = NewActivity(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Clear,
            "Contoso AD", "Contoso AD", ActivityStatus.Complete, "Alice Adams", null, null, daysOld: 2);
        _deleteConnectedSystem = NewActivity(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Delete,
            "Fabrikam HR", "Fabrikam HR", ActivityStatus.Complete, "Bob Brown", null, null, daysOld: 1);

        // Not Worker Task Activities: the wrong Target Type, or the wrong operation on a right Target Type.
        _syncRuleUpdate = NewActivity(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Update,
            "Contoso AD", "Inbound Users", ActivityStatus.Complete, "Alice Adams", null, null, daysOld: 6);
        _connectedSystemUpdate = NewActivity(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Update,
            "Contoso AD", "Contoso AD", ActivityStatus.Complete, "Alice Adams", null, null, daysOld: 7);
        _runProfileCreate = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Create,
            "Contoso AD", "Full Import", ActivityStatus.Complete, "Alice Adams", null, null, daysOld: 8);

        // A child Activity of a Worker Task Activity: the history lists parents only.
        _childActivity = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute,
            "Contoso AD", "Full Import", ActivityStatus.Complete, "Alice Adams", NightlyScheduleId, "Nightly Sync", daysOld: 5);
        _childActivity.ParentActivityId = _fullImport.Id;

        _dbContext.Activities.AddRange(_fullImport, _deltaImport, _fullExport, _clearConnectedSystem, _deleteConnectedSystem,
            _syncRuleUpdate, _connectedSystemUpdate, _runProfileCreate, _childActivity);
        await _dbContext.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetOnly_ReturnsExactlyTheWorkerTaskActivitiesAsync()
    {
        var result = await QueryWithPresetAsync();

        Assert.That(result, Is.EquivalentTo(new[]
            {
                _fullImport.Id, _deltaImport.Id, _fullExport.Id, _clearConnectedSystem.Id, _deleteConnectedSystem.Id
            }),
            "the preset selects top-level Run Profile executions and Connected System Execute/Clear/Delete operations, "
            + "and excludes configuration changes and child Activities");
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithConnectedSystemFilter_ReturnsOnlyThatSystemsActivitiesAsync()
    {
        var result = await QueryWithPresetAsync(connectedSystemFilter: ["Contoso AD"]);

        Assert.That(result, Is.EquivalentTo(new[] { _fullImport.Id, _deltaImport.Id, _clearConnectedSystem.Id }),
            "the Connected System filter matches on the Activity's Target Context");
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithMultipleConnectedSystems_IsAdditiveAsync()
    {
        var result = await QueryWithPresetAsync(connectedSystemFilter: ["Contoso AD", "Fabrikam HR"]);

        Assert.That(result, Is.EquivalentTo(new[]
            {
                _fullImport.Id, _deltaImport.Id, _fullExport.Id, _clearConnectedSystem.Id, _deleteConnectedSystem.Id
            }),
            "several Connected Systems combine with OR within the filter");
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithRunProfileFilter_ReturnsOnlyThatRunProfilesActivitiesAsync()
    {
        var result = await QueryWithPresetAsync(runProfileFilter: ["Full Import", "Full Export"]);

        Assert.That(result, Is.EquivalentTo(new[] { _fullImport.Id, _fullExport.Id }),
            "the Run Profile filter matches on the Activity's Target Name");
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithStatusFilter_ReturnsOnlyThatStatusAsync()
    {
        var result = await QueryWithPresetAsync(statusFilter: [ActivityStatus.FailedWithError]);

        Assert.That(result, Is.EquivalentTo(new[] { _deltaImport.Id }));
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithInitiatedByFilter_MatchesPartOfTheNameCaseInsensitivelyAsync()
    {
        var result = await QueryWithPresetAsync(initiatedByFilter: "alice");

        Assert.That(result, Is.EquivalentTo(new[] { _fullImport.Id, _clearConnectedSystem.Id }),
            "the initiator filter is a case-insensitive Contains on the initiator's name");
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithHasChildActivitiesTrue_ReturnsOnlyParentsAsync()
    {
        var result = await QueryWithPresetAsync(hasChildActivities: true);

        Assert.That(result, Is.EquivalentTo(new[] { _fullImport.Id }),
            "only the Full Import execution has a child Activity");
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithHasChildActivitiesFalse_ExcludesParentsAsync()
    {
        var result = await QueryWithPresetAsync(hasChildActivities: false);

        Assert.That(result, Is.EquivalentTo(new[]
            {
                _deltaImport.Id, _fullExport.Id, _clearConnectedSystem.Id, _deleteConnectedSystem.Id
            }));
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithInitiatedByScheduleTrue_ReturnsOnlyScheduledActivitiesAsync()
    {
        var result = await QueryWithPresetAsync(initiatedBySchedule: true);

        Assert.That(result, Is.EquivalentTo(new[] { _fullImport.Id, _deltaImport.Id }));
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithInitiatedByScheduleFalse_ReturnsOnlyUnscheduledActivitiesAsync()
    {
        var result = await QueryWithPresetAsync(initiatedBySchedule: false);

        Assert.That(result, Is.EquivalentTo(new[]
            {
                _fullExport.Id, _clearConnectedSystem.Id, _deleteConnectedSystem.Id
            }));
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithScheduleFilter_ReturnsOnlyThatSchedulesActivitiesAsync()
    {
        var result = await QueryWithPresetAsync(scheduleFilter: [NightlyScheduleId]);

        Assert.That(result, Is.EquivalentTo(new[] { _fullImport.Id }));
    }

    [Test]
    public async Task GetActivitiesAsync_WorkerTaskPresetWithCombinedFilters_CombinesThemWithAndAsync()
    {
        var result = await QueryWithPresetAsync(
            connectedSystemFilter: ["Contoso AD"],
            statusFilter: [ActivityStatus.FailedWithError],
            initiatedBySchedule: true);

        Assert.That(result, Is.EquivalentTo(new[] { _deltaImport.Id }),
            "separate filters narrow each other, which is what makes 'has this Schedule's import been failing' answerable");
    }

    [Test]
    public async Task GetActivitiesAsync_WithoutTheWorkerTaskPreset_AlsoReturnsConfigurationActivitiesAsync()
    {
        // The counterpart assertion: the preset is the caller's, not the query's. Without it the same query is
        // the general Activities list, which is the whole point of collapsing the two methods into one.
        var result = await _repository.Activity.GetActivitiesAsync(page: 1, pageSize: 100);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[]
            {
                _fullImport.Id, _deltaImport.Id, _fullExport.Id, _clearConnectedSystem.Id, _deleteConnectedSystem.Id,
                _syncRuleUpdate.Id, _connectedSystemUpdate.Id, _runProfileCreate.Id
            }),
            "every top-level Activity is returned when no preset is supplied; only child Activities are excluded");
    }

    private async Task<List<Guid>> QueryWithPresetAsync(
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null)
    {
        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1,
            pageSize: 100,
            operationFilter: WorkerTaskOperations,
            typeFilter: WorkerTaskTargetTypes,
            statusFilter: statusFilter,
            hasChildActivities: hasChildActivities,
            connectedSystemFilter: connectedSystemFilter,
            runProfileFilter: runProfileFilter,
            initiatedByFilter: initiatedByFilter,
            initiatedBySchedule: initiatedBySchedule,
            scheduleFilter: scheduleFilter);

        return result.Results.Select(a => a.Id).ToList();
    }

    private static Activity NewActivity(
        ActivityTargetType targetType,
        ActivityTargetOperationType operationType,
        string targetContext,
        string targetName,
        ActivityStatus status,
        string initiatedByName,
        Guid? scheduleId,
        string? scheduleName,
        int daysOld) => new()
        {
            Id = Guid.NewGuid(),
            TargetType = targetType,
            TargetOperationType = operationType,
            TargetContext = targetContext,
            TargetName = targetName,
            Status = status,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedByName = initiatedByName,
            Created = DateTime.UtcNow.AddDays(-daysOld),
            ScheduledByScheduleId = scheduleId,
            ScheduledByScheduleName = scheduleName
        };
}
