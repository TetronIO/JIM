// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Attribute Flow dialog re-analyses its expression as the administrator types (#1750). Each keystroke asks again;
/// only the newest question may answer, or a slow reply about an earlier text would paint over the current one (a loop
/// error for an expression no longer in the box). These pin the debounce and the latest-only guard.
/// </summary>
[TestFixture]
public class LatestRequestGateTests
{
    [Test]
    public async Task RunAsync_OneRequest_AppliesItsResultAsync()
    {
        using var gate = new LatestRequestGate(TimeSpan.FromMilliseconds(10));
        string? applied = null;

        var ran = await gate.RunAsync(_ => Task.FromResult("a"), result => applied = result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ran, Is.True);
            Assert.That(applied, Is.EqualTo("a"));
        }
    }

    [Test]
    public async Task RunAsync_SecondRequestWithinTheDelay_TheFirstNeverRunsAsync()
    {
        using var gate = new LatestRequestGate(TimeSpan.FromMilliseconds(200));
        var workRuns = new List<string>();
        var applied = new List<string>();

        var first = gate.RunAsync(_ => { workRuns.Add("first"); return Task.FromResult("first"); }, applied.Add);
        var second = gate.RunAsync(_ => { workRuns.Add("second"); return Task.FromResult("second"); }, applied.Add);
        var outcomes = await Task.WhenAll(first, second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes, Is.EqualTo(new[] { false, true }));
            Assert.That(workRuns, Is.EqualTo(new[] { "second" }), "a superseded request is debounced away, never sent");
            Assert.That(applied, Is.EqualTo(new[] { "second" }));
        }
    }

    [Test]
    public async Task RunAsync_EarlierReplyArrivingLast_IsDiscardedAsync()
    {
        using var gate = new LatestRequestGate(TimeSpan.Zero);
        var slowReply = new TaskCompletionSource<string>();
        var applied = new List<string>();

        // The first request is already in flight when the second is made, and answers after it.
        var first = gate.RunAsync(_ => slowReply.Task, applied.Add);
        await Task.Delay(20);
        var second = await gate.RunAsync(_ => Task.FromResult("second"), applied.Add);
        slowReply.SetResult("first");
        var firstOutcome = await first;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.True);
            Assert.That(firstOutcome, Is.False);
            Assert.That(applied, Is.EqualTo(new[] { "second" }), "the stale reply must not overwrite the current one");
        }
    }

    [Test]
    public async Task RunAsync_AfterDispose_DoesNotApplyAsync()
    {
        var gate = new LatestRequestGate(TimeSpan.FromMilliseconds(200));
        var applied = new List<string>();

        var pending = gate.RunAsync(_ => Task.FromResult("late"), applied.Add);
        gate.Dispose();

        Assert.That(await pending, Is.False);
        Assert.That(applied, Is.Empty, "a dialog that has closed must not be written to");
    }
}
