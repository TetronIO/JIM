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
    /// type withheld this run. A run that would exceed a limit attempts none of that change type; there
    /// is no partial attempt, so the sentence names what stopped it and how to let it through, rather
    /// than a count of what was done.
    /// </summary>
    /// <param name="type">The change type withheld this run.</param>
    /// <param name="limit">The Run Profile's limit for this change type.</param>
    /// <param name="pending">How many of this type were pending at the start of the run, all of which
    /// remain pending: the ledger decides the whole type withheld or not once, up front, so this is
    /// never a partial figure.</param>
    internal static string ForWithheld(PendingExportChangeType type, int limit, int pending)
    {
        var (singular, plural) = type switch
        {
            PendingExportChangeType.Create => ("create", "creates"),
            PendingExportChangeType.Update => ("update", "updates"),
            PendingExportChangeType.Delete => ("delete", "deletes"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported change type for a withheld-export warning.")
        };

        const string remedy = "Check what staged {0}, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit.";

        if (pending == 1)
        {
            return $"Max {plural} is {limit:N0}, but 1 {singular} was pending, so it was not attempted and remains pending. " +
                   string.Format(remedy, "it");
        }

        return $"Max {plural} is {limit:N0}, but {pending:N0} {plural} were pending, so none were attempted and all {pending:N0} remain pending. " +
               string.Format(remedy, "them");
    }
}
