// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models;

/// <summary>
/// Pairs what an import recorded for each Partition before and after it ran (#2715), for the Activity's Connector state.
/// Kept apart from the markup so what each side says is decided in one place.
/// </summary>
public static class ImportPartitionWatermarkRows
{
    /// <summary>
    /// One row per Partition the import read or recorded, ordered by the name the Activity recorded. After the run, a
    /// Partition reads Unchanged where the import recorded the value it started from, or left the Partition out (which
    /// keeps it), and NotRecorded throughout where the import recorded none at all.
    /// </summary>
    /// <param name="from">The watermarks the import started from, as the Activity recorded them.</param>
    /// <param name="to">The watermarks the import recorded, or null where it recorded none.</param>
    public static IReadOnlyList<ImportPartitionWatermarkRow> Compare(IReadOnlyList<ImportPartitionWatermark>? from, IReadOnlyList<ImportPartitionWatermark>? to)
    {
        var started = (from ?? []).GroupBy(w => w.PartitionId).ToDictionary(g => g.Key, g => g.First());
        var recorded = (to ?? []).GroupBy(w => w.PartitionId).ToDictionary(g => g.Key, g => g.First());

        return started.Keys.Union(recorded.Keys)
            .Select(partitionId =>
            {
                started.TryGetValue(partitionId, out var before);
                recorded.TryGetValue(partitionId, out var after);
                var name = before?.PartitionName ?? after!.PartitionName;
                var afterState = AfterState(to, before, after);
                return new ImportPartitionWatermarkRow(partitionId, name,
                    before?.Value != null ? ImportPartitionWatermarkState.Recorded : ImportPartitionWatermarkState.None,
                    before?.Value,
                    afterState,
                    afterState == ImportPartitionWatermarkState.Recorded ? after!.Value : null);
            })
            .OrderBy(row => row.PartitionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PartitionId)
            .ToList();
    }

    private static ImportPartitionWatermarkState AfterState(IReadOnlyList<ImportPartitionWatermark>? to, ImportPartitionWatermark? before, ImportPartitionWatermark? after)
    {
        if (to == null)
            return ImportPartitionWatermarkState.NotRecorded;
        if (after == null || string.Equals(after.Value, before?.Value, StringComparison.Ordinal))
            return ImportPartitionWatermarkState.Unchanged;
        return after.Value == null ? ImportPartitionWatermarkState.Cleared : ImportPartitionWatermarkState.Recorded;
    }
}
