// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The lifecycle state of a <see cref="GeneratedValueAssignment"/> (Unique Value Generation, #242, plan
/// decision 17). Persisted by ordinal; append-only, in the style of <c>ActivityRunProfileExecutionItemErrorType</c>
/// and the other causality enums, so new members are added at the end and existing values are never renumbered.
/// </summary>
public enum GeneratedValueAssignmentState
{
    /// <summary>
    /// The value has been generated in this synchronisation pass but not yet saved onto its object (#1904): it
    /// exists only in memory until the page flush. A dry run (Sync Preview), a page that fails before its flush,
    /// or a commit that loses the cross-run uniqueness race leaves it here, and it is never persisted.
    /// </summary>
    Proposed = 0,

    /// <summary>
    /// The value has been saved onto its object (#1904, product-owner decision 2026-10-01): set, with
    /// <see cref="GeneratedValueAssignment.CommittedAt"/>, by the page flush that persists the Metaverse Object
    /// (import mode) or the Connected System Object, provisioning or existing (export mode). Sticky; never
    /// recomputed from the base expression. It says nothing about whether a target system has accepted the value:
    /// that is anchoring (release 4), derived from the connector space, never from this state.
    /// </summary>
    Committed = 1,

    /// <summary>
    /// The value was revised after an attributable, unanchored export rejection (Collision Remediation, release 4).
    /// The object is flagged for review; the next synchronisation re-stages the export.
    /// </summary>
    Remediated = 2,

    /// <summary>
    /// The value was rejected and could not be safely revised automatically (anchored elsewhere, unknown, or the
    /// remediation attempt limit was exhausted). Waits on an administrator decision (release 4).
    /// </summary>
    NeedsDecision = 3
}
