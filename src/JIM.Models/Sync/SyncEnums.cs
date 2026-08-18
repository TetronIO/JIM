// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// Describes the fate of a Metaverse Object after its deletion rule was evaluated
/// during CSO disconnection. Used to build the appropriate causality tree outcome.
/// </summary>
public enum MvoDeletionFate
{
    /// <summary>MVO was not marked for deletion (Manual rule, remaining connectors, non-authoritative source, etc.).</summary>
    NotDeleted,
    /// <summary>MVO was queued for immediate synchronous deletion (0 grace period).</summary>
    DeletedImmediately,
    /// <summary>MVO was marked for deferred deletion by housekeeping (grace period configured).</summary>
    DeletionScheduled
}

/// <summary>
/// Why an MVO-deletion export decision came out the way it did (#288 outbound extraction; the #655 semantics).
/// The disconnect itself is unconditional and is not part of this verdict: every joined CSO is disconnected when
/// its Metaverse Object is deleted, and this reason explains only whether a Delete export was staged besides.
/// </summary>
public enum MvoDeletionExportReason
{
    /// <summary>The Metaverse Object carries no Type, so no export Synchronisation Rule can be matched. The remedy is the object, not the rules.</summary>
    NoMetaverseObjectType,
    /// <summary>No enabled export Synchronisation Rule matches the CSO's (Connected System, Connected System Object Type) pair.</summary>
    NoMatchingExportRule,
    /// <summary>Rules match, and every one of them says Disconnect rather than Delete.</summary>
    MatchingRulesDeclineDeletion,
    /// <summary>A matching rule with OutboundDeprovisionAction.Delete won; Delete beats Disconnect when rules conflict (#655).</summary>
    DeleteRuleWon
}

/// <summary>
/// What an export Synchronisation Rule's OutboundDeprovisionAction means for a CSO that has fallen out of the
/// rule's scope (#288 outbound extraction). Unlike an MVO deletion, where disconnection is unconditional, here
/// the action decides everything: an unrecognised action does nothing at all rather than guessing.
/// </summary>
public enum OutOfScopeDeprovisioningAction
{
    /// <summary>Break the join between the CSO and the MVO; the object stays in the Connected System.</summary>
    Disconnect,
    /// <summary>Stage a Delete export for the CSO (and break the join), per the rule's Delete action.</summary>
    StageDeleteExport,
    /// <summary>The rule carries an OutboundDeprovisionAction this engine does not recognise; do nothing, visibly.</summary>
    UnknownAction
}

/// <summary>
/// What kind of export, if any, a Metaverse Object change stages against one export Synchronisation Rule's
/// target (#288 outbound extraction; the verdict behind the <c>CreateOrUpdatePendingExport*</c> entry points).
/// </summary>
public enum OutboundStagingOutcome
{
    /// <summary>The Metaverse Object's one CSO in this Connected System is of a different Connected System
    /// Object Type than the rule targets (#1331); the conflict is reported and nothing is staged.</summary>
    ObjectTypeConflict,
    /// <summary>Reference recall found no exportable presence in the target (#1003); nothing is staged and
    /// nothing is provisioned.</summary>
    RecallSkippedNoTargetPresence,
    /// <summary>The target holds no presence for the object (or only a pending provisioning) and the rule
    /// does not provision; nothing is staged.</summary>
    ProvisioningDeclined,
    /// <summary>The rule wants a presence created: a Create export. The orchestrator interposes export
    /// matching first, and a matched CSO turns this into an update instead.</summary>
    ProvisionNewCso,
    /// <summary>A pending provisioning CSO already exists and the changes are relevant to this rule; its
    /// Create export is restaged from the latest Metaverse Object state.</summary>
    ReusePendingProvisioningCso,
    /// <summary>A pending provisioning CSO exists but none of the changed attributes map to this rule;
    /// restaging would misattribute the existing Create export in the causality tree, so nothing is staged.</summary>
    PendingProvisioningChangesIrrelevant,
    /// <summary>The object exists in the target: an Update export carrying only the changed attributes.</summary>
    UpdateExistingCso
}

/// <summary>
/// How reference recall changes combined with a Pending Export already attached to the target CSO (#288
/// outbound extraction; the #908/#1003 semantics).
/// </summary>
public enum RecallPendingExportMergeOutcome
{
    /// <summary>Stage the recall changes (merged with any existing Update export's surviving changes).</summary>
    Proceed,
    /// <summary>An existing Delete export wins: the object is being deprovisioned, so a membership removal is
    /// moot, and replacing the Delete would leave the object alive in the target forever (#1003).</summary>
    SkippedDeleteSupersedes,
    /// <summary>An existing Create export is protected: recall never provisions, and replacing a provisioning
    /// export with a recall Update would silently lose it. Defensive; the pending-provisioning filter makes
    /// this unreachable in practice.</summary>
    SkippedCreateProtected
}

/// <summary>
/// What kind of outbound decision an <c>OutboundPreviewEntry</c> records (#288 plan Phase 2).
/// </summary>
public enum OutboundPreviewEntryKind
{
    /// <summary>An in-scope staging decision: what export, if any, would be staged against the rule's target.</summary>
    Staging,
    /// <summary>An out-of-scope deprovisioning decision for a joined target object.</summary>
    Deprovisioning
}
