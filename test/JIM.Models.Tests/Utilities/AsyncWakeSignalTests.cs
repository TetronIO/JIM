// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

[TestFixture]
public class AsyncWakeSignalTests
{
    [Test]
    public async Task WaitAsync_SignalBeforeWait_ReturnsTrueImmediatelyAsync()
    {
        var signal = new AsyncWakeSignal();
        signal.Signal();

        var woken = await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.That(woken, Is.True);
    }

    [Test]
    public async Task WaitAsync_NoSignal_ReturnsFalseOnTimeoutAsync()
    {
        var signal = new AsyncWakeSignal();

        var woken = await signal.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.That(woken, Is.False);
    }

    [Test]
    public async Task WaitAsync_SignalDuringWait_ReturnsTrueAsync()
    {
        var signal = new AsyncWakeSignal();
        var waitTask = signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // Give the wait a moment to start, then signal it awake.
        await Task.Delay(50);
        signal.Signal();

        var woken = await waitTask;
        Assert.That(woken, Is.True);
    }

    [Test]
    public async Task WaitAsync_MultipleSignalsBeforeWait_CoalesceIntoOneWakeAsync()
    {
        var signal = new AsyncWakeSignal();
        signal.Signal();
        signal.Signal();

        // The first wait consumes the single coalesced signal.
        var firstWait = await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // The second wait must not observe a second pending signal; it should time out.
        var secondWait = await signal.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.That(firstWait, Is.True);
        Assert.That(secondWait, Is.False);
    }

    [Test]
    public async Task WaitAsync_SignalConsumed_NextWaitTimesOutAsync()
    {
        var signal = new AsyncWakeSignal();
        signal.Signal();
        await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var woken = await signal.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.That(woken, Is.False);
    }

    [Test]
    public void WaitAsync_CancelledDuringWait_ThrowsOperationCanceledExceptionAsync()
    {
        var signal = new AsyncWakeSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await signal.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Test]
    public void WaitAsync_CancelledBeforeWait_ThrowsOperationCanceledExceptionAsync()
    {
        var signal = new AsyncWakeSignal();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await signal.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Test]
    public async Task Signal_AfterWaitTimedOut_NextWaitReturnsTrueAsync()
    {
        var signal = new AsyncWakeSignal();

        // Let a wait time out first; the signal state must remain clean afterwards.
        var timedOut = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        signal.Signal();
        var woken = await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.That(timedOut, Is.False);
        Assert.That(woken, Is.True);
    }
}
