// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// The sentence a finished import leaves on its Activity, once the run has nothing left to narrate.
/// The import counterpart of <see cref="ExportOutcomeMessage"/>, kept as a type of its own for the same
/// reason: the wording and the number formatting are pinned by tests without driving a whole import.
/// </summary>
internal static class ImportOutcomeMessage
{
    /// <param name="objects">Everything the run read from the Connected System.</param>
    /// <param name="created">Connected System Objects created.</param>
    /// <param name="updated">Connected System Objects whose attributes were changed.</param>
    /// <param name="unchanged">Objects skipped as unchanged by their content hash.</param>
    /// <param name="errors">Objects the run could not import.</param>
    /// <param name="throughput">
    /// How long the run took and what it averaged, already formatted by
    /// <see cref="ThroughputTracker.FormatCompletion"/>, or empty where there was too little work
    /// to average.
    /// </param>
    internal static string ForImport(int objects, int created, int updated, int unchanged, int errors, string throughput)
    {
        var parts = new List<string> { $"{created:N0} created", $"{updated:N0} updated" };
        if (unchanged > 0)
            parts.Add($"{unchanged:N0} unchanged");
        if (errors > 0)
            parts.Add($"{errors:N0} error{(errors == 1 ? "" : "s")}");

        return $"Import complete: {objects:N0} object{(objects == 1 ? "" : "s")} ({string.Join(", ", parts)}){throughput}";
    }
}
