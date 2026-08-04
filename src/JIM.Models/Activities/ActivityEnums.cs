// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// High-level outcome stat types used for filtering activities by their outcome columns.
/// Maps to the granular summary stat fields on Activity (TotalAdded, TotalUpdated, etc.).
/// </summary>
public enum ActivityOutcomeType
{
    Added,
    Updated,
    Deleted,
    Projected,
    Joined,
    AttributeFlows,
    Disconnected,
    DriftCorrections,
    Provisioned,
    Exported,
    Deprovisioned,
    Created,
    PendingExports,
    Errors
}

/// <summary>
/// The type of outcome recorded in an RPEI sync outcome node.
/// Covers all three Run Profile types: import, sync, and export.
/// </summary>
public enum ActivityRunProfileExecutionItemSyncOutcomeType
{
    // Import outcomes
    CsoAdded,
    CsoUpdated,
    CsoDeleted,
    DeletionDetected,

    // Import outcomes — confirming import (export confirmation)
    ExportConfirmed,
    ExportFailed,

    // Sync outcomes — inbound
    Projected,
    Joined,
    AttributeFlow,
    Disconnected,
    DisconnectedOutOfScope,
    MvoDeleted,
    DriftCorrection,

    // Sync outcomes — outbound (Pending Export creation during sync)
    Provisioned,
    PendingExportCreated,

    // Export execution outcomes
    Exported,
    Deprovisioned,

    // Added after initial release — appended to preserve existing database ordinals
    MvoDeletionScheduled,

    /// <summary>
    /// Attribute priority (#91): a connected, in-scope contributor with "Null is a value" set positively asserted
    /// "no value" for an attribute, persisting an asserted-null marker that clears the attribute downstream. Emitted
    /// during inbound attribute flow so an admin can see a blank was deliberately asserted, not merely uncontributed.
    /// </summary>
    AssertedNull,

    /// <summary>
    /// Attribute priority (#91): an attribute value was cleared because no contributor supplied a replacement; the
    /// last contributing rule stopped providing a value (withdrew it, or its Connected System Object was obsoleted
    /// with no surviving contributor to re-elect) and nothing asserted the blank. Emitted during inbound attribute
    /// flow and attribute recall; an attribute that was already blank reports nothing.
    /// </summary>
    NoContributor,

    // Configuration change preview (#827). A preview describes what a proposed configuration *would* do, and its
    // per-object deltas need a vocabulary for transitions that no synchronisation run ever performs. They live here
    // rather than in a parallel enum so that a preview delta and the sync outcome it anticipates are the same value,
    // and a reader of either does not have to learn two vocabularies. Nothing writes these during a run.

    /// <summary>
    /// Preview only: the object is out of scope today and the proposed configuration would bring it into scope.
    /// </summary>
    WouldFallInScope,

    /// <summary>
    /// Preview only: the object is in scope today and the proposed configuration would take it out of scope. What
    /// then happens to it is governed by the rule's Inbound Out-of-Scope Action.
    /// </summary>
    WouldFallOutOfScope,

    /// <summary>
    /// Preview only: the Metaverse Object does not satisfy its type's deletion rule today, and would under the
    /// proposed configuration. Deletion eligibility takes effect on save, so this is the transition a Metaverse
    /// Object Type deletion-settings preview exists to surface.
    /// </summary>
    WouldBecomeDeletionEligible,

    /// <summary>
    /// Preview only: the inverse. The Metaverse Object is eligible for deletion today and would cease to be, which
    /// is what a proposal that relaxes a deletion rule needs to state as plainly as one that tightens it.
    /// </summary>
    WouldCeaseToBeDeletionEligible,

