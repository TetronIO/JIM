// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Decides when a running Activity's Run Profile Execution Items table re-reads its rows. The live refresh reads the
/// Activity's item total from its stats on every tick; the table follows that total, but no more often than
/// <see cref="MinimumInterval"/>, because a large run writes thousands of items a second and each re-read is a
/// database query. A change held back by the interval is not lost: the next tick past it still sees a total the table
/// does not show.
/// </summary>
public sealed class ExecutionItemsRefreshGate
{
    /// <summary>
    /// The shortest gap between two re-reads while a run is in progress.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);

    private int? _shownTotal;
    private DateTime _lastReloadUtc = DateTime.MinValue;

    /// <summary>
    /// Records the total the table was loaded with, so the first tick does not re-read rows it already holds.
    /// </summary>
    public void Seed(int? total, DateTime nowUtc)
    {
        _shownTotal = total;
        _lastReloadUtc = nowUtc;
    }

    /// <summary>
    /// Whether the table should re-read its rows now, recording the re-read when it should.
    /// </summary>
    /// <param name="total">The item total the stats report now.</param>
    /// <param name="runEnded">
    /// True on the tick the run leaves In Progress. That tick always re-reads, whatever the interval or the total: the
    /// last items land as the run finishes, the in-flight stats are only exact once it has, and no later tick is coming.
    /// </param>
    /// <param name="nowUtc">The current time.</param>
    public bool ShouldReload(int? total, bool runEnded, DateTime nowUtc)
    {
        if (!runEnded && (total == _shownTotal || nowUtc - _lastReloadUtc < MinimumInterval))
            return false;

        _shownTotal = total;
        _lastReloadUtc = nowUtc;
        return true;
    }
}
