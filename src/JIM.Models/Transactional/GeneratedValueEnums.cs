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

/// <summary>
/// Why a generated value was written to the retired values register (Unique Value Generation, #242, plan decision
/// 4, <see cref="RetiredGeneratedValue"/>). Persisted by ordinal and exposed by name on the REST API and in
/// PowerShell; append-only, so new members are added at the end and existing values are never renumbered.
/// </summary>
public enum RetiredGeneratedValueReason
{
    /// <summary>
    /// The object that held the value (a Metaverse Object for an import flow, a Connected System Object for an
    /// export flow) was deleted. The portal calls this "Object deleted".
    /// </summary>
    ObjectDeleted = 0,

    /// <summary>
    /// Collision Remediation issued the object a different value after a target rejected this one (release 4).
    /// Nothing writes this reason before Collision Remediation exists.
    /// </summary>
    Regenerated = 1,

    /// <summary>
    /// The generated value stopped being JIM's to manage while its object lived on: another Attribute Flow took
    /// the attribute over, or the value was cleared. The portal calls this "No longer generated".
    /// </summary>
    Superseded = 2,

    /// <summary>
    /// The Attribute Flow that generated the value was removed (deleted, its Synchronisation Rule or Connected
    /// System deleted, or its source type changed). The portal calls this "Flow removed".
    /// </summary>
    Recalled = 3
}

/// <summary>
/// What became of a Collision Remediation revision handed to the repository (Unique Value Generation, #242, release
/// 4). Not persisted.
/// </summary>
public enum GeneratedValueRevisionResult
{
    /// <summary>
    /// The revision was written: the new value, the assignment and (import mode) the change record and
    /// revision-pending record, all in one transaction.
    /// </summary>
    Applied = 0,

    /// <summary>
    /// Nothing was written, because the value being revised was no longer the one the export carried: something
    /// changed it after the export run read it (the optimistic concurrency check). The rejection is reported as an
    /// ordinary export error; the next synchronisation re-stages whatever the object now holds.
    /// </summary>
    ValueChanged = 1,

    /// <summary>
    /// Nothing was written, because another live assignment for the same attribute claimed the new value first (the
    /// cross-assignment unique index, plan decision 13). The rejection is reported as an ordinary export error, and
    /// the next rejection draws again.
    /// </summary>
    ValueTaken = 2
}

/// <summary>
/// How a generated value's anchoring was decided (Unique Value Generation, #242, release 4; plan decision 10). Not
/// persisted.
/// </summary>
public enum GeneratedValueAnchoring
{
    /// <summary>
    /// No other participating Connected System holds the value for the object, so Collision Remediation may revise it.
    /// </summary>
    Unanchored = 0,

    /// <summary>
    /// Another participating Connected System has accepted the value (its Connected System Object joined to the same
    /// Metaverse Object holds it), so revising it would rename an account that is already in use.
    /// </summary>
    Anchored = 1,

    /// <summary>
    /// A participating Connected System cannot say, because it has not completed a Full Import since its connector
    /// space was cleared. Missing knowledge never permits a rename, so this resolves to Needs Decision.
    /// </summary>
    CannotTell = 2
}

/// <summary>
/// What the export run did with an export rejected because a generated value it carried is already in use (Unique
/// Value Generation, #242, release 4). Not persisted.
/// </summary>
public enum GeneratedValueCollisionHandling
{
    /// <summary>
    /// Collision Remediation drew the next value and revised it; the export stays Pending for the next
    /// synchronisation to re-stage.
    /// </summary>
    Remediated = 0,

    /// <summary>
    /// The value could not safely be revised (anchored, anchoring unknown, or remediation exhausted); the assignment
    /// waits on an administrator and the export is Parked.
    /// </summary>
    NeedsDecision = 1
}
