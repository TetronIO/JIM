// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using NUnit.Framework;

namespace JIM.Worker.Tests;

/// <summary>
/// Tests the defunct-task sweep (#1568): a worker task whose Task has terminated without running its own
/// epilogue (an exception escaping the dispatch case, such as a completion write failing on a poisoned
/// DbContext) must be identified so the main loop can drop it. Without the sweep, the stale entry pins
/// CurrentTasks above zero forever and the loop heartbeats a dead task instead of ever polling for new
/// work again, silently wedging the whole worker.
/// </summary>
[TestFixture]
public class WorkerDefunctTaskSweepTests
{
    [Test]
    public void GetDefunctTaskEntries_FaultedCancelledAndCompletedEntries_AreSelectedRunningIsNot()
    {
        var running = new TaskTask(Guid.NewGuid(), new TaskCompletionSource().Task, new CancellationTokenSource());
        var faulted = new TaskTask(Guid.NewGuid(), Task.FromException(new InvalidOperationException("poisoned context")), new CancellationTokenSource());
        var cancelled = new TaskTask(Guid.NewGuid(), Task.FromCanceled(new CancellationToken(true)), new CancellationTokenSource());
        var completed = new TaskTask(Guid.NewGuid(), Task.CompletedTask, new CancellationTokenSource());

        var defunct = Worker.GetDefunctTaskEntries([running, faulted, cancelled, completed]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(defunct.Select(t => t.TaskId), Is.EquivalentTo(new[] { faulted.TaskId, cancelled.TaskId, completed.TaskId }),
                "every terminated-but-still-listed entry must be swept, whatever its terminal state");
            Assert.That(defunct.Select(t => t.TaskId), Does.Not.Contain(running.TaskId),
                "a live task must never be swept");
        }
    }

    [Test]
    public void GetDefunctTaskEntries_NoEntries_ReturnsEmpty()
    {
        Assert.That(Worker.GetDefunctTaskEntries([]), Is.Empty);
    }
}
