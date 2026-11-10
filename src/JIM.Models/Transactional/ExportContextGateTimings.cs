// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// An export run's totals on its shared context gate (#2895), by purpose: how often each purpose took the gate, how long
/// it waited for it and how long it held it. The gate serialises everything an export's parallel batches do on the run's
/// own database context, so these totals are what say how much of the run that serial section costs, and which purpose
/// costs it. Safe to record into from several parallel batches at once.
/// </summary>
public sealed class ExportContextGateTimings
{
    private readonly object _lock = new();
    private readonly Dictionary<ExportContextGatePurpose, ExportContextGateTiming> _byPurpose = [];

    /// <summary>
    /// Adds one pass through the gate to its purpose's totals.
    /// </summary>
    /// <param name="purpose">Why the gate was taken.</param>
    /// <param name="wait">How long the caller waited for the gate.</param>
    /// <param name="held">How long the caller held the gate.</param>
    public void Record(ExportContextGatePurpose purpose, TimeSpan wait, TimeSpan held)
    {
        lock (_lock)
        {
            var current = _byPurpose.GetValueOrDefault(purpose, new ExportContextGateTiming(purpose, 0, TimeSpan.Zero, TimeSpan.Zero));
            _byPurpose[purpose] = current with
            {
                Count = current.Count + 1,
                TotalWait = current.TotalWait + wait,
                TotalHeld = current.TotalHeld + held
            };
        }
    }

    /// <summary>How many times the gate was taken, for every purpose.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _byPurpose.Values.Sum(t => t.Count);
        }
    }

    /// <summary>The time spent waiting for the gate, for every purpose.</summary>
    public TimeSpan TotalWait
    {
        get
        {
            lock (_lock)
                return TimeSpan.FromTicks(_byPurpose.Values.Sum(t => t.TotalWait.Ticks));
        }
    }

    /// <summary>The time the gate was held, for every purpose.</summary>
    public TimeSpan TotalHeld
    {
        get
        {
            lock (_lock)
                return TimeSpan.FromTicks(_byPurpose.Values.Sum(t => t.TotalHeld.Ticks));
        }
    }

    /// <summary>
    /// The totals of each purpose that took the gate, the one that held it longest first.
    /// </summary>
    public IReadOnlyList<ExportContextGateTiming> Snapshot()
    {
        lock (_lock)
            return [.. _byPurpose.Values.OrderByDescending(t => t.TotalHeld).ThenBy(t => t.Purpose)];
    }
}
