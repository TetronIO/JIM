// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Operations;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Operations page's demo mode (<c>?d=true</c>, Development only) exists to show the page fully populated, so a
/// layout can be judged against every shape it draws. These tests hold the demo data to that: a new outcome counter,
/// status or trigger pattern fails here until the demo shows it, rather than quietly never being seen.
/// </summary>
[TestFixture]
public class OperationsDemoDataTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Counters the Completed list never renders: data generation is not a Worker Task the list shows.
    /// </summary>
    private static readonly string[] CountersNotOnTheCompletedList = [nameof(Activity.TotalCreated)];

    [Test]
    public void CompletedDemo_EveryOutcomeCounterTheListRenders_IsShownByAtLeastOneActivity()
    {
        var activities = OperationsCompletedDemoData.Build(Now);
        var counters = typeof(Activity).GetProperties()
            .Where(p => p.Name.StartsWith("Total", StringComparison.Ordinal) && p.PropertyType == typeof(int))
            .Where(p => !CountersNotOnTheCompletedList.Contains(p.Name));

        var missing = counters.Where(p => activities.All(a => (int)p.GetValue(a)! == 0)).Select(p => p.Name).ToList();

        Assert.That(missing, Is.Empty, "the demo shows no Activity with these outcomes");
    }

    [Test]
    public void CompletedDemo_EveryFinishedStatus_IsShown()
    {
        var shown = OperationsCompletedDemoData.Build(Now).Select(a => a.Status).ToHashSet();
        var finished = Enum.GetValues<ActivityStatus>().Where(s => s is not ActivityStatus.NotSet and not ActivityStatus.InProgress);

        Assert.That(finished.Where(s => !shown.Contains(s)), Is.Empty);
    }

    [Test]
    public void CompletedDemo_EveryRunTypeAndConnectedSystemOperation_IsShown()
    {
        var activities = OperationsCompletedDemoData.Build(Now);
        var runTypes = activities.Select(a => a.ConnectedSystemRunType).ToHashSet();
        var expectedRunTypes = Enum.GetValues<ConnectedSystemRunType>().Where(t => t != ConnectedSystemRunType.NotSet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(expectedRunTypes.Where(t => !runTypes.Contains(t)), Is.Empty);
            Assert.That(activities.Any(a => a.TargetOperationType == ActivityTargetOperationType.Clear), Is.True,
                "a Connected System's objects being cleared");
            Assert.That(activities.Any(a => a is { TargetType: ActivityTargetType.ConnectedSystem, TargetOperationType: ActivityTargetOperationType.Delete }),
                Is.True, "a Connected System being deleted");
        }
    }

    [Test]
    public void CompletedDemo_EveryKindOfInitiator_IsShown()
    {
        var activities = OperationsCompletedDemoData.Build(Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activities.Any(a => a.InitiatedByType == ActivityInitiatorType.User), Is.True, "a person");
            Assert.That(activities.Any(a => a.InitiatedByType == ActivityInitiatorType.ApiKey), Is.True, "an API key");
            Assert.That(activities.Any(a => a.InitiatedByType == ActivityInitiatorType.System && a.ScheduledByScheduleName is null),
                Is.True, "JIM itself");
            Assert.That(activities.Any(a => a.ScheduledByScheduleName is not null && a.ScheduleExecutionId is not null),
                Is.True, "a Schedule");
        }
    }

    [Test]
    public void CompletedDemo_ShowsNoChangesAndALongName_AndEveryActivityIsAWorkerTask()
    {
        var activities = OperationsCompletedDemoData.Build(Now);
        bool HasOutcome(Activity a) => typeof(Activity).GetProperties()
            .Where(p => p.Name.StartsWith("Total", StringComparison.Ordinal) && p.PropertyType == typeof(int))
            .Any(p => (int)p.GetValue(a)! > 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activities.Any(a => !HasOutcome(a)), Is.True, "a run with no changes");
            Assert.That(activities.Any(a => (a.TargetContext?.Length ?? 0) >= 40), Is.True, "a long Connected System name");
            Assert.That(activities.Select(a => a.Id).Distinct().Count(), Is.EqualTo(activities.Count), "ids are unique");
            Assert.That(activities.All(a => a.TargetType is ActivityTargetType.ConnectedSystemRunProfile or ActivityTargetType.ConnectedSystem),
                Is.True, "the Completed list only shows Worker Tasks");
        }
    }

    [Test]
    public void SchedulesDemo_EveryLastOutcomeStatusAndNeverRun_IsShown()
    {
        var schedules = OperationsSchedulesDemoData.Build(Now);
        var shown = schedules.Where(s => s.LastExecutionStatus.HasValue).Select(s => s.LastExecutionStatus!.Value).ToHashSet();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enum.GetValues<ScheduleExecutionStatus>().Where(s => !shown.Contains(s)), Is.Empty);
            Assert.That(schedules.Any(s => s.LastExecutionStatus is null), Is.True, "a Schedule that has never run");
        }
    }

    [Test]
    public void SchedulesDemo_EveryTriggerAndPattern_IsShown()
    {
        var schedules = OperationsSchedulesDemoData.Build(Now);
        var cron = schedules.Where(s => s.TriggerType == ScheduleTriggerType.Cron).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedules.Any(s => s.TriggerType == ScheduleTriggerType.Manual), Is.True, "a manual Schedule");
            Assert.That(Enum.GetValues<SchedulePatternType>().Where(p => cron.All(s => s.PatternType != p)), Is.Empty);
            Assert.That(schedules.Any(s => !s.IsEnabled), Is.True, "a disabled Schedule");
            Assert.That(schedules.Any(s => string.IsNullOrEmpty(s.Description)), Is.True, "a Schedule with no description");
            Assert.That(schedules.Any(s => (s.Description?.Length ?? 0) >= 150), Is.True, "a long description");
            Assert.That(schedules.Select(s => s.Id).Distinct().Count(), Is.EqualTo(schedules.Count), "ids are unique");
        }
    }

    [Test]
    public void HealthDemo_EveryStatusAndService_IsShown()
    {
        var report = OperationsHealthDemoData.Build(Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enum.GetValues<ServiceHealthStatus>().Where(s => report.Services.All(h => h.Status != s)), Is.Empty);
            Assert.That(Enum.GetValues<JimService>().Where(s => report.Services.All(h => h.Service != s)), Is.Empty);
            Assert.That(report.Overall, Is.EqualTo(report.Services.Max(h => h.Status)), "the verdict is the worst service's");
        }
    }
}
