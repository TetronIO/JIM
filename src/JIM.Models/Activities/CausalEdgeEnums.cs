// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// The cascade seam a <see cref="CausalEdge"/> records: what kind of relationship holds between the
/// cause and the effect. Persisted by ordinal and therefore append-only; see
/// <c>CausalEdgeTypeOrdinalTests</c>.
/// <para>
/// Only hops that cross a Run Profile Execution Item or Activity boundary appear here. A sync outcome
/// tree is itself a causal structure, parenting each consequence under the event that caused it, so a
/// hop within one item needs no edge and must not get one: duplicating a link that is already
/// persisted is exactly what the PRD forbids where it rejects the queueing-to-executing hop
/// (<see cref="ActivityRunProfileExecutionItem.PendingExportId"/> already expresses it). Scope loss to
/// disconnect, disconnect to Deletion Rule firing, and the nested case of deletion to deprovisioning
/// all fail that test and are deliberately absent.
/// </para>
/// </summary>
public enum CausalEdgeType
{
    /// <summary>
    /// A Metaverse Object deletion caused a delete-type Pending Export to be staged against one of
    /// the object's own provisioned Connected System Objects, in the case where the export could not
    /// be recorded under the deletion's own outcome and became a standalone item of its own.
    /// </summary>
    MetaverseObjectDeletionCausedDeprovision = 0,

    /// <summary>
    /// A Metaverse Object deletion caused reference recall to stage a Pending Export removing a
    /// reference to it from another object. This is the seam the PRD's worked example turns on, and
    /// the only one where cause and effect sit on two entirely different objects.
    /// </summary>
    MetaverseObjectDeletionCausedReferenceRemoval = 1,

    /// <summary>
    /// An executed export caused the confirming outcome recorded on the next import. This hop needs
    /// an edge because reconciliation correlates only by Connected System Object id, and an object
    /// can cycle through export and import repeatedly, so an id-only join can pick the wrong cycle.
    /// </summary>
    ExportCausedImportConfirmation = 2,

    /// <summary>
    /// The synchronisation that staged a Pending Export caused the export run that carried it out. The
    /// two sit in different Activities, minutes or days apart, so the executing item cannot otherwise say
    /// why it exported anything.
    /// </summary>
    /// <remarks>
    /// The PRD expected this hop to be free, on the grounds that
    /// <see cref="ActivityRunProfileExecutionItem.PendingExportId"/> already links the queueing item to the
    /// executing one. It does not. That column is populated only on a <c>PendingExport</c>-type item (a
    /// provisioning export with no Connected System Object yet) and is null on every ordinary <c>Exported</c>
    /// item, so there is nothing to walk back along; the export path had no cause at all, which is the very
    /// defect this feature exists to remove.
    ///
    /// An edge is also the only durable answer. The Pending Export row is deleted the moment the export
    /// succeeds, so a link derived from it after the fact could never be resolved.
    /// </remarks>
    PendingExportQueueingCausedExportExecution = 3
}

/// <summary>
/// Why a <see cref="CausalEdge"/>'s cause produced its effect, as a code rather than a sentence.
/// Together with the edge type, Connected System and Synchronisation Rule this forms the
/// **attribution tuple** that cohort grouping keys on. Persisted by ordinal and therefore
/// append-only; see <c>CausalReasonCodeOrdinalTests</c>.
/// <para>
/// This exists as an enum specifically so that grouping never keys on prose.
/// <c>SyncEngine.EvaluateMvoDeletionRule</c> builds its reason as free text with the Connected
/// System name interpolated in, which the tuple already carries separately; grouping on that string
/// would be redundant, would change behaviour silently whenever the wording changed, and would
/// collapse every cohort to a single member the moment a per-object element (a grace period date, an
/// object name) entered it, with no error anywhere. The displayed sentence is derived at render time
/// from this code plus the snapshot names.
/// </para>
/// <para>
/// Deliberately minimal: only the codes the seams implemented so far need. Codes for the remaining
/// seams are appended as those seams gain capture, which is free because the enum is append-only.
/// </para>
/// </summary>
public enum CausalReasonCode
{
    /// <summary>
    /// No reason recorded. Only valid on an edge whose seam has no sub-variants worth distinguishing.
    /// </summary>
    NotSet = 0,

    /// <summary>
    /// Deletion Rule "when last connector disconnects": the final joined Connected System Object was
    /// disconnected.
    /// </summary>
    LastConnectorDisconnected = 1,

    /// <summary>
    /// Deletion Rule "when authoritative source disconnects", but no authoritative sources were
    /// configured, so evaluation fell back to last-connector behaviour. Distinguished from
    /// <see cref="LastConnectorDisconnected"/> because the fallback signals a misconfiguration an
    /// administrator investigating a deletion cascade will want to see.
    /// </summary>
    LastConnectorDisconnectedNoSourcesConfigured = 2,

    /// <summary>
    /// Deletion Rule "when authoritative source disconnects" in All Sources mode: the last
    /// authoritative source disconnected and none remain connected.
    /// </summary>
    AllAuthoritativeSourcesDisconnected = 3,

    /// <summary>
    /// Deletion Rule "when authoritative source disconnects" in Specific Sources mode: a listed
    /// authoritative source disconnected.
    /// </summary>
    AuthoritativeSourceDisconnected = 4
}
