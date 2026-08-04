// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// The cascade seam a <see cref="CausalEdge"/> records: what kind of relationship holds between the
/// cause and the effect. Persisted by ordinal and therefore append-only; see
/// <c>CausalEdgeTypeOrdinalTests</c>.
/// <para>
/// Every seam the PRD enumerates has a member here from the outset, because the taxonomy is
/// structural and known, even though the worker only writes some of them until later phases land.
/// Note the deliberate absence of a "Pending Export queued caused it to be exported" member:
/// <see cref="ActivityRunProfileExecutionItem.PendingExportId"/> already expresses that hop, and
/// duplicating an existing link as an edge is exactly what the PRD forbids.
/// </para>
/// </summary>
public enum CausalEdgeType
{
    /// <summary>
    /// A Connected System Object leaving a Synchronisation Rule's scoping criteria caused it to be
    /// disconnected from its Metaverse Object.
    /// </summary>
    ScopeLossCausedDisconnect = 0,

    /// <summary>
    /// A disconnect (or the last qualifying disconnect) caused the Metaverse Object Type's Deletion
    /// Rule to fire, deleting or scheduling deletion of the Metaverse Object.
    /// </summary>
    DisconnectCausedMetaverseObjectDeletion = 1,

    /// <summary>
    /// A Metaverse Object deletion caused a delete-type Pending Export to be staged against one of
    /// the object's own provisioned Connected System Objects.
    /// </summary>
    MetaverseObjectDeletionCausedDeprovision = 2,

    /// <summary>
    /// A Metaverse Object deletion caused reference recall to stage a Pending Export removing a
    /// reference to it from another object. This is the seam the PRD's worked example turns on, and
    /// the only one where cause and effect sit on two entirely different objects.
    /// </summary>
    MetaverseObjectDeletionCausedReferenceRemoval = 3,

    /// <summary>
    /// An executed export caused the confirming outcome recorded on the next import. This hop needs
    /// an edge because reconciliation correlates only by Connected System Object id, and an object
    /// can cycle through export and import repeatedly, so an id-only join can pick the wrong cycle.
    /// </summary>
    ExportCausedImportConfirmation = 4
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
    /// The Connected System Object no longer satisfied its inbound Synchronisation Rule's scoping
    /// criteria.
    /// </summary>
    LeftSynchronisationRuleScope = 1,

    /// <summary>
    /// Deletion Rule "when last connector disconnects": the final joined Connected System Object was
    /// disconnected.
    /// </summary>
    LastConnectorDisconnected = 2,

    /// <summary>
    /// Deletion Rule "when authoritative source disconnects", but no authoritative sources were
    /// configured, so evaluation fell back to last-connector behaviour. Distinguished from
    /// <see cref="LastConnectorDisconnected"/> because the fallback signals a misconfiguration an
    /// administrator investigating a deletion cascade will want to see.
    /// </summary>
    LastConnectorDisconnectedNoSourcesConfigured = 3,

    /// <summary>
    /// Deletion Rule "when authoritative source disconnects" in All Sources mode: the last
    /// authoritative source disconnected and none remain connected.
    /// </summary>
    AllAuthoritativeSourcesDisconnected = 4,

    /// <summary>
    /// Deletion Rule "when authoritative source disconnects" in Specific Sources mode: a listed
    /// authoritative source disconnected.
    /// </summary>
    AuthoritativeSourceDisconnected = 5
}
