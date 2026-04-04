namespace JIM.Worker.Processors;

/// <summary>
/// Tracks throughput (objects/sec) and estimates time remaining for long-running operations.
/// Used by import, sync, and export processors to enrich progress messages with live performance data.
/// </summary>
internal class ThroughputTracker
{
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>
    /// Formats a throughput suffix for a progress message, e.g. " (312 obj/s · ~5 min remaining)".
    /// Returns empty string if insufficient data to calculate (fewer than 2 seconds elapsed or zero processed).
    /// </summary>
    /// <param name="processed">Number of objects processed so far.</param>
    /// <param name="total">Total number of objects to process (0 if unknown).</param>
    public string FormatThroughput(int processed, int total = 0)
    {
        var elapsed = DateTime.UtcNow - _startedAt;
        if (elapsed.TotalSeconds < 2 || processed <= 0)
            return string.Empty;

        var rate = processed / elapsed.TotalSeconds;

        if (total > 0 && processed < total)
        {
            var remaining = (total - processed) / rate;
            return $" ({rate:N0} obj/s · ~{FormatDuration(remaining)} remaining)";
        }

        return $" ({rate:N0} obj/s)";
    }

    /// <summary>
    /// Formats the final throughput for a completion message, e.g. " in 3 min 12 sec (312 obj/s)".
    /// </summary>
    /// <param name="processed">Total objects processed.</param>
    public string FormatCompletion(int processed)
    {
        var elapsed = DateTime.UtcNow - _startedAt;
        if (elapsed.TotalSeconds < 1 || processed <= 0)
            return string.Empty;

        var rate = processed / elapsed.TotalSeconds;
        return $" in {FormatDuration(elapsed.TotalSeconds)} ({rate:N0} obj/s)";
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
