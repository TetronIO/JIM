using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new Metaverse Attribute.
/// </summary>
public class CreateMetaverseAttributeRequest
{
    /// <summary>
    /// The name for the Metaverse Attribute.
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
    /// Optional list of object type IDs to associate this attribute with.
    /// </summary>
    public List<int>? ObjectTypeIds { get; set; }
}

/// <summary>
/// Request DTO for updating an existing Metaverse Attribute.
/// </summary>
public class UpdateMetaverseAttributeRequest
{
    /// <summary>
    /// The updated name for the Metaverse Attribute.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The updated data type of the attribute.
    /// </summary>
    public AttributeDataType? Type { get; set; }

    /// <summary>
    /// Whether the attribute is single-valued or multi-valued.
    /// </summary>
    public AttributePlurality? AttributePlurality { get; set; }

    /// <summary>
    /// Optional list of object type IDs to associate this attribute with.
    /// This will replace the existing associations.
    /// </summary>
    public List<int>? ObjectTypeIds { get; set; }
}
