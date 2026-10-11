// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One outbound disconnection recorded by an export evaluation run: a Disconnect Deprovisioning Action broke the join
/// to a Connected System Object in a target system and left the object itself in place, either because its Metaverse
/// Object left an export Synchronisation Rule's scope or because the Metaverse Object was deleted. Immutable, and
/// recorded on <see cref="ExportEvaluationWorkingSet"/> only after the broken join has been persisted, so a failed
/// write cannot leave a disconnection on record that never happened.
/// </summary>
/// <param name="ConnectedSystemObjectId">The id of the Connected System Object that was disconnected.</param>
/// <param name="ConnectedSystemId">The Connected System the disconnected object belongs to.</param>
/// <param name="MetaverseObjectId">The Metaverse Object the object was disconnected from.</param>
/// <param name="SyncRuleId">The export Synchronisation Rule whose Deprovisioning Action decided to leave the object in
/// place, so the outcome reporting it can say why the account was not deleted (#1811).</param>
/// <param name="SyncRuleName">That rule's name, snapshotted with it so the outcome still reads once the rule is
/// renamed or deleted.</param>
public sealed record OutboundDisconnection(
    Guid ConnectedSystemObjectId,
    int ConnectedSystemId,
    Guid MetaverseObjectId,
    int SyncRuleId,
    string SyncRuleName);
