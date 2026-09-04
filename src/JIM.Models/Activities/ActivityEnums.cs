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
    DeprovisionQueued,

    /// <summary>
    /// Preview only: the Connected System Object would leave import scope and is joined to a Metaverse Object, so
    /// the obsoletion that follows disconnects the two and recalls whatever the object contributed.
    ///
    /// Separate from <see cref="WouldFallOutOfScope"/>, which covers the unjoined objects leaving scope, because
    /// the two have entirely different consequences and an administrator consents to them differently. An unjoined
    /// object leaving scope loses JIM nothing; a joined one takes its contributed attribute values out of the
    /// Metaverse with it and may leave its Metaverse Object with no connectors at all.
    /// </summary>
    WouldDisconnectFromMetaverseObject,

    /// <summary>
    /// Preview only (#1115): the next synchronisation would stage a Pending Export of type Delete for this object,
    /// removing it from its target Connected System. The preview sibling of <see cref="DeprovisionQueued"/>, used
    /// where a proposed Outbound Deprovision Action of Delete would turn an out-of-scope disconnection into a
    /// deletion in the target system; the destructive count an administrator consents to.
    /// </summary>
    WouldStageDeleteExport,

    /// <summary>
    /// Preview only (#1115): a joined object that the current configuration would disconnect on its next
    /// synchronisation would instead keep its Metaverse Object join under the proposal ("once managed, always
    /// managed"). The relaxing direction of the Inbound Out-of-Scope Action toggle; nothing is destroyed, and the
    /// preview states it so the administrator knows the disconnections they may have been expecting stop.
    /// </summary>
    WouldRemainJoined,

    /// <summary>
    /// Preview only (#1115): the object is joined and in scope today, so nothing happens to it on save, but the
    /// fate a future scope exit would hand it changes with the proposed Outbound Deprovision Action (disconnect
    /// versus delete in the target system). The exposure tier of the destructive-toggle preview: it counts every
    /// object the changed action now stands over, which is the number that makes "3,400 objects in this system
    /// move from Disconnect to Delete" readable at a glance. The direction of the change is carried in the
    /// delta's old and new values.
    /// </summary>
    WouldChangeDeprovisionAction,

    /// <summary>
    /// Preview only (#1437): the proposed Attribute Flow would not evaluate for this object, so the attribute it
    /// targets is not written at all. The Expression threw, a required input has no value under a Missing Input
    /// Behaviour that fails, or a multi-valued source flows to a single-valued target.
    ///
    /// Its own transition rather than an absent delta, because an Expression that fails on one object in a thousand
    /// is exactly what an Attribute Flow preview exists to find: reported as no change, the failing objects would
    /// be indistinguishable from the ones the edit does not touch. Only failures the proposal introduces are
    /// counted; one the stored configuration already has is not this change's doing.
    /// </summary>
    WouldFailAttributeFlow,

    /// <summary>
    /// Preview only (#1457): the Connected System Object joins to one Metaverse Object under the stored Object
    /// Matching Rules and would join to a different one under the proposal. The most dangerous transition a
    /// matching change can produce, because nothing about it fails: the account simply becomes part of the wrong
    /// identity, and every attribute it contributes goes with it. The old and new values name the two Metaverse
    /// Objects.
    /// </summary>
    WouldJoinDifferentMetaverseObject,

    /// <summary>
    /// Preview only (#1457): the Connected System Object matches nothing today, so its next synchronisation would
    /// project a new Metaverse Object for it, and under the proposal it would join an existing one instead. The
    /// benign direction of a matching change, and the one an administrator widening a rule is usually aiming for.
    /// </summary>
    WouldJoinInsteadOfProject,

    /// <summary>
    /// Preview only (#1457): the inverse, and a duplicate-identity risk. The Connected System Object matches a
    /// Metaverse Object today and would match nothing under the proposal, so its next synchronisation would project
    /// a second Metaverse Object beside the one it should have joined.
    /// </summary>
    WouldProjectInsteadOfJoin,

    /// <summary>
    /// Preview only (#1457): the proposed Object Matching Rules match more than one Metaverse Object for this
    /// object, so its next synchronisation fails it with an ambiguous match rather than joining it to anything. Its
    /// own transition rather than a validation finding, because ambiguity is a property of the data and not of the
    /// rule: a rule that is unique across every object but two is invisible until those two are counted.
    /// </summary>
    WouldMatchAmbiguously,

    /// <summary>
    /// Preview only (#1462): the object would have had a new Metaverse Object projected for it on the next
    /// synchronisation, and under the proposal it would not. It stays in the connector space unmanaged, which is
    /// what turning Project To Metaverse off, or disabling the rule that does the projecting, actually means.
    ///
    /// Its own transition rather than an absent delta because the objects concerned are the ones an administrator
    /// was expecting identities for; reported as no change they would be indistinguishable from the objects the
    /// edit does not touch.
    /// </summary>
    WouldStopProjecting,

    /// <summary>
    /// Preview only (#1462): the target-system inverse. The Metaverse Object would have had an object provisioned
    /// for it in the target Connected System and under the proposal would not, so the account an administrator was
    /// expecting is never created. Nothing existing is destroyed, which is exactly why it goes unnoticed.
    /// </summary>
    WouldStopProvisioning,

    /// <summary>
    /// Preview only (#1462): the object's divergence from what JIM holds would no longer be corrected, because the
    /// export rule that stands over it would stop enforcing state. The count of objects free to drift from the
    /// moment the change is saved.
    /// </summary>
    WouldStopCorrectingDrift,

    /// <summary>
    /// Preview only (#1475): the object would stop being imported, and nothing else would happen to it. Deselecting
    /// its Object Type removes the type from deletion detection, so the object is never compared against an import
    /// again: it stays joined to its Metaverse Object and keeps contributing the values it last imported, which stop
    /// being refreshed.
    ///
    /// Where the delta names an attribute, the same freeze at attribute granularity: the Connector stops fetching
    /// that attribute and the values already held for it stay exactly as they are.
    ///
    /// Its own transition rather than a disconnection or an obsoletion, because it is neither. An object that keeps
    /// contributing stale values while nothing reports it is the failure this preview exists to make visible, and
    /// borrowing either of those words would describe a cascade that does not happen. See #1474.
    /// </summary>
    WouldStopBeingImported,

    /// <summary>
    /// Preview only (#1475): the inverse. The object, or the attribute the delta names, would start being imported
    /// again, so values that had frozen resume tracking the Connected System from the next Import Run Profile.
    /// </summary>
    WouldResumeBeingImported,

    /// <summary>
    /// Preview only (#1475): the Metaverse Object would have the values this Connected System contributed withdrawn
    /// when its obsolete object is next synchronised, where today they would be left in place. What turning Remove
    /// Contributed Attributes On Obsoletion on means for the objects already waiting: a surviving contributor is
    /// re-elected where there is one, and the value is cleared where there is not.
    /// </summary>
    WouldWithdrawContributedValues,

    /// <summary>
    /// Preview only (#1475): the inverse. The values this Connected System contributed would be left on the
    /// Metaverse Object when its obsolete object is next synchronised, rather than withdrawn. They stop tracking
    /// anything at that point, which is the part a proposal that reads as "keep the data" does not say.
    /// </summary>
    WouldRetainContributedValues,

    /// <summary>
    /// A disconnection left the Metaverse Object with no remaining Connected System carrying an enabled import
    /// Synchronisation Rule for its type, so the departed system's sole-contributed values were preserved as last
    /// known state rather than recalled (#1570): recalling them would blank live target accounts and feed
    /// expression-based mappings (such as a Distinguished Name) with nothing. Emitted during obsoletion and
    /// out-of-scope disconnection so an administrator can see why the values remain; the sibling of
    /// <see cref="NoContributor"/>, which reports the values that were genuinely cleared.
    /// </summary>
    ValuesPreserved,

    /// <summary>
    /// A scheduled grace-period Metaverse Object deletion was cancelled because a Connected System Object
    /// rejoined the Metaverse Object from a source whose rejoin the deletion rule's trigger mode recognises
    /// as undoing the disconnection that triggered it (#119). Emitted on the rejoining item as a child of
    /// its <see cref="Joined"/> outcome, so the Lineage records why the object survived its scheduled
    /// deletion rather than leaving the reader to notice the deletion was scheduled and never happened
    /// (#1620).
    /// </summary>
    MvoDeletionCancelled
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
    OutcomeType = 4,

    /// <summary>
    /// Entries an import read from the Connected System and discarded because an excluded Container carved them
    /// out (#1255), keyed by that Container's id.
    /// </summary>
    /// <remarks>
    /// The one dimension that is <b>not</b> derived from Run Profile Execution Items, and the only reason it is
    /// here rather than in a table of its own: a discarded entry produced no item, by definition. It has the same
    /// shape as every other counter, is written by the same incremental upsert (which matters, because an import
    /// reports these per page), and is read by the same query. What it costs is that finalisation can no longer
    /// recompute the whole counter set from the item tables, so
    /// <see cref="RunProfileExecutionStatsDimensions.RecomputedFromExecutionItems"/> names the dimensions it owns
    /// and leaves this one alone.
    /// </remarks>
    ExcludedContainer = 5
}
