// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// One source feeding a data flow: an attribute on the originating side, or an expression that computes the value
/// (#1199). A mapping's target is always a single attribute, but its source side may be one attribute, several
/// chained sources, or an expression, so the source side of a <see cref="DataFlowHeader"/> is a list.
/// </summary>
public class DataFlowSource
{
    /// <summary>
    /// The order the sources are evaluated in, lowest first, mirroring <see cref="SyncRuleMappingSource.Order"/>.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Populated on an Export flow, where the value comes from a Metaverse Attribute.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// Populated on an Export flow, where the value comes from a Metaverse Attribute.
    /// </summary>
    public string? MetaverseAttributeName { get; set; }

    /// <summary>
    /// Populated on an Import flow, where the value comes from a Connected System attribute.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// Populated on an Import flow, where the value comes from a Connected System attribute.
    /// </summary>
    public string? ConnectedSystemAttributeName { get; set; }

    /// <summary>
    /// Populated when the value is computed rather than taken from an attribute. An expression may reference
    /// attributes that no other field here records, so a flow whose source is an expression cannot be matched by
    /// an attribute filter; that limitation is stated on the Data Flow page rather than guessed around.
    /// </summary>
    public string? Expression { get; set; }

    /// <summary>
    /// Whether this source computes its value from an expression rather than reading a single attribute.
    /// </summary>
    public bool IsExpression => !string.IsNullOrWhiteSpace(Expression);

    /// <summary>
    /// The source rendered for display: the attribute's name, or "Expression" where the value is computed.
    /// </summary>
    public string DisplayName =>
        MetaverseAttributeName ?? ConnectedSystemAttributeName ?? (IsExpression ? "Expression" : string.Empty);
}
