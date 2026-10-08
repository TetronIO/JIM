// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Changes withdrawn from an object's Pending Export by an export evaluation, because the target Connected System
/// Object already holds the values the Metaverse now wants (#2001). A change queued by an earlier synchronisation
/// that no longer reflects the Metaverse must not be exported, so no-net-change detection withdraws it; this records
/// what was withdrawn so the run can report it on the execution item of the object whose change caused it, and the
/// previews can show the same outcome. A change superseded by a newly staged one for the same attribute is replaced,
/// not withdrawn, and is never listed here.
/// </summary>
/// <param name="ConnectedSystemObjectId">The target Connected System Object whose Pending Export lost the changes.</param>
/// <param name="ConnectedSystemId">The Connected System the target object belongs to.</param>
/// <param name="SyncRuleId">The export Synchronisation Rule whose evaluation found the changes already current.</param>
/// <param name="SyncRuleName">That Synchronisation Rule's name, for attribution on the outcome.</param>
/// <param name="MetaverseObjectId">The Metaverse Object whose evaluation withdrew the changes.</param>
/// <param name="Changes">The withdrawn changes, as they stood on the Pending Export.</param>
public sealed record PendingExportChangesWithdrawal(
    Guid ConnectedSystemObjectId,
    int ConnectedSystemId,
    int SyncRuleId,
    string SyncRuleName,
    Guid MetaverseObjectId,
    IReadOnlyList<PendingExportAttributeValueChange> Changes);
