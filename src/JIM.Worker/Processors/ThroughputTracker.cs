// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// Tracks throughput (objects/sec) and estimates time remaining for long-running operations.
/// Used by import, sync, and export processors to enrich progress messages with live performance data.
/// The displayed rate is computed over a sliding window of recent samples rather than the whole run,
/// so heavy-tailed phases (for example an export's final mega-group batches) show what is actually
/// happening now; a whole-run average misleads in both directions (#1005). When the counter stops
/// advancing entirely, the ETA is replaced with "finishing up" instead of a fabricated
/// seconds-remaining figure. Thread-safe: export progress callbacks can fire from parallel batches.
/// </summary>
internal class ThroughputTracker
{
    /// <summary>No output until this much of the run has elapsed; too little signal.</summary>
    private const double MinimumElapsedSeconds = 2;

    /// <summary>The sliding window the displayed rate is computed over.</summary>
    private const double WindowSeconds = 90;

    /// <summary>Below this window span the run average is the better signal (start of run).</summary>
    private const double MinimumWindowSpanSeconds = 5;

    /// <summary>With no counter movement for this long, stop fabricating an ETA.</summary>
    private const double StalledAfterSeconds = 2 * WindowSeconds;

    private readonly object _lock = new();
    private readonly Func<DateTime> _clock;
    private readonly DateTime _startedAt;
    private readonly Queue<(DateTime At, int Processed)> _samples = new();
    private DateTime _lastAdvanceAt;
    private int _highWaterProcessed;
    private int _lastSampledProcessed;

    public ThroughputTracker()
        : this(null)
    {
    }

    /// <summary>
    /// Test hook: inject a clock so window and stall behaviour can be pinned deterministically.
    /// </summary>
    internal ThroughputTracker(Func<DateTime>? clock)
    {
        _clock = clock ?? (static () => DateTime.UtcNow);
        _startedAt = _clock();
        _lastAdvanceAt = _startedAt;
    }

    /// <summary>
    /// Formats a throughput suffix for a progress message, e.g. " (312 obj/s · ~5 min remaining)".
    /// Returns empty string if insufficient data to calculate (fewer than 2 seconds elapsed or zero processed).
    /// Returns " (finishing up)" in place of a rate and ETA when the counter has stopped advancing.
    /// </summary>
    /// <param name="processed">Number of objects processed so far.</param>
    /// <param name="total">Total number of objects to process (0 if unknown).</param>
    public string FormatThroughput(int processed, int total = 0)
    {
        lock (_lock)
        {
            var now = _clock();
            var elapsed = now - _startedAt;

            if (processed > _highWaterProcessed)
            {
                _highWaterProcessed = processed;
                _lastAdvanceAt = now;
            }

            // A counter reset (a phase restarting its count) would make window deltas negative;
            // start the window afresh from the new baseline.
            if (processed < _lastSampledProcessed)
                _samples.Clear();
            _lastSampledProcessed = processed;

            _samples.Enqueue((now, processed));

            // Trim the window, always retaining a baseline sample so a rate stays computable.
            while (_samples.Count > 2 && (now - _samples.Peek().At).TotalSeconds > WindowSeconds)
                _samples.Dequeue();

            if (elapsed.TotalSeconds < MinimumElapsedSeconds || processed <= 0)
                return string.Empty;

            var (windowStart, windowBaseline) = _samples.Peek();
            var windowSpan = (now - windowStart).TotalSeconds;
            var rate = windowSpan >= MinimumWindowSpanSeconds
                ? (processed - windowBaseline) / windowSpan
                : processed / elapsed.TotalSeconds;

            if (total > 0 && processed < total)
            {
                // The counter has not moved for a multiple of the window: any ETA would be
                // fabricated. Say what is actually happening instead.
                if (rate <= 0 || (now - _lastAdvanceAt).TotalSeconds >= StalledAfterSeconds)
                    return " (finishing up)";

                var remaining = (total - processed) / rate;
                return $" ({rate:N0} obj/s · ~{FormatDuration(remaining)} remaining)";
            }

            return rate > 0 ? $" ({rate:N0} obj/s)" : string.Empty;
        }
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
