using System.Diagnostics;
using Serilog;
using Serilog.Events;

namespace JIM.Application.Diagnostics;

/// <summary>
/// Listens for diagnostic spans and logs their completion with timing information.
/// Provides performance visibility into JIM operations.
/// </summary>
public sealed class DiagnosticListener : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly string _sourcePrefix;
    private readonly LogEventLevel _logLevel;
    private readonly double _slowOperationThresholdMs;

    /// <summary>
    /// Creates a new diagnostic listener that logs span completions to Serilog.
    /// </summary>
    /// <param name="sourcePrefix">Only listen to sources starting with this prefix (e.g., "JIM.").</param>
    /// <param name="logLevel">The log level to use for normal operations.</param>
    /// <param name="slowOperationThresholdMs">Operations taking longer than this will be logged at Warning level.</param>
    public DiagnosticListener(
        string sourcePrefix = "JIM.",
        LogEventLevel logLevel = LogEventLevel.Debug,
        double slowOperationThresholdMs = 1000)
    {
        _sourcePrefix = sourcePrefix;
        _logLevel = logLevel;
        _slowOperationThresholdMs = slowOperationThresholdMs;

        _listener = new ActivityListener
        {
            ShouldListenTo = ShouldListenTo,
            Sample = Sample,
            ActivityStarted = OnActivityStarted,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(_listener);
        Log.Debug("DiagnosticListener: Started listening for {SourcePrefix}* spans", _sourcePrefix);
    }

    private bool ShouldListenTo(ActivitySource source)
    {
        return source.Name.StartsWith(_sourcePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        // Capture all data for all spans
        return ActivitySamplingResult.AllDataAndRecorded;
    }

    private void OnActivityStarted(Activity activity)
    {
        // Log at Verbose level when spans start (optional, can be noisy)
        Log.Verbose("DiagnosticListener: Started {SpanName}", activity.DisplayName);
    }

    private void OnActivityStopped(Activity activity)
    {
        var durationMs = activity.Duration.TotalMilliseconds;
        var isSlowOperation = durationMs >= _slowOperationThresholdMs;

        // Build tags string for context
        var tags = string.Join(", ", activity.Tags.Select(t => $"{t.Key}={t.Value}"));
        var tagsSuffix = string.IsNullOrEmpty(tags) ? "" : $" [{tags}]";

        // Determine parent context for hierarchical display
        var parentName = activity.Parent?.DisplayName;
        var hierarchyPrefix = parentName != null ? $"{parentName} > " : "";

        // Log format: "Parent > Child completed in Xms" for parseable output
        // Use Warning level for slow operations but keep name format consistent for tree parsing
        var logLevel = isSlowOperation ? Serilog.Events.LogEventLevel.Warning : _logLevel;
        var slowMarker = isSlowOperation ? "[SLOW] " : "";

        Log.Write(
            logLevel,
            "DiagnosticListener: {SlowMarker}{HierarchyPrefix}{SpanName} completed in {DurationMs:F1}ms{Tags}",
            slowMarker,
            hierarchyPrefix,
            activity.DisplayName,
            durationMs,
            tagsSuffix);

        // Log errors specially
        if (activity.Status == ActivityStatusCode.Error)
        {
            var errorMessage = activity.StatusDescription ?? "Unknown error";
            Log.Error(
                "DiagnosticListener: {SpanName} failed: {ErrorMessage}",
                activity.DisplayName,
                errorMessage);
        }
    }

    /// <summary>
    /// Stops listening for diagnostic spans.
    /// </summary>
    public void Dispose()
    {
        _listener.Dispose();
        Log.Debug("DiagnosticListener: Stopped listening");
    }
}
