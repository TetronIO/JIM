// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// One Metaverse attribute value change inbound Attribute Flow would make, as returned within a Sync
/// Preview's inbound summary: the attribute, the change direction, the value as a display string, and the
/// contributing Synchronisation Rule.
/// </summary>
public class SyncPreviewAttributeFlowChangeDto
{
    /// <summary>
    /// The Metaverse Attribute's id.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// Snapshot of the attribute's name.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// True when the value would be added or set; false when it would be removed or cleared.
    /// </summary>
    public bool IsAddition { get; set; }

    /// <summary>
    /// The value that would be added or removed, rendered for display.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose Attribute Flow contributed the change, when attributable.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing rule's name.
    /// </summary>
    public string? SyncRuleName { get; set; }

    public static SyncPreviewAttributeFlowChangeDto FromModel(SyncPreviewAttributeFlowChange model) => new()
    {
        AttributeId = model.AttributeId,
        AttributeName = model.AttributeName,
        IsAddition = model.IsAddition,
        Value = model.Value,
        SyncRuleId = model.SyncRuleId,
        SyncRuleName = model.SyncRuleName
    };
}
