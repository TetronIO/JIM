// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Models.Localisation;
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
        ActivityMessage.ExportComplete(succeeded, failed, deferred, throughput, writtenInPart).ToEnglish();

    internal static string ForPreview(int pendingExports) =>
        ActivityMessage.ExportPreviewComplete(pendingExports).ToEnglish();

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
    internal static string ForWithheld(PendingExportChangeType type, int limit, int pending) =>
        ActivityMessage.ExportWithheld(type, limit, pending).ToEnglish();

    /// <summary>
    /// The sentence appended to the Activity's warning when queued changes were withdrawn before the run because nothing
    /// authorised them any more (see <c>ExportExecutionServer.WithdrawQueuedChangesWithoutAuthorityAsync</c>). A warning
    /// rather than a silent tidy-up: a queued change an administrator may have been expecting did not go out.
    /// </summary>
    /// <param name="changes">How many queued attribute changes were withdrawn.</param>
    /// <param name="pendingExports">How many Pending Exports were removed because the withdrawal left them empty.</param>
    internal static string ForWithdrawn(int changes, int pendingExports) =>
        ActivityMessage.ExportWithdrawn(changes, pendingExports).ToEnglish();

    /// <summary>
    /// The sentence appended to the Activity's warning when auto-confirmed exports were written but JIM could not record
    /// the values they wrote (#1936). A warning, not a failure: the target holds the right values, and nothing is lost,
    /// because the exports are left unconfirmed and the next export run sends them again.
    /// </summary>
    /// <param name="exports">How many exports were left unconfirmed.</param>
    internal static string ForUnrecorded(int exports) =>
        ActivityMessage.ExportUnrecorded(exports).ToEnglish();

    /// <summary>
    /// The warning for newly provisioned accounts that may be owed an Initial Password JIM could not record (#1121). A
    /// warning, not a failure: the accounts were created, but nothing will deliver a password nobody knows is owed, and
    /// exporting again will not queue one, because only the Create that provisions an account stages its password.
    /// </summary>
    /// <param name="accounts">How many accounts' passwords could not be recorded.</param>
    internal static string ForInitialPasswordsNotStaged(int accounts) =>
        ActivityMessage.InitialPasswordsNotStaged(accounts).ToEnglish();

    /// <summary>
    /// The warning for exported objects whose declared Exchange recipient state JIM could not queue or report (#2021).
    /// A warning, not a failure: the objects were written to the directory, but nothing will reach Exchange for them
    /// until their declared state is exported again.
    /// </summary>
    /// <param name="objects">How many objects' Exchange changes could not be recorded.</param>
    internal static string ForExchangeChangesNotStaged(int objects) =>
        ActivityMessage.ExchangeChangesNotStaged(objects).ToEnglish();

    /// <summary>
    /// The warning for objects an export created or renamed whose Partition JIM could not record (#2723). A warning, not
    /// a failure: the exports succeeded and are recorded, and each object keeps the Partition it had until the next import
    /// that reads it records where it is; until then, a Partition-scoped import cannot see it there.
    /// </summary>
    /// <param name="objects">How many objects' Partitions could not be recorded.</param>
    internal static string ForPartitionsNotRecorded(int objects) =>
        ActivityMessage.PartitionsNotRecorded(objects).ToEnglish();

    /// <summary>
    /// The warning for accounts whose enabled state an export wrote where JIM could not check, and record against, any
    /// Initial Password still owed to them (#2064). Delivering such a password would then follow the Synchronisation
    /// Rule's setting rather than the Attribute Flow, which is why the export run says so; most accounts are owed
    /// nothing, so it says "may".
    /// </summary>
    /// <param name="accounts">How many accounts the record could not be made for.</param>
    internal static string ForInitialPasswordMarkingFailed(int accounts) =>
        ActivityMessage.InitialPasswordMarkingFailed(accounts).ToEnglish();
}
