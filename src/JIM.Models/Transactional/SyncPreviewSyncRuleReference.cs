// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A lightweight reference to a Synchronisation Rule that participated in a preview (#288, PRD requirement 1's
/// AffectedSyncRules): id plus a name snapshot, so the result serialises without entity graphs and survives a
/// later rename.
/// </summary>
public class SyncPreviewSyncRuleReference
{
    /// <summary>
    /// The Synchronisation Rule's id.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Snapshot of the rule's name at preview time.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
