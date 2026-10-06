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

    /// <summary>
    /// The sentence appended to the Activity's warning when queued changes were withdrawn before the run because nothing
    /// authorised them any more (see <c>ExportExecutionServer.WithdrawQueuedChangesWithoutAuthorityAsync</c>). A warning
    /// rather than a silent tidy-up: a queued change an administrator may have been expecting did not go out.
    /// </summary>
    /// <param name="changes">How many queued attribute changes were withdrawn.</param>
    /// <param name="pendingExports">How many Pending Exports were removed because the withdrawal left them empty.</param>
    internal static string ForWithdrawn(int changes, int pendingExports)
    {
        var (subject, pronoun) = changes == 1
            ? ("1 queued change was", "it")
            : ($"{changes:N0} queued changes were", "them");

        var removal = pendingExports switch
        {
            0 => ".",
            1 => "; 1 Pending Export left empty was removed.",
            _ => $"; {pendingExports:N0} Pending Exports left empty were removed."
        };

        return $"{subject} withdrawn instead of exported, because no enabled Synchronisation Rule or Attribute Flow authorised {pronoun} " +
               $"any more, or the object was no longer joined{removal}";
    }

    /// <summary>
    /// The sentence appended to the Activity's warning when auto-confirmed exports were written but JIM could not record
    /// the values they wrote (#1936). A warning, not a failure: the target holds the right values, and nothing is lost,
    /// because the exports are left unconfirmed and the next export run sends them again.
    /// </summary>
    /// <param name="exports">How many exports were left unconfirmed.</param>
    internal static string ForUnrecorded(int exports) =>
        exports == 1
            ? "1 export was written, but JIM could not record the values it wrote, so it was left unconfirmed and the next export run " +
              "will send it again. The service log names the cause."
            : $"{exports:N0} exports were written, but JIM could not record the values they wrote, so they were left unconfirmed and the " +
              "next export run will send them again. The service log names the cause.";
}
