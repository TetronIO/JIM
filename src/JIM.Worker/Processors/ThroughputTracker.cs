// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// Measures how long an operation took and what it averaged, for the message a completed run
/// leaves behind. Used by the import, sync and export processors.
/// </summary>
/// <remarks>
/// This deliberately does not report live throughput. It used to: every progress message carried a
/// rate and a time-remaining suffix measured here. The portal and the progress API compute both
/// from the Activity's own counters (<c>ActivityEtaTracker</c>), so the worker's figures were
/// consumed only as prose, and the two estimators disagreed on screen because they sampled over
/// different windows. Live rate, time remaining and the stalled "finishing up" state now belong to
/// whoever is displaying the run; a completed run has no live bar to duplicate, so the summary
/// below stays here.
/// </remarks>
internal class ThroughputTracker
{
    private readonly Func<DateTime> _clock;
    private readonly DateTime _startedAt;

    public ThroughputTracker()
        : this(null)
    {
    }

    /// <summary>
    /// Test hook: inject a clock so elapsed time can be pinned deterministically.
    /// </summary>
    internal ThroughputTracker(Func<DateTime>? clock)
    {
        _clock = clock ?? (static () => DateTime.UtcNow);
        _startedAt = _clock();
    }

    /// <summary>
    /// Formats the final throughput for a completion message, e.g. " in 3 min 12 sec (avg 312 obj/s)".
    /// Deliberately a whole-run average: it summarises the completed operation.
    /// </summary>
    /// <param name="processed">Total objects processed.</param>
    public string FormatCompletion(int processed)
    {
        var elapsed = _clock() - _startedAt;
        if (elapsed.TotalSeconds < 1 || processed <= 0)
            return string.Empty;

        var rate = processed / elapsed.TotalSeconds;
        return $" in {FormatDuration(elapsed.TotalSeconds)} (avg {rate:N0} obj/s)";
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (totalSeconds < 60)
            return $"{totalSeconds:N0} sec";
        if (totalSeconds < 3600)
        {
            var minutes = (int)(totalSeconds / 60);
            var seconds = (int)(totalSeconds % 60);
            return seconds > 0 ? $"{minutes} min {seconds} sec" : $"{minutes} min";
        }

        var hours = (int)(totalSeconds / 3600);
        var mins = (int)((totalSeconds % 3600) / 60);
        return mins > 0 ? $"{hours} hr {mins} min" : $"{hours} hr";
    }
}
