// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of changing a Metaverse Attribute's data type or plurality to a proposed pair. While any
/// Metaverse Object holds a value for the attribute, the only change allowed is widening it from single-valued to
/// multi-valued with its data type unchanged; every other change is refused. Submitting the current data type and
/// plurality is no change at all, so it is never refused.
/// </summary>
/// <remarks>
/// Returned by both the preview and the execute method so REST and PowerShell callers get the same block/allow
/// decision. The widening is lossless because every stored value is still a valid member of the wider set, and lossless
/// for export because the export change type follows the target attribute's plurality (#2153, D3 and F4).
/// </remarks>
public class AttributeSchemaChangeImpact
{
    /// <summary>
    /// The ID of the Metaverse Attribute.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The name of the Metaverse Attribute.
    /// </summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the attribute is built-in. Built-in attributes can never be changed.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The attribute's data type before the change.
    /// </summary>
    public AttributeDataType CurrentType { get; set; }

    /// <summary>
    /// The attribute's plurality before the change.
    /// </summary>
    public AttributePlurality CurrentPlurality { get; set; }

    /// <summary>
    /// The data type the change asked for.
    /// </summary>
    public AttributeDataType ProposedType { get; set; }

    /// <summary>
    /// The plurality the change asked for.
    /// </summary>
    public AttributePlurality ProposedPlurality { get; set; }

    /// <summary>
    /// The total number of Metaverse Objects that hold a stored value for the attribute.
    /// </summary>
    public int TotalObjectsWithValues { get; set; }

    /// <summary>
    /// True when the requested data type and plurality are now in effect: the change was made, or they were already
    /// the attribute's own and there was nothing to change. False when the change was refused, or when the impact was
    /// only previewed.
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// True when the requested data type and plurality are the attribute's current ones, so there is nothing to change.
    /// Stored values never block this, and nothing is written or recorded in the change history.
    /// </summary>
    public bool IsUnchanged => ProposedType == CurrentType && ProposedPlurality == CurrentPlurality;

    /// <summary>
    /// True when the change keeps the data type and goes from single-valued to multi-valued. This is the one change
    /// allowed while Metaverse Objects hold values, because every stored value remains valid.
    /// </summary>
    public bool IsLosslessWidening => IsLosslessWideningChange(CurrentType, CurrentPlurality, ProposedType, ProposedPlurality);

    /// <summary>
    /// True when the change is refused because Metaverse Objects hold values and it is neither a lossless widening nor
    /// a request for the current data type and plurality: a data type change, or a change from multi-valued to
    /// single-valued.
    /// </summary>
    public bool BlockedByValues => TotalObjectsWithValues > 0 && !IsUnchanged && !IsLosslessWidening;

    /// <summary>
    /// Whether changing an attribute from one (type, plurality) pair to another is a lossless widening: the same data
    /// type, from single-valued to multi-valued. The single statement of the rule, shared by the server and the portal.
    /// </summary>
    public static bool IsLosslessWideningChange(AttributeDataType currentType, AttributePlurality currentPlurality,
        AttributeDataType proposedType, AttributePlurality proposedPlurality) =>
        proposedType == currentType &&
        currentPlurality == AttributePlurality.SingleValued &&
        proposedPlurality == AttributePlurality.MultiValued;
}
