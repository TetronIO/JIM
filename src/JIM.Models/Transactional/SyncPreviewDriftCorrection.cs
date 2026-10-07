// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One attribute a synchronisation would correct on the object being synchronised, because an export rule to its own
/// Connected System enforces state and the value there has drifted from what the Metaverse Object says (#1530).
/// </summary>
public class SyncPreviewDriftCorrection
{
    /// <summary>
    /// The Connected System Object Type attribute corrected.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// Snapshot of the attribute's name.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// The value the object holds now, rendered for display; null when it holds none. Personal data: never log it.
    /// </summary>
    public string? CurrentValue { get; set; }

    /// <summary>
    /// The value the correction would write, rendered for display; null when the correction clears it.
    /// </summary>
    public string? CorrectedValue { get; set; }

    /// <summary>
    /// The export Synchronisation Rule whose enforced state the correction restores.
    /// </summary>
    public int SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of that rule's name.
    /// </summary>
    public string SyncRuleName { get; set; } = string.Empty;
}
