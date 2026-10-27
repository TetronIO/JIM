// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Queued section's estimated start times (#2549): each slot starts when the work ahead of it is expected to
/// finish, taking each Run Profile's typical (median) duration. An estimate is only shown where every input is known;
/// past the first unknown, a time would be a guess presented as a forecast, so the rest are blank.
/// </summary>
[TestFixture]
public class QueueStartEstimatorTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 14, 32, 0, DateTimeKind.Utc);

    private static WorkerTaskHeader Task(int? runProfileId, Guid? execution = null, int? step = null,
        WorkerTaskExecutionMode mode = WorkerTaskExecutionMode.Sequential, int minutesAfterBase = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Run Profile {runProfileId}",
        Type = "Synchronisation",
        Status = WorkerTaskStatus.Queued,
        Timestamp = Now.AddMinutes(minutesAfterBase),
        ConnectedSystemRunProfileId = runProfileId,
        ScheduleExecutionId = execution,
        ScheduleStepIndex = step,
        ExecutionMode = mode
    };

    private static readonly Dictionary<int, TimeSpan> Medians = new()
    {
        [1] = TimeSpan.FromMinutes(10),
        [2] = TimeSpan.FromMinutes(30),
        [3] = TimeSpan.FromMinutes(5)
    };

    [Test]
    public void Estimate_NothingRunning_FirstSlotStartsNowAndTheNextAfterIt()
    {
        var first = Task(1, minutesAfterBase: 0);
        var second = Task(2, minutesAfterBase: 1);

        var starts = QueueStartEstimator.Estimate(QueueRunSlot.Plan([first, second]), TimeSpan.Zero, Medians, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(starts[first.Id], Is.EqualTo(Now));
            Assert.That(starts[second.Id], Is.EqualTo(Now.AddMinutes(10)));
        }
    }

    [Test]
    public void Estimate_WorkRunning_FirstSlotStartsWhenItIsExpectedToFinish()
    {
        var first = Task(1);

        var starts = QueueStartEstimator.Estimate(QueueRunSlot.Plan([first]), TimeSpan.FromMinutes(7), Medians, Now);

        Assert.That(starts[first.Id], Is.EqualTo(Now.AddMinutes(7)));
    }

    [Test]
    public void Estimate_ParallelSlot_LastsAsLongAsItsSlowestTask()
    {
        var execution = Guid.NewGuid();
        var quick = Task(1, execution, 0, WorkerTaskExecutionMode.Parallel);
        var slow = Task(2, execution, 0, WorkerTaskExecutionMode.Parallel);
        var after = Task(3);

        var starts = QueueStartEstimator.Estimate(QueueRunSlot.Plan([quick, slow, after]), TimeSpan.Zero, Medians, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(starts[quick.Id], Is.EqualTo(Now));
            Assert.That(starts[slow.Id], Is.EqualTo(Now));
            Assert.That(starts[after.Id], Is.EqualTo(Now.AddMinutes(30)));
        }
    }

    [Test]
    public void Estimate_RunningWorkWithUnknownRemainingTime_LeavesEveryEstimateBlank()
    {
        var first = Task(1);

        var starts = QueueStartEstimator.Estimate(QueueRunSlot.Plan([first]), null, Medians, Now);

        Assert.That(starts[first.Id], Is.Null);
    }

    [Test]
    public void Estimate_RunProfileWithNoHistory_StillHasAStartButBlanksEverythingAfterIt()
    {
        var neverRun = Task(99, minutesAfterBase: 0);
        var after = Task(1, minutesAfterBase: 1);

        var starts = QueueStartEstimator.Estimate(QueueRunSlot.Plan([neverRun, after]), TimeSpan.Zero, Medians, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(starts[neverRun.Id], Is.EqualTo(Now));
            Assert.That(starts[after.Id], Is.Null);
        }
    }

    [Test]
    public void Remaining_TypicalDurationKnown_IsWhatIsLeftOfIt()
    {
        var remaining = QueueStartEstimator.Remaining(Now.AddMinutes(-7), TimeSpan.FromMinutes(30), stepSecondsRemaining: null, Now);

        Assert.That(remaining, Is.EqualTo(TimeSpan.FromMinutes(23)));
    }

    [Test]
    public void Remaining_CurrentStepAloneNeedsLonger_TakesTheStepEstimate()
    {
        // The step estimate covers only the step running now, so it is a floor, never the whole answer; but a run
        // going slower than usual should not be promised to finish before its current step can.
        var remaining = QueueStartEstimator.Remaining(Now.AddMinutes(-7), TimeSpan.FromMinutes(30), stepSecondsRemaining: 60 * 40, Now);

        Assert.That(remaining, Is.EqualTo(TimeSpan.FromMinutes(40)));
    }

    [Test]
    public void Remaining_RunOverrunningItsTypicalDuration_FallsBackToTheStepEstimate()
    {
        var remaining = QueueStartEstimator.Remaining(Now.AddMinutes(-50), TimeSpan.FromMinutes(30), stepSecondsRemaining: 120, Now);

        Assert.That(remaining, Is.EqualTo(TimeSpan.FromMinutes(2)));
    }

    [Test]
    public void Remaining_RunOverrunningWithNoStepEstimate_IsUnknown()
    {
        var remaining = QueueStartEstimator.Remaining(Now.AddMinutes(-50), TimeSpan.FromMinutes(30), stepSecondsRemaining: null, Now);

        Assert.That(remaining, Is.Null);
    }

    [Test]
    public void Remaining_NoTypicalDuration_IsUnknownEvenWithAStepEstimate()
    {
        // A step estimate alone would understate a run with steps still to come.
        var remaining = QueueStartEstimator.Remaining(Now.AddMinutes(-7), null, stepSecondsRemaining: 600, Now);

        Assert.That(remaining, Is.Null);
    }

    [Test]
    public void Remaining_NotStarted_IsUnknown()
    {
        Assert.That(QueueStartEstimator.Remaining(null, TimeSpan.FromMinutes(30), null, Now), Is.Null);
    }

    [Test]
    public void LongestRemaining_SeveralRunning_IsTheLongestAndUnknownIfAnyIs()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(QueueStartEstimator.LongestRemaining([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(9)]), Is.EqualTo(TimeSpan.FromMinutes(9)));
            Assert.That(QueueStartEstimator.LongestRemaining([TimeSpan.FromMinutes(5), null]), Is.Null);
            Assert.That(QueueStartEstimator.LongestRemaining([]), Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public void Estimate_TaskThatIsNotARunProfile_BlanksEverythingAfterIt()
    {
        var clear = Task(null, minutesAfterBase: 0);
        var after = Task(1, minutesAfterBase: 1);

        var starts = QueueStartEstimator.Estimate(QueueRunSlot.Plan([clear, after]), TimeSpan.Zero, Medians, Now);

        Assert.That(starts[after.Id], Is.Null);
    }
}
