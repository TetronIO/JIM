// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// How the downward causal walk (#1811) reached a consequence from the item before it. Read-time only, never
/// persisted, so it carries no ordinal guarantee; the REST API names it rather than numbering it.
/// </summary>
public enum CausalConsequenceHopKind
{
    /// <summary>
    /// A recorded <see cref="CausalEdge"/> whose cause is the item before: a reference removal, a standalone
    /// deprovision, an export execution, a generated value revision or a scheduled deletion carried out.
    /// </summary>
    Edge,

    /// <summary>
    /// The import that confirmed an export the item before carried out, paired by the Pending Export both name.
    /// A confirmation is where a branch ends: the change it was about has landed.
    /// </summary>
    Confirmation,

    /// <summary>
    /// The first synchronisation of the same Connected System Object after an import, read from the object's own
    /// history. Nothing records this hop when it happens; it is what came next on the timeline.
    /// </summary>
    NextSynchronisation,

    /// <summary>
    /// A Pending Export the item before staged or executed that still exists: work that has not finished yet. Not an
    /// item; its live status says what it is waiting for.
    /// </summary>
    OutstandingPendingExport,

    /// <summary>
    /// A Metaverse Object deletion the item before scheduled that has not been carried out yet, read from the object
    /// itself, so a schedule cancelled since is not reported as pending.
    /// </summary>
    ScheduledDeletion
}

/// <summary>
/// Why a branch of the downward causal walk (#1811) stops. Every branch ends with one, so an administrator can tell a
/// chain that is complete from one that is still under way and from one cut short by the read bound. Read-time only,
/// never persisted.
/// </summary>
public enum CausalConsequenceEnding
{
    /// <summary>
    /// The branch reached the import that confirmed its export.
    /// </summary>
    Confirmed,

    /// <summary>
    /// An import whose Connected System has not been synchronised since, so nothing has acted on it yet.
    /// </summary>
    WaitingForSynchronisation,

    /// <summary>
    /// An import whose Connected System has been synchronised since, with nothing recorded for the object: the change
    /// needed nothing doing.
    /// </summary>
    NothingFollowed,

    /// <summary>
    /// A Pending Export waiting for an export run (Pending or Executing).
    /// </summary>
    WaitingForExport,

    /// <summary>
    /// A Pending Export whose export was not confirmed and will be retried (ExportNotConfirmed).
    /// </summary>
    WaitingForRetry,

    /// <summary>
    /// A Pending Export that will not progress without an administrator (Failed or Parked).
    /// </summary>
    NeedsAttention,

    /// <summary>
    /// A Pending Export that was exported and is waiting for the import that confirms it.
    /// </summary>
    WaitingForConfirmingImport,

    /// <summary>
    /// A Metaverse Object deletion scheduled with a grace period that has not ended yet.
    /// </summary>
    DeletionScheduled,

    /// <summary>
    /// The walk stopped at its read bound (depth, or the number of objects it carries to the next level), not at a
    /// real end.
    /// </summary>
    FurtherThanShown,

    /// <summary>
    /// The item recorded nothing that leads anywhere else. Its own outcomes are the whole story.
    /// </summary>
    NoFurtherConsequences
}
