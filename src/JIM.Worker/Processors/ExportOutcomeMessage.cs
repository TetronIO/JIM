// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Worker.Processors;

/// <summary>
/// The sentence a finished export leaves on its Activity, once the run has nothing left to narrate.
/// </summary>
/// <remarks>
/// A small type of its own so the wording and the number formatting are pinned by tests: built
/// inline in the processor they could not be reached without driving a whole export, so "10000
/// succeeded" sat there beside an already-grouped throughput figure, formatting its numbers two
/// different ways in one sentence.
/// </remarks>
internal static class ExportOutcomeMessage
{
    /// <param name="throughput">
    /// How long the run took and what it averaged, already formatted by
    /// <see cref="ThroughputTracker.FormatCompletion"/>, or empty where there was too little work
    /// to average.
    /// </param>
    /// <param name="writtenInPart">
    /// How many of the succeeded exports were written in part and are still waiting on a reference
    /// (issue #1398); named in the sentence so "succeeded" does not read as "finished".
    /// </param>
    internal static string ForExport(int succeeded, int failed, int deferred, string throughput, int writtenInPart = 0) =>
        writtenInPart > 0
            ? $"Export complete: {succeeded:N0} succeeded ({writtenInPart:N0} written in part, awaiting references), {failed:N0} failed, {deferred:N0} deferred{throughput}"
            : $"Export complete: {succeeded:N0} succeeded, {failed:N0} failed, {deferred:N0} deferred{throughput}";

    internal static string ForPreview(int pendingExports) =>
        $"Preview complete: {pendingExports:N0} export(s) would be processed";

    /// <summary>
    /// Run Profile Safeguards (#1618): the sentence appended to the Activity's warning for each change
    /// type whose limit was reached this run.
    /// </summary>
    /// <param name="type">The change type whose limit stopped further processing.</param>
    /// <param name="attempted">How many of this type were attempted, which is the Run Profile's own
    /// limit: the ledger only ever withholds once the whole of that limit has been consumed.</param>
    /// <param name="withheld">How many of this type remain Pending, untouched, for the next run.</param>
    internal static string ForWithheld(PendingExportChangeType type, int attempted, int withheld)
    {
        var (singular, plural) = type switch
        {
            PendingExportChangeType.Create => ("create", "creates"),
            PendingExportChangeType.Update => ("update", "updates"),
            PendingExportChangeType.Delete => ("delete", "deletes"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported change type for a withheld-export warning.")
        };

        var remaining = withheld == 1
            ? $"1 {singular} remains pending"
            : $"{withheld:N0} {plural} remain pending";

        return $"Stopped processing {plural} after {attempted:N0}, this Run Profile's limit; {remaining}.";
    }
}
