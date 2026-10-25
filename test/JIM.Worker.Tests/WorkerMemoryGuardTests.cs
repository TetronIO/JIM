// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;
using JIM.Worker;

namespace JIM.Worker.Tests;

/// <summary>
/// When the Worker's memory guard warns, stops its running work, or gives up waiting and exits: a warning at 85% in
/// use whoever holds the memory; a stop at 92% only when the Worker holds most of it; and an exit when the stop has
/// not taken effect, so the reason is recorded before the operating system kills the process without a word.
/// </summary>
[TestFixture]
public class WorkerMemoryGuardTests
{
    private const long Gb = 1_000_000_000;
    private static readonly DateTime T0 = new(2026, 10, 25, 2, 43, 0, DateTimeKind.Utc);

    /// <summary>A reading of a 100 GB host with <paramref name="usedGb"/> in use, <paramref name="workerGb"/> of it by the Worker.</summary>
    private static MemoryReading Reading(long usedGb, long workerGb) =>
        new() { ProcessBytes = workerGb * Gb, TotalBytes = 100 * Gb, AvailableBytes = (100 - usedGb) * Gb };

    [Test]
    public void Evaluate_BelowTheWarningShare_DoesNothing()
    {
        Assert.That(new WorkerMemoryGuard().Evaluate(Reading(84, 70), tasksRunning: true, T0), Is.EqualTo(MemoryGuardAction.None));
    }

    [Test]
    public void Evaluate_AtTheWarningShare_WarnsOnceWhileTheTasksRun()
    {
        var guard = new WorkerMemoryGuard();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(guard.Evaluate(Reading(85, 10), tasksRunning: true, T0), Is.EqualTo(MemoryGuardAction.Warn), "whoever holds the memory, the run is at risk");
            Assert.That(guard.Evaluate(Reading(88, 10), tasksRunning: true, T0.AddSeconds(1)), Is.EqualTo(MemoryGuardAction.None), "one warning per run, not one a second");
        }
    }

    [Test]
    public void Evaluate_NoTasksRunning_DoesNothingAndForgetsTheLastWarning()
    {
        var guard = new WorkerMemoryGuard();
        guard.Evaluate(Reading(86, 10), tasksRunning: true, T0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(guard.Evaluate(Reading(99, 90), tasksRunning: false, T0.AddSeconds(1)), Is.EqualTo(MemoryGuardAction.None), "with nothing running there is nothing to warn or stop");
            Assert.That(guard.Evaluate(Reading(86, 10), tasksRunning: true, T0.AddSeconds(2)), Is.EqualTo(MemoryGuardAction.Warn), "the next run gets its own warning");
        }
    }

    [Test]
    public void Evaluate_AtTheStopShareWithTheWorkerHoldingMost_Stops()
    {
        Assert.That(new WorkerMemoryGuard().Evaluate(Reading(92, 60), tasksRunning: true, T0), Is.EqualTo(MemoryGuardAction.Stop));
    }

    [Test]
    public void Evaluate_AtTheStopShareWithSomethingElseHoldingMost_OnlyWarns()
    {
        // Stopping a synchronisation would not free what something else holds, and the operating system would not
        // pick the Worker to kill.
        Assert.That(new WorkerMemoryGuard().Evaluate(Reading(95, 40), tasksRunning: true, T0), Is.EqualTo(MemoryGuardAction.Warn));
    }

    [Test]
    public void Evaluate_AfterAStop_WaitsForTheTasksToEnd()
    {
        var guard = new WorkerMemoryGuard();
        guard.Evaluate(Reading(93, 60), tasksRunning: true, T0);

        Assert.That(guard.Evaluate(Reading(93, 60), tasksRunning: true, T0.AddSeconds(10)), Is.EqualTo(MemoryGuardAction.None), "a cancelled task needs a moment to wind down");
    }

    [Test]
    public void Evaluate_StopNotTakenEffectWithinTheGrace_Aborts()
    {
        var guard = new WorkerMemoryGuard();
        guard.Evaluate(Reading(93, 60), tasksRunning: true, T0);

        Assert.That(guard.Evaluate(Reading(93, 60), tasksRunning: true, T0 + WorkerMemoryGuard.StopGrace), Is.EqualTo(MemoryGuardAction.Abort));
    }

    [Test]
    public void Evaluate_StopTakingEffectWithinTheGrace_DoesNotAbort()
    {
        var guard = new WorkerMemoryGuard();
        guard.Evaluate(Reading(93, 60), tasksRunning: true, T0);

        Assert.That(guard.Evaluate(Reading(80, 40), tasksRunning: true, T0 + WorkerMemoryGuard.StopGrace), Is.EqualTo(MemoryGuardAction.None), "memory is coming back as the tasks unwind");
    }

    [Test]
    public void Evaluate_StillGrowingPastTheAbortShareAfterAStop_AbortsWithoutWaitingOutTheGrace()
    {
        // Growth like the 2026-10-25 failure (0.35 GB a second inside one blocking directory call) reaches the limit
        // long before a 30-second grace ends; the reason must be recorded while there is still memory to record it.
        var guard = new WorkerMemoryGuard();
        guard.Evaluate(Reading(93, 60), tasksRunning: true, T0);

        Assert.That(guard.Evaluate(Reading(97, 64), tasksRunning: true, T0.AddSeconds(2)), Is.EqualTo(MemoryGuardAction.Abort));
    }

    [Test]
    public void Evaluate_AfterTheStoppedTasksEnd_ANewRunIsJudgedAfresh()
    {
        var guard = new WorkerMemoryGuard();
        guard.Evaluate(Reading(93, 60), tasksRunning: true, T0);
        guard.Evaluate(Reading(40, 5), tasksRunning: false, T0.AddSeconds(5));

        Assert.That(guard.Evaluate(Reading(93, 60), tasksRunning: true, T0 + WorkerMemoryGuard.StopGrace + TimeSpan.FromSeconds(1)), Is.EqualTo(MemoryGuardAction.Stop),
            "the stop belonged to the tasks that ended; the new one is stopped, not killed");
    }

    [Test]
    public void Evaluate_TotalUnknown_DoesNothing()
    {
        var reading = new MemoryReading { ProcessBytes = 90 * Gb };

        Assert.That(new WorkerMemoryGuard().Evaluate(reading, tasksRunning: true, T0), Is.EqualTo(MemoryGuardAction.None), "a guard that cannot read the total must not guess");
    }
}
