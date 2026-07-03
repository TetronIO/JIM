// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Summary of a single Temporal Scope Reconciler sweep (issue #892): how many Synchronisation Rules carried a
/// relative-date scoping criterion, how many candidate objects were evaluated, and which objects were flagged
/// with <c>ScopeReviewPending</c> because their time-driven scope membership had flipped. Flagging is all the
/// reconciler does; the existing sync/export engine applies the actual outcome (flag-and-delegate).
/// </summary>
public class ScopeReconciliationResult
{
    /// <summary>
    /// The number of enabled Synchronisation Rules that carried at least one complete relative-date scoping
    /// criterion and were therefore reconciled this sweep.
    /// </summary>
    public int RulesEvaluated { get; set; }

    /// <summary>
    /// The number of inbound (Connected System Object) candidates whose full scope was evaluated in memory.
    /// </summary>
    public int InboundCandidatesEvaluated { get; set; }

    /// <summary>
    /// The number of outbound (Metaverse Object) candidates whose full export scope was evaluated in memory.
    /// </summary>
    public int OutboundCandidatesEvaluated { get; set; }

    /// <summary>
    /// The Connected System Objects flagged for inbound re-synchronisation (their fresh scope disagreed with
    /// their current connection state).
    /// </summary>
    public List<Guid> FlaggedConnectedSystemObjectIds { get; set; } = new();

    /// <summary>
    /// The Metaverse Objects flagged for outbound re-export-evaluation (their fresh export scope disagreed with
    /// whether they are currently provisioned to the rule's target system).
    /// </summary>
    public List<Guid> FlaggedMetaverseObjectIds { get; set; } = new();

    /// <summary>
    /// The count of inbound Connected System Objects flagged this sweep.
    /// </summary>
    public int InboundFlagged => FlaggedConnectedSystemObjectIds.Count;

    /// <summary>
    /// The count of outbound Metaverse Objects flagged this sweep.
    /// </summary>
    public int OutboundFlagged => FlaggedMetaverseObjectIds.Count;
}
