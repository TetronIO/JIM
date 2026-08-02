// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;

namespace JIM.Utilities.Tests;

/// <summary>
/// The guarantees JIM makes to Connector authors about progress reporting (#637, #454): emits are
/// serialised, a failure to narrate never fails the run, and cancellation still propagates.
/// </summary>
[TestFixture]
public class ConnectorProgressTests
{
    [Test]
    public async Task ReportAsync_WithReportDelegate_ForwardsMessagesInOrderAsync()
    {
        var messages = new List<string>();
        using var progress = new ConnectorProgress(message =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        });

        await progress.ReportAsync("Loading existing export file...");
        await progress.ReportAsync("Writing 10 rows to output file...");

        Assert.That(messages, Is.EqualTo(new[] { "Loading existing export file...", "Writing 10 rows to output file..." }));
    }

    [Test]
    public async Task ReportAsync_WithNoReportDelegate_DoesNothingAsync()
    {
        // Connectors never check whether anybody is listening, so a reporter that records nothing
        // has to be safe to call.
        using var progress = new ConnectorProgress(report: null);

        await progress.ReportAsync("Reading the file...");
        await progress.EnterPhaseAsync("read");

        Assert.Pass();
    }

    [Test]
    public async Task None_IsSafeToCallAsync()
    {
        await ConnectorProgress.None.ReportAsync("Parsed 50,000 rows...");
        await ConnectorProgress.None.EnterPhaseAsync("parse", "Parsed 50,000 rows...");

        Assert.Pass();
    }

    [Test]
    public async Task ReportAsync_WithEmptyMessage_DoesNotReportAsync()
    {
        var reportCount = 0;
        using var progress = new ConnectorProgress(_ =>
        {
            reportCount++;
            return Task.CompletedTask;
        });

        await progress.ReportAsync(string.Empty);
        await progress.ReportAsync("   ");

        Assert.That(reportCount, Is.Zero, "An empty message would blank the Activity message for no benefit");
    }

    [Test]
    public async Task EnterPhaseAsync_WithPhaseDelegate_ForwardsTheKeyAndMessageAsync()
    {
        var transitions = new List<(string Key, string? Message)>();
        using var progress = new ConnectorProgress(
            report: _ => Task.CompletedTask,
            enterPhase: (key, message) =>
            {
                transitions.Add((key, message));
                return Task.CompletedTask;
            });

        await progress.EnterPhaseAsync("load-existing-file");
        await progress.EnterPhaseAsync("merge", "Merging 100 changes into file...");

        Assert.That(transitions, Is.EqualTo(new[]
        {
            ("load-existing-file", (string?)null),
            ("merge", (string?)"Merging 100 changes into file...")
        }));
    }

    [Test]
    public async Task EnterPhaseAsync_WithBlankKey_IsIgnoredAsync()
    {
        var transitions = 0;
        using var progress = new ConnectorProgress(
            report: _ => Task.CompletedTask,
            enterPhase: (_, _) =>
            {
                transitions++;
                return Task.CompletedTask;
            });

        await progress.EnterPhaseAsync("   ");

        Assert.That(transitions, Is.Zero);
    }

    [Test]
    public async Task EnterPhaseAsync_WithNoPhaseDelegate_StillNarratesTheMessageAsync()
    {
        // Callers that do not track phases (an export path reporting counts only) must not lose the
        // Connector's narration just because it arrived attached to a phase change.
        var messages = new List<string>();
        using var progress = new ConnectorProgress(message =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        });

        await progress.EnterPhaseAsync("merge", "Merging 100 changes into file...");

        Assert.That(messages, Is.EqualTo(new[] { "Merging 100 changes into file..." }));
    }

    [Test]
    public async Task ReportAsync_WithConcurrentEmits_SerialisesReportsAsync()
    {
        var concurrentEmits = 0;
        var maxConcurrentEmits = 0;
        using var progress = new ConnectorProgress(async _ =>
        {
            var current = Interlocked.Increment(ref concurrentEmits);
            InterlockedMax(ref maxConcurrentEmits, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref concurrentEmits);
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => progress.ReportAsync($"page {i}"))));

        Assert.That(maxConcurrentEmits, Is.EqualTo(1),
            "Report delegates typically write to a shared DbContext, so emits from a connector's parallel internal work must not overlap");
    }

    [Test]
    public void ReportAsync_WhenReportDelegateThrows_DoesNotPropagate()
    {
        using var progress = new ConnectorProgress(_ => throw new InvalidOperationException("activity update failed"));

        // Progress reporting is cosmetic; a failure to narrate must never fail a synchronisation run.
        Assert.DoesNotThrowAsync(async () => await progress.ReportAsync("Merging 100 changes into file..."));
    }

    [Test]
    public void EnterPhaseAsync_WhenPhaseDelegateThrows_DoesNotPropagate()
    {
        using var progress = new ConnectorProgress(
            report: _ => Task.CompletedTask,
            enterPhase: (_, _) => throw new InvalidOperationException("phase write failed"));

        Assert.DoesNotThrowAsync(async () => await progress.EnterPhaseAsync("write"));
    }

    [Test]
    public void ReportAsync_WhenReportDelegateCancelled_PropagatesCancellation()
    {
        using var progress = new ConnectorProgress(_ => throw new OperationCanceledException());

        // A cancelled run must keep unwinding rather than being masked by the progress guard.
        Assert.ThrowsAsync<OperationCanceledException>(async () => await progress.ReportAsync("Querying root DSE..."));
    }

    [Test]
    public async Task ReportAsync_WithSharedGate_SerialisesAgainstTheCallersOwnEmitsAsync()
    {
        using var sharedGate = new SemaphoreSlim(1, 1);
        var reported = false;
        using var progress = new ConnectorProgress(_ =>
        {
            reported = true;
            return Task.CompletedTask;
        }, sharedGate: sharedGate);

        await sharedGate.WaitAsync();
        var emit = progress.ReportAsync("Exporting");

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
        var progress = new ConnectorProgress(_ => Task.CompletedTask, sharedGate: sharedGate);

        progress.Dispose();

        // The caller owns the shared gate and may still be using it after the connector call returns.
        await sharedGate.WaitAsync();
        sharedGate.Release();
        Assert.Pass();
    }

    [Test]
    public void ConnectorProgress_ImplementsTheConnectorFacingContract()
    {
        Assert.That(new ConnectorProgress(report: null), Is.InstanceOf<IConnectorProgress>());
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
