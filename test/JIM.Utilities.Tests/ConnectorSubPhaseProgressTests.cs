// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities.Tests;

[TestFixture]
public class ConnectorSubPhaseProgressTests
{
    [Test]
    public void Callback_WithNullReportDelegate_IsNull()
    {
        using var progress = new ConnectorSubPhaseProgress(null);

        Assert.That(progress.Callback, Is.Null, "Connectors detect 'no progress reporting wanted' by a null callback");
    }

    [Test]
    public async Task Callback_WithReportDelegate_ForwardsMessagesInOrderAsync()
    {
        var messages = new List<string>();
        using var progress = new ConnectorSubPhaseProgress(message =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        });

        Assert.That(progress.Callback, Is.Not.Null);
        var callback = progress.Callback!;
        await callback("Loading existing export file...");
        await callback("Writing 10 rows to output file...");

        Assert.That(messages, Is.EqualTo(new[] { "Loading existing export file...", "Writing 10 rows to output file..." }));
    }

    [Test]
    public async Task Callback_WithEmptyMessage_DoesNotReportAsync()
    {
        var reportCount = 0;
        using var progress = new ConnectorSubPhaseProgress(_ =>
        {
            reportCount++;
            return Task.CompletedTask;
        });

        var callback = progress.Callback!;
        await callback(string.Empty);
        await callback("   ");

        Assert.That(reportCount, Is.Zero, "An empty sub-phase message would blank the Activity message for no benefit");
    }

    [Test]
    public async Task Callback_WithConcurrentEmits_SerialisesReportsAsync()
    {
        var concurrentEmits = 0;
        var maxConcurrentEmits = 0;
        using var progress = new ConnectorSubPhaseProgress(async _ =>
        {
            var current = Interlocked.Increment(ref concurrentEmits);
            InterlockedMax(ref maxConcurrentEmits, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref concurrentEmits);
        });

        var callback = progress.Callback!;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => callback($"page {i}"))));

        Assert.That(maxConcurrentEmits, Is.EqualTo(1),
            "Report delegates typically write to a shared DbContext, so emits from a connector's parallel internal work must not overlap");
    }

    [Test]
    public void Callback_WhenReportDelegateThrows_DoesNotPropagate()
    {
        using var progress = new ConnectorSubPhaseProgress(_ => throw new InvalidOperationException("activity update failed"));
        var callback = progress.Callback!;

        // Progress reporting is cosmetic; a failure to narrate must never fail a synchronisation run.
        Assert.DoesNotThrowAsync(async () => await callback("Merging 100 changes into file..."));
    }

    [Test]
    public void Callback_WhenReportDelegateCancelled_PropagatesCancellation()
    {
        using var progress = new ConnectorSubPhaseProgress(_ => throw new OperationCanceledException());
        var callback = progress.Callback!;

        // A cancelled run must keep unwinding rather than being masked by the progress guard.
        Assert.ThrowsAsync<OperationCanceledException>(async () => await callback("Querying root DSE..."));
    }

    [Test]
    public async Task Callback_WithSharedGate_SerialisesAgainstTheCallersOwnEmitsAsync()
    {
        using var sharedGate = new SemaphoreSlim(1, 1);
        var reported = false;
        using var progress = new ConnectorSubPhaseProgress(_ =>
        {
            reported = true;
            return Task.CompletedTask;
        }, sharedGate: sharedGate);

        await sharedGate.WaitAsync();
        var emit = progress.Callback!("Exporting");

        await Task.Delay(50);
        Assert.That(reported, Is.False, "The emit must wait on the caller's gate while the caller is reporting its own progress");

        sharedGate.Release();
        await emit;
        Assert.That(reported, Is.True);
    }

    [Test]
    public async Task Dispose_WithSharedGate_LeavesTheGateUsableAsync()
    {
        using var sharedGate = new SemaphoreSlim(1, 1);
        var progress = new ConnectorSubPhaseProgress(_ => Task.CompletedTask, sharedGate: sharedGate);

        progress.Dispose();

        // The caller owns the shared gate and may still be using it after the connector call returns.
        await sharedGate.WaitAsync();
        sharedGate.Release();
        Assert.Pass();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
                return;
            current = previous;
        }
    }
}
