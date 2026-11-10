// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// The export's shared context gate (#2895): one holder at a time, and every pass through it timed into the run's
/// totals by purpose, whether the work under it succeeds or not.
/// </summary>
[TestFixture]
public class ExportContextGateTests
{
    [Test]
    public async Task RunAsync_WorkUnderTheGate_RecordsItsHeldTimeUnderItsPurposeAsync()
    {
        var timings = new ExportContextGateTimings();
        using var gate = new ExportContextGate(timings);

        var answer = await gate.RunAsync(ExportContextGatePurpose.ConnectorProgress, async () =>
        {
            await Task.Delay(30);
            return 42;
        }, CancellationToken.None);

        var entry = timings.Snapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer, Is.EqualTo(42));
            Assert.That(entry.Purpose, Is.EqualTo(ExportContextGatePurpose.ConnectorProgress));
            Assert.That(entry.Count, Is.EqualTo(1));
            Assert.That(entry.TotalHeld, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(25)));
        }
    }

    [Test]
    public async Task RunAsync_SecondCallerWhileTheGateIsHeld_WaitsAndRecordsTheWaitAsync()
    {
        var timings = new ExportContextGateTimings();
        using var gate = new ExportContextGate(timings);
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var concurrentHolders = 0;
        var maxConcurrentHolders = 0;

        async Task<bool> Hold()
        {
            var now = Interlocked.Increment(ref concurrentHolders);
            maxConcurrentHolders = Math.Max(maxConcurrentHolders, now);
            holding.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref concurrentHolders);
            return true;
        }

        var first = gate.RunAsync(ExportContextGatePurpose.ConnectorProgress, Hold, CancellationToken.None);
        await holding.Task;
        var second = gate.RunAsync(ExportContextGatePurpose.NextBatch, async () =>
        {
            var now = Interlocked.Increment(ref concurrentHolders);
            maxConcurrentHolders = Math.Max(maxConcurrentHolders, now);
            Interlocked.Decrement(ref concurrentHolders);
            await Task.CompletedTask;
            return true;
        }, CancellationToken.None);

        await Task.Delay(40);
        Assert.That(second.IsCompleted, Is.False, "the second caller must wait while the gate is held");
        release.SetResult();
        await Task.WhenAll(first, second);

        var byPurpose = timings.Snapshot().ToDictionary(e => e.Purpose);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(maxConcurrentHolders, Is.EqualTo(1));
            Assert.That(byPurpose[ExportContextGatePurpose.NextBatch].TotalWait, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(30)),
                "the wait for the gate is the second caller's, under its own purpose");
            Assert.That(byPurpose[ExportContextGatePurpose.ConnectorProgress].TotalHeld, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(30)));
        }
    }

    [Test]
    public async Task RunAsync_WorkThrows_RecordsTheHeldTimeReleasesTheGateAndRethrowsAsync()
    {
        var timings = new ExportContextGateTimings();
        using var gate = new ExportContextGate(timings);

        Assert.That(async () => await gate.RunAsync<bool>(ExportContextGatePurpose.Progress,
            () => throw new InvalidOperationException("boom"), CancellationToken.None), Throws.InvalidOperationException);

        var next = await gate.RunAsync(ExportContextGatePurpose.Progress, () => Task.FromResult(true), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next, Is.True, "the gate was released by the failed pass");
            Assert.That(timings.Snapshot().Single().Count, Is.EqualTo(2), "a failed pass still counts");
        }
    }

    [Test]
    public async Task RunAsync_CancelledWhileWaiting_RecordsNothingAndDoesNotTakeTheGateAsync()
    {
        var timings = new ExportContextGateTimings();
        using var gate = new ExportContextGate(timings);
        var release = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        var first = gate.RunAsync(ExportContextGatePurpose.ConnectorProgress, async () =>
        {
            holding.SetResult();
            await release.Task;
            return true;
        }, CancellationToken.None);
        await holding.Task;

        using var cts = new CancellationTokenSource();
        var waiting = gate.RunAsync(ExportContextGatePurpose.NextBatch, () => Task.FromResult(true), cts.Token);
        await cts.CancelAsync();
        Assert.That(async () => await waiting, Throws.InstanceOf<OperationCanceledException>());
        release.SetResult();
        await first;

        Assert.That(timings.Snapshot().Select(e => e.Purpose), Is.EqualTo(new[] { ExportContextGatePurpose.ConnectorProgress }),
            "a caller that never took the gate held nothing");
    }
}
