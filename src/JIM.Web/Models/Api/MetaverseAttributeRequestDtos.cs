// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new custom Metaverse Attribute.
/// </summary>
public class CreateMetaverseAttributeRequest
{
    /// <summary>
    /// The name for the Metaverse Attribute. Must be unique, compared case-insensitively (so <c>CostCentre</c> and
    /// <c>costCentre</c> cannot coexist). Stored and returned exactly as supplied.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The data type of the attribute.
    /// </summary>
    [Required]
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// Whether the attribute is single-valued or multi-valued.
    /// </summary>
    public AttributePlurality AttributePlurality { get; set; } = AttributePlurality.SingleValued;

    /// <summary>
    /// Optional list of Metaverse Object Type IDs to bind this attribute to on creation. Empty or omitted creates it
    /// unbound.
    /// </summary>
    public List<int>? ObjectTypeIds { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request DTO for updating a custom Metaverse Attribute's name, rendering configuration and Standard Mappings.
/// Type and plurality are changed via the dedicated schema endpoint (they are gated by stored values); Object Type
/// bindings are changed via the bind / unassign endpoints. At least one of <see cref="Name"/>,
/// <see cref="RenderingHint"/> or <see cref="StandardMappings"/> must be supplied.
/// </summary>
public class UpdateMetaverseAttributeRequest
{
    /// <summary>
    /// The new name for the attribute. Omitted (or null) leaves the name unchanged. Subject to the same
    /// case-insensitive uniqueness check as creation.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The updated rendering hint for multi-valued attributes. Omitted (or null) leaves it unchanged.
    /// </summary>
    public AttributeRenderingHint? RenderingHint { get; set; }

    /// <summary>
    /// The attribute's full set of Standard Mappings. Omitted (or null) leaves them unchanged; a supplied list
    /// replaces them entirely, so an empty list clears them. Each mapping requires a standard and a counterpart
    /// attribute name, unique in combination.
    /// </summary>
    public List<StandardMappingDto>? StandardMappings { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request DTO for changing a custom Metaverse Attribute's data type and/or plurality. The change is refused while any
/// Metaverse Object holds a stored value for the attribute.
/// </summary>
public class ChangeMetaverseAttributeSchemaRequest
{
    /// <summary>
    /// The new data type.
    /// </summary>
    [Required]
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// The new plurality.
    /// </summary>
    [Required]
    public AttributePlurality AttributePlurality { get; set; }

    /// <summary>
    /// Optional reason for the change, recorded on the audit Activity and configuration change history.
    /// </summary>
    public string? ChangeReason { get; set; }
}
