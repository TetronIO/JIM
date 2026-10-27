// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// How the Queued section lays out its rows (#2549). The Activity tab reads in time order, later at the top, so the
/// next run sits at the bottom of Queued, directly above In progress; a Schedule Execution's tasks sit under one
/// header; and when the section is short of room it keeps the rows nearest In progress, which are the ones about to
/// matter.
/// </summary>
[TestFixture]
public class QueueDisplayRowTests
{
    private static readonly DateTime BaseTime = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);

    private static WorkerTaskHeader Task(string name, int minutesAfterBase, Guid? execution = null, int? step = null,
        WorkerTaskExecutionMode mode = WorkerTaskExecutionMode.Sequential, string? scheduleName = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "Synchronisation",
        Status = WorkerTaskStatus.Queued,
        Timestamp = BaseTime.AddMinutes(minutesAfterBase),
        ScheduleExecutionId = execution,
        ScheduleExecutionName = scheduleName,
        ScheduleStepIndex = step,
        ExecutionMode = mode
    };

    private static List<string> Describe(IEnumerable<QueueDisplayRow> rows) =>
        rows.Select(r => r.IsHeader ? $"[{r.ScheduleName}]" : $"{r.PositionLabel} {r.Task!.Name}").ToList();

    [Test]
    public void Build_NextRunIsTheLastRow_FurthestOutTheFirst()
    {
        var rows = QueueDisplayRow.Build(QueueRunSlot.Plan([Task("First", 0), Task("Second", 1), Task("Third", 2)]));

        Assert.That(Describe(rows), Is.EqualTo(new[] { "3rd Third", "2nd Second", "Next First" }));
    }

    [Test]
    public void Build_ParallelTasks_ShareTheirPositionUnderOneScheduleHeader()
    {
        var execution = Guid.NewGuid();
        var rows = QueueDisplayRow.Build(QueueRunSlot.Plan(
        [
            Task("Manual", 0),
            Task("Export A", 5, execution, 2, WorkerTaskExecutionMode.Parallel, "Regional Sync Cycle"),
            Task("Export B", 5, execution, 2, WorkerTaskExecutionMode.Parallel, "Regional Sync Cycle")
        ]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Describe(rows), Is.EqualTo(new[] { "2nd Manual", "[Regional Sync Cycle]", "Next Export A", "Next Export B" }));
            Assert.That(rows.Where(r => !r.IsHeader && r.PositionLabel == "Next").All(r => r.IsParallel), Is.True);
        }
    }

    [Test]
    public void Build_PositionsPastThird_UseOrdinals()
    {
        var tasks = Enumerable.Range(0, 12).Select(i => Task($"T{i}", i)).ToList();

        var labels = QueueDisplayRow.Build(QueueRunSlot.Plan(tasks)).Select(r => r.PositionLabel).Reverse().ToList();

        Assert.That(labels, Is.EqualTo(new[] { "Next", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th", "9th", "10th", "11th", "12th" }));
    }

    [Test]
    public void Limit_ShortOfRoom_KeepsTheRowsNearestInProgressAndCountsTheRest()
    {
        var rows = QueueDisplayRow.Build(QueueRunSlot.Plan(Enumerable.Range(0, 6).Select(i => Task($"T{i}", i))));

        var (visible, hidden) = QueueDisplayRow.Limit(rows, 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Describe(visible), Is.EqualTo(new[] { "3rd T2", "2nd T1", "Next T0" }));
            Assert.That(hidden, Is.EqualTo(3));
        }
    }

    [Test]
    public void Limit_CutThroughASchedule_KeepsTheHeaderOfTheTasksShown()
    {
        var execution = Guid.NewGuid();
        var rows = QueueDisplayRow.Build(QueueRunSlot.Plan(
            Enumerable.Range(0, 4).Select(i => Task($"Step {i}", 0, execution, i, scheduleName: "Nightly"))));

        var (visible, hidden) = QueueDisplayRow.Limit(rows, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Describe(visible), Is.EqualTo(new[] { "[Nightly]", "2nd Step 1", "Next Step 0" }));
            Assert.That(hidden, Is.EqualTo(2));
        }
    }

    [Test]
    public void Limit_EnoughRoom_ShowsEverything()
    {
        var rows = QueueDisplayRow.Build(QueueRunSlot.Plan([Task("A", 0), Task("B", 1)]));

        var (visible, hidden) = QueueDisplayRow.Limit(rows, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(visible, Is.EqualTo(rows));
            Assert.That(hidden, Is.Zero);
        }
    }
}
