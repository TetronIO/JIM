// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The two queries that decide whether a cron-triggered Schedule ever runs, and the invariant between them.
/// <para>
/// The scheduler's polling cycle bootstraps missing next run times and then starts whatever is due. Those two
/// steps read the same column, so they must not both claim the same row: a schedule whose next run time has
/// arrived is <b>work to start</b>, not a next run time to recompute. Recomputing it moves the time into the
/// future, and the due check that follows finds nothing. That is not a near miss but total: every cron-triggered
/// Schedule is swallowed on the exact cycle it becomes due, on every cycle, so none of them ever fires.
/// </para>
/// <para>
/// Bootstrapping is therefore restricted to rows that have no next run time at all. Advancing one that has run
/// belongs to the code that started it, which already does it.
/// </para>
/// </summary>
[TestFixture]
public class SchedulingRepositoryDueScheduleTests
{
    private List<Schedule> _schedulesData = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _schedulesData = new List<Schedule>();
    }

    [TearDown]
    public void TearDown() => _repository?.Dispose();

    [Test]
    public async Task GetSchedulesForNextRunCalculationAsync_ScheduleIsDue_LeavesItForTheDueCheckAsync()
    {
        var due = NewSchedule("Due now", nextRunTime: DateTime.UtcNow.AddMinutes(-1));
        BuildRepository();

        var forCalculation = await _repository.Scheduling.GetSchedulesForNextRunCalculationAsync();

        Assert.That(forCalculation.Select(s => s.Id), Does.Not.Contain(due.Id),
            "a schedule whose next run time has arrived is work to start; recomputing it here moves the time " +
            "into the future and the due check that follows in the same cycle finds nothing");
    }

    [Test]
    public async Task GetSchedulesForNextRunCalculationAsync_ScheduleHasNoNextRunTime_BootstrapsItAsync()
    {
        var bootstrapping = NewSchedule("Never calculated", nextRunTime: null);
        BuildRepository();

        var forCalculation = await _repository.Scheduling.GetSchedulesForNextRunCalculationAsync();

        Assert.That(forCalculation.Select(s => s.Id), Does.Contain(bootstrapping.Id),
            "a newly created or newly enabled schedule has no next run time until this pass gives it one");
    }

    [Test]
    public async Task GetSchedulesForNextRunCalculationAsync_ManualAndDisabledSchedules_AreLeftAloneAsync()
    {
        var manual = NewSchedule("Manual", nextRunTime: null, triggerType: ScheduleTriggerType.Manual);
        var disabled = NewSchedule("Disabled", nextRunTime: null, isEnabled: false);
        BuildRepository();

        var forCalculation = await _repository.Scheduling.GetSchedulesForNextRunCalculationAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(forCalculation.Select(s => s.Id), Does.Not.Contain(manual.Id),
                "a manually triggered schedule has no cron to compute a next run time from");
            Assert.That(forCalculation.Select(s => s.Id), Does.Not.Contain(disabled.Id),
                "a disabled schedule does not run, so it needs no next run time");
        }
    }

    [Test]
    public async Task GetDueSchedulesAsync_ReturnsOnlyEnabledSchedulesWhoseTimeHasArrivedAsync()
    {
        var due = NewSchedule("Due now", nextRunTime: DateTime.UtcNow.AddMinutes(-1));
        var future = NewSchedule("Not yet", nextRunTime: DateTime.UtcNow.AddHours(1));
        var disabled = NewSchedule("Disabled", nextRunTime: DateTime.UtcNow.AddMinutes(-1), isEnabled: false);
        var uncalculated = NewSchedule("Never calculated", nextRunTime: null);
        BuildRepository();

        var dueSchedules = await _repository.Scheduling.GetDueSchedulesAsync(DateTime.UtcNow);

        Assert.That(dueSchedules.Select(s => s.Id), Is.EqualTo(new[] { due.Id }),
            $"only {due.Name} is enabled with a next run time that has arrived; " +
            $"{future.Name}, {disabled.Name} and {uncalculated.Name} are not");
    }

    /// <summary>
    /// The invariant the two queries exist under: between them they must claim a due schedule exactly once, and
    /// it must be the due check that claims it. A test on either query alone would pass while the pair was
    /// broken, which is how this went unnoticed.
    /// </summary>
    [Test]
    public async Task DueSchedule_IsClaimedByTheDueCheckAndNotByTheNextRunTimeBootstrapAsync()
    {
        var due = NewSchedule("Due now", nextRunTime: DateTime.UtcNow.AddMinutes(-1));
        BuildRepository();

        var forCalculation = await _repository.Scheduling.GetSchedulesForNextRunCalculationAsync();
        var dueSchedules = await _repository.Scheduling.GetDueSchedulesAsync(DateTime.UtcNow);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dueSchedules.Select(s => s.Id), Does.Contain(due.Id));
            Assert.That(forCalculation.Select(s => s.Id), Does.Not.Contain(due.Id));
        }
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private Schedule NewSchedule(
        string name,
        DateTime? nextRunTime,
        bool isEnabled = true,
        ScheduleTriggerType triggerType = ScheduleTriggerType.Cron)
    {
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsEnabled = isEnabled,
            TriggerType = triggerType,
            CronExpression = triggerType == ScheduleTriggerType.Cron ? "0 * * * *" : null,
            NextRunTime = nextRunTime,
            Steps = new List<ScheduleStep>()
        };

        _schedulesData.Add(schedule);
        return schedule;
    }

    private void BuildRepository()
    {
        var mockDbSet = _schedulesData.BuildMockDbSet();
        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(m => m.Schedules).Returns(mockDbSet.Object);
        _repository = new PostgresDataRepository(mockDbContext.Object);
    }
}
