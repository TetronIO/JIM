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
