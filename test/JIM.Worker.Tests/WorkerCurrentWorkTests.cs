// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Tasking;

namespace JIM.Worker.Tests;

/// <summary>
/// The wording the Worker puts in its heartbeat's CurrentWork: what an administrator reads on the Operations page
/// while a task is running.
/// </summary>
[TestFixture]
public class WorkerCurrentWorkTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private static TaskTask InFlight(string? description, DateTime startedAt) =>
        new(Guid.NewGuid(), Task.CompletedTask, new CancellationTokenSource(), description, startedAt);

    [Test]
    public void DescribeTask_SynchronisationTask_RunProfileThenConnectedSystem()
    {
        var task = new SynchronisationWorkerTask(1, 2)
        {
            Activity = new Activity { TargetName = "Full Import", TargetContext = "Corporate Directory" }
        };

        Assert.That(WorkerCurrentWork.DescribeTask(task), Is.EqualTo("Full Import: Corporate Directory"));
    }

    [Test]
    public void DescribeTask_SynchronisationTaskWithoutNames_FallsBackToTheKind()
    {
        var task = new SynchronisationWorkerTask(1, 2) { Activity = new Activity() };

        Assert.That(WorkerCurrentWork.DescribeTask(task), Is.EqualTo("Synchronisation"));
    }

    [Test]
    public void DescribeTask_OtherTaskWithTarget_KindThenTarget()
    {
        var task = new DeleteSyncRuleWorkerTask
        {
            Activity = new Activity { TargetName = "Users Inbound", TargetContext = "Corporate Directory" }
        };

        Assert.That(WorkerCurrentWork.DescribeTask(task), Is.EqualTo("Synchronisation Rule deletion: Users Inbound (Corporate Directory)"));
    }

    [Test]
    public void DescribeTask_TaskWhoseTargetNameIsItsKind_KindOnly()
    {
        var task = new HistoryRetentionCleanupWorkerTask
        {
            Activity = new Activity { TargetName = "History Retention Cleanup" }
        };

        Assert.That(WorkerCurrentWork.DescribeTask(task), Is.EqualTo("History retention cleanup"));
    }

    [Test]
    public void DescribeTask_UnknownKind_WordsFromTheTypeName()
    {
        var task = new AuxiliaryClassDiscoveryWorkerTask { Activity = new Activity() };

        Assert.That(WorkerCurrentWork.DescribeTask(task), Is.EqualTo("Auxiliary class discovery"));
    }

    [Test]
    public void Describe_NoTasks_Idle()
    {
        var (currentWork, startedAt) = WorkerCurrentWork.Describe([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentWork, Is.Null);
            Assert.That(startedAt, Is.Null);
        }
    }

    [Test]
    public void Describe_SeveralTasks_JoinsThemAndTakesTheEarliestStart()
    {
        var tasks = new[]
        {
            InFlight("Full Import: HR", T0.AddMinutes(-1)),
            InFlight("Full Import: Corporate Directory", T0.AddMinutes(-5))
        };

        var (currentWork, startedAt) = WorkerCurrentWork.Describe(tasks);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentWork, Is.EqualTo("Full Import: HR; Full Import: Corporate Directory"));
            Assert.That(startedAt, Is.EqualTo(T0.AddMinutes(-5)));
        }
    }

    [Test]
    public void Describe_VeryLongDescriptions_TruncatedToTheColumnLength()
    {
        var tasks = Enumerable.Range(0, 20).Select(i => InFlight($"Full Import: {new string('x', 60)} {i}", T0)).ToArray();

        var (currentWork, _) = WorkerCurrentWork.Describe(tasks);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentWork, Has.Length.LessThanOrEqualTo(WorkerCurrentWork.MaxLength));
            Assert.That(currentWork, Does.EndWith("..."));
        }
    }
}
