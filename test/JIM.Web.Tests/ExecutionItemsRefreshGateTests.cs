// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers when a running Activity's Run Profile Execution Items table re-reads its rows. The live refresh already
/// reads the item total from the stats on every tick; the table follows it, but no more often than the gate allows,
/// because a run can write thousands of items a second and every re-read costs a database query.
/// </summary>
[TestFixture]
public class ExecutionItemsRefreshGateTests
{
    private static readonly DateTime Start = new(2026, 10, 24, 21, 0, 0, DateTimeKind.Utc);

    private static ExecutionItemsRefreshGate Seeded(int? total)
    {
        var gate = new ExecutionItemsRefreshGate();
        gate.Seed(total, Start);
        return gate;
    }

    [Test]
    public void ShouldReload_TotalUnchanged_DoesNotReload()
    {
        Assert.That(Seeded(100).ShouldReload(100, runEnded: false, Start.AddMinutes(1)), Is.False);
    }

    [Test]
    public void ShouldReload_TotalGrewAfterTheInterval_Reloads()
    {
        Assert.That(Seeded(0).ShouldReload(274000, runEnded: false, Start + ExecutionItemsRefreshGate.MinimumInterval), Is.True);
    }

    [Test]
    public void ShouldReload_TotalGrewWithinTheInterval_WaitsAndReloadsOnALaterTick()
    {
        // Held back rather than dropped: the next tick past the interval still sees a total the table does not show.
        var gate = Seeded(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gate.ShouldReload(500, runEnded: false, Start.AddSeconds(1)), Is.False);
            Assert.That(gate.ShouldReload(900, runEnded: false, Start + ExecutionItemsRefreshGate.MinimumInterval), Is.True);
        }
    }

    [Test]
    public void ShouldReload_AfterAReload_WaitsForTheTotalToMoveAgain()
    {
        var gate = Seeded(0);
        var reloadedAt = Start + ExecutionItemsRefreshGate.MinimumInterval;
        gate.ShouldReload(900, runEnded: false, reloadedAt);

        Assert.That(gate.ShouldReload(900, runEnded: false, reloadedAt.AddMinutes(1)), Is.False);
    }

    [Test]
    public void ShouldReload_RunEnded_ReloadsAtOnceEvenWithTheTotalUnchanged()
    {
        // The last items land as the run finishes, and the stats are only exact once it has: the finished table must
        // show the final set, and no later tick is coming to catch it.
        Assert.That(Seeded(900).ShouldReload(900, runEnded: true, Start.AddSeconds(1)), Is.True);
    }
}
