// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;

namespace JIM.Application.Diagnostics;

/// <summary>
/// Totals the spans completed beneath one span, by their path below it, so a slow span can say where its time went
/// even when every descendant is individually too quick to be logged (#2492: a group page spent 19 seconds in a loop
/// of 500 objects, none of whose 38 ms per-object spans reached the 100 ms logging threshold).
/// </summary>
/// <remarks>
/// A path is the chain of span names from the owning span's child down to the descendant, joined by <c>/</c>. Spans
/// can complete on several threads at once (parallel export batches), so recording is synchronised.
/// </remarks>
public sealed class SpanBreakdown
{
    private readonly Dictionary<string, SpanBreakdownEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds one completed descendant span.
    /// </summary>
    public void Record(string path, double durationMs)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(path, out var entry))
            {
                entry = new SpanBreakdownEntry(path);
                _entries[path] = entry;
            }

            entry.Count++;
            entry.TotalMs += durationMs;
            if (durationMs > entry.MaxMs)
                entry.MaxMs = durationMs;
        }
    }

    /// <summary>
    /// The recorded paths, largest total first.
    /// </summary>
    public List<SpanBreakdownEntry> GetEntries()
    {
        lock (_entries)
        {
            return _entries.Values
                .Select(e => new SpanBreakdownEntry(e.Path) { Count = e.Count, TotalMs = e.TotalMs, MaxMs = e.MaxMs })
                .OrderByDescending(e => e.TotalMs)
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// The owning span's time that no direct child span covers: work done in the span itself, or in code with no span
    /// of its own. Never negative, since children running in parallel can add up to more than their parent.
    /// </summary>
    public double GetUnaccountedMs(double ownerDurationMs)
    {
        var directChildrenMs = GetEntries().Where(e => !e.Path.Contains('/')).Sum(e => e.TotalMs);
        return Math.Max(0, ownerDurationMs - directChildrenMs);
    }

    /// <summary>
    /// One line for the log: the largest paths with their count, total and longest single run, then the unaccounted
    /// time.
    /// </summary>
    public string Describe(double ownerDurationMs, int maxEntries)
    {
        var entries = GetEntries();
        var builder = new StringBuilder();

        foreach (var entry in entries.Take(maxEntries))
        {
            if (builder.Length > 0)
                builder.Append("; ");
            builder.Append(CultureInfo.InvariantCulture,
                $"{entry.Path} {entry.Count}x {entry.TotalMs:F1}ms (max {entry.MaxMs:F1}ms)");
        }

        if (entries.Count > maxEntries)
            builder.Append(CultureInfo.InvariantCulture, $"; {entries.Count - maxEntries} more");

        if (builder.Length > 0)
            builder.Append("; ");
        builder.Append(CultureInfo.InvariantCulture, $"unaccounted {GetUnaccountedMs(ownerDurationMs):F1}ms");

        return builder.ToString();
    }
}
