// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

        // Build tags list including parent ID for accurate tree reconstruction.
        // IMPORTANT: use TagObjects, not Tags. Activity.Tags only yields KeyValuePair<string, string?>
        // (string-valued tags); int/double-valued tags set via Activity.SetTag(string, object?)
        // live in TagObjects only and would silently disappear from the rendered message —
        // including cumulativeObjectCount, wallClockOffsetMs, csoCount, etc., which downstream
        // parsers (JIM Step 6 + JIM-Bench LogLineParser) read off the log line.
        var tagsList = activity.TagObjects.Select(t => $"{t.Key}={t.Value}").ToList();

        // Add parentId to enable proper parent-child time tracking in the tree display
        // Without this, child times across multiple parent invocations get incorrectly summed
        var parentId = activity.Parent?.Id;
        if (parentId != null)
        {
            tagsList.Insert(0, $"parentId={parentId}");
        }

        // rootSpanId is the unique-per-execution identifier — it's the same string
        // for the root span and every descendant within one trace. Downstream
        // (JIM-Bench) uses it to partition throughput series per sync execution
        // so two sync runs against the same Connected System in the same test
        // run don't get collapsed into one line. Emitted on every span so the
        // join semantics in bench are uniform between root rows (test_operations)
        // and child rows (throughput_samples).
        var rootSpanId = activity.RootId;
        if (!string.IsNullOrEmpty(rootSpanId))
        {
            tagsList.Insert(0, $"rootSpanId={rootSpanId}");
        }

        // Slow operations are tagged inside the bracket rather than prefixed to the path.
        // The previous "[SLOW] " inline prefix broke downstream parsers that anchor on the
        // span name (e.g. JIM-Bench's LogLineParser), and the Warning log level still
        // signals slowness to humans skim-reading docker logs.
        if (isSlowOperation)
        {
            tagsList.Add("slow=true");
        }

        var tags = string.Join(", ", tagsList);
        var tagsSuffix = string.IsNullOrEmpty(tags) ? "" : $" [{tags}]";

        // Determine root-operation context for hierarchical display. Walk up the activity
        // chain to find the topmost ancestor; emit "{Root} > {Child}" rather than
        // "{ImmediateParent} > {Child}" so downstream parsers (JIM Step 6 + JIM-Bench)
        // can identify the root operation (FullSync, DeltaSync, Import, Export, etc.)
        // even when emitted from a deeply nested span. The full ancestry is still
        // recoverable via the parentId tag, which Step 6 uses to rebuild the perf tree.
        Activity? rootActivity = activity;
        while (rootActivity?.Parent != null)
        {
            rootActivity = rootActivity.Parent;
        }
        var hierarchyPrefix = (rootActivity != null && rootActivity != activity)
            ? $"{rootActivity.DisplayName} > "
            : "";

        // Log format: "Parent > Child completed in Xms" for parseable output.
        // The :l format specifier on each string argument tells Serilog to render
        // literally (no surrounding "" quotes), which both the JIM Step 6 parser
        // and the JIM-Bench server-side parser depend on.
        var logLevel = isSlowOperation ? Serilog.Events.LogEventLevel.Warning : _logLevel;

        Log.Write(
            logLevel,
            "DiagnosticListener: {HierarchyPrefix:l}{SpanName:l} completed in {DurationMs:F1}ms{Tags:l}",
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
