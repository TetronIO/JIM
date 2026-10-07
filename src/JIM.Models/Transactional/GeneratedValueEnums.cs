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
    /// In import mode a <see cref="GeneratedValueRevisionPending"/> record carries the revision to the queued exports at
    /// the next synchronisation; in export mode the queued export was rewritten directly.
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

/// <summary>
/// Why a generated value is waiting on an administrator's decision (Unique Value Generation, #242, release 4; plan
/// decisions 10, 11 and 15): recorded when the assignment enters <see cref="GeneratedValueAssignmentState.NeedsDecision"/>,
/// so the decision surfaces can say why without re-deriving it from state that has since moved on. Persisted by ordinal
/// and exposed by name on the REST API and in PowerShell; append-only, so new members are added at the end and existing
/// values are never renumbered.
/// </summary>
public enum GeneratedValueNeedsDecisionReason
{
    /// <summary>
    /// Another Connected System the value is exported to has already accepted it for the same object, so correcting it
    /// would rename an account already in use. <see cref="GeneratedValueAssignment.AnchoredByConnectedSystemId"/> names it.
    /// </summary>
    AnchoredElsewhere = 0,

    /// <summary>
    /// JIM cannot tell whether another Connected System the value is exported to holds it, because that system's
    /// connector space was cleared and it has not completed a Full Import since. Missing knowledge never permits a
    /// rename. <see cref="GeneratedValueAssignment.AnchoredByConnectedSystemId"/> names the system.
    /// </summary>
    CannotTell = 1,

    /// <summary>
    /// The value has already been corrected <see cref="GeneratedValueAssignment.RemediationCount"/> times (the limit),
    /// and the target still rejects it: a target that refuses every value JIM issues is telling an administrator
    /// something a further rename will not fix.
    /// </summary>
    RemediationLimitReached = 2,

    /// <summary>
    /// JIM looked for another value and found none it could issue (the flow's attempt limit, a full fixed width, or
    /// every candidate taken).
    /// </summary>
    NoValueAvailable = 3
}

/// <summary>
/// Where a generated value decision stands, as the decision surfaces show it (Unique Value Generation, #242, release 4,
/// Phase 9). Derived from the assignment, never persisted; exposed by name on the REST API and in PowerShell.
/// </summary>
public enum GeneratedValueDecisionStatus
{
    /// <summary>
    /// A target rejected the value and JIM did not correct it: its export is held until an administrator allows the
    /// rename, tries again, or changes the flow.
    /// </summary>
    NeedsDecision = 0,

    /// <summary>
    /// An administrator allowed the rename; the next export run that meets the rejection chooses the next free value
    /// and applies it everywhere.
    /// </summary>
    RenameAllowed = 1,

    /// <summary>
    /// Not held: nothing is waiting on an administrator for this value (for example, straight after "Try again"). The
    /// decision lists never return such a row; a read of one value by id can.
    /// </summary>
    Released = 2
}

/// <summary>
/// What became of an administrator's action on one generated value decision (Unique Value Generation, #242, release 4,
/// Phase 9). Not persisted.
/// </summary>
public enum GeneratedValueDecisionActionOutcome
{
    /// <summary>The action was carried out.</summary>
    Done = 0,

    /// <summary>No generated value has that id (it may have been deleted with its object or its flow).</summary>
    NotFound = 1,

    /// <summary>The value exists but is not waiting on a decision, so there is nothing to act on.</summary>
    NotWaitingForDecision = 2
}

/// <summary>
/// How allowing the rename of a held generated value changes one Connected System it is exported to (Unique Value
/// Generation, #242, release 4, Phase 9): what the "Allow the rename" confirmation names for each system. Not persisted.
/// </summary>
public enum GeneratedValueRenameChangeKind
{
    /// <summary>An account there already holds the value, so the new value renames it.</summary>
    Rename = 0,

    /// <summary>The object is being provisioned there, so the account is created with the new value.</summary>
    Create = 1,

    /// <summary>An account there is joined but does not hold the value, so it is updated with the new value.</summary>
    Update = 2
}
