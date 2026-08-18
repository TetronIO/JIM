// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One advisory or blocking condition a preview surfaced (#288, PRD requirements 1 and 16). The
/// machine-readable <see cref="Code"/> is what consumers branch on; <see cref="Detail"/> is for people.
/// Blocking-ness is positional: a message in <see cref="SyncPreviewResult.Errors"/> would prevent the real
/// sync, one in <see cref="SyncPreviewResult.Warnings"/> would not.
/// </summary>
public class SyncPreviewMessage
{
    /// <summary>
    /// The machine-readable condition code.
    /// </summary>
    public SyncPreviewMessageCode Code { get; set; }

    /// <summary>
    /// Human-readable detail of the condition.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// The Synchronisation Rule the condition arose under, when one is attributable.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the attributed Synchronisation Rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The Connected System the condition concerns, when one is attributable.
    /// </summary>
    public int? ConnectedSystemId { get; set; }
}