    /// <summary>
    /// Preview only: the Metaverse Object is on its way to deletion both before and after the proposal, but the
    /// date it would be deleted on moves. Separate from the two above because it is a different question: those
    /// answer "would this delete objects that are safe today", this answers "would this bring forward, push back,
    /// or cancel a deletion already scheduled". A grace period edited from 30 days to 7 deletes nobody today and
    /// changes the fate of everyone already waiting.
    /// </summary>
    WouldChangeDeletionEligibleDate,

    /// <summary>
    /// A Pending Export whose change type is Delete was staged during synchronisation: the object is queued to be
    /// removed from the target Connected System on its next export run. The delete-flavoured sibling of
    /// <see cref="PendingExportCreated"/>, which covers every other change type.
    ///
    /// The distinction exists because a delete Pending Export carries the Connected System Object's secondary
    /// external ID (the DN, for LDAP) as an attribute value change, so the connector can still resolve the target
    /// after the object is disconnected from its Metaverse Object and possibly housekept away. Reported as a plain
    /// Pending Export, that payload read as "one attribute set" and a deprovisioning cascade was indistinguishable
    /// from an attribute update.
    ///
    /// Counts towards an Activity's Pending Export totals exactly as <see cref="PendingExportCreated"/> does; it
    /// is one, and only its intent differs.
    /// </summary>
    DeprovisionQueued
}

/// <summary>
/// Controls how much detail is recorded for sync outcome graphs on each RPEI.
/// Higher levels provide richer audit trails but increase storage usage.
/// </summary>
public enum ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel
{
    /// <summary>
    /// No outcome tree — RPEI ObjectChangeType only (legacy behaviour).
    /// Maximum performance, minimal storage.
    /// </summary>
    None,

    /// <summary>
    /// Root-level outcomes only (Projected, Joined, Exported, etc.) — no nested children.
    /// Enables stat chips on list view with basic causal visibility.
    /// </summary>
    Standard,

    /// <summary>
    /// Full tree with nested children (Projected -> AttributeFlow -> PendingExportCreated per system).
    /// Default. Full audit trail, debugging, compliance.
    /// </summary>
    Detailed
}

/// <summary>
/// How a phase of a Run Profile execution turned out (#454). A phase is recorded as Pending when
/// the run starts and moves on from there as the run progresses.
/// </summary>
public enum ActivityPhaseStatus
{
    /// <summary>Declared for this run, not reached yet.</summary>
    Pending = 0,

    /// <summary>Currently running.</summary>
    Active = 1,

    /// <summary>Ran and finished.</summary>
    Completed = 2,

    /// <summary>
    /// Never ran, because the run did not need it. A Delta Import performs no deletion detection;
    /// a file-based import opens no connection. Recorded when a later phase is entered.
    /// </summary>
    Skipped = 3,

    /// <summary>The run failed or was cancelled while this phase was running.</summary>
    Failed = 4
}

/// <summary>
/// The dimension a persisted Activity stat counter row counts along. Each Run Profile Activity's
/// execution stats are maintained as incremental (ActivityId, Dimension, Key) counter rows so the
/// stats read is O(counter rows) instead of aggregating every Run Profile Execution Item; see
/// <see cref="ActivityStatCounter"/>.
/// </summary>
public enum ActivityStatDimension
{
    /// <summary>Counts per RPEI <see cref="JIM.Models.Enums.ObjectChangeType"/> (key: the enum's integer value).</summary>
    ObjectChangeType = 0,

    /// <summary>Counts per resolved object type name (key: the type name).</summary>
    ObjectTypeName = 1,

    /// <summary>Counts per RPEI <see cref="ActivityRunProfileExecutionItemErrorType"/>, excluding NotSet (key: the enum's integer value).</summary>
    ErrorType = 2,

    /// <summary>Counts per <see cref="JIM.Models.Enums.NoChangeReason"/> on NoChange RPEIs (key: the enum's integer value).</summary>
    NoChangeReason = 3,

    /// <summary>Counts per <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/> across the Activity's sync outcome rows (key: the enum's integer value).</summary>
    OutcomeType = 4
}
