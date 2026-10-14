// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Metaverse Attribute in full.
/// </summary>
public class MetaverseAttributeDetailDto
{
    /// <summary>
    /// The unique identifier of the attribute.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// When the attribute was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The kind of value the attribute holds.
    /// </summary>
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// Whether the attribute holds one value or many.
    /// </summary>
    public AttributePlurality AttributePlurality { get; set; }

    /// <summary>
    /// Whether JIM provides the attribute. A built-in attribute cannot be changed or deleted.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// How the portal shows a multi-valued attribute's values. Ignored for a single-valued attribute.
    /// </summary>
    public AttributeRenderingHint RenderingHint { get; set; }

    /// <summary>
    /// The Object Types the attribute is bound to.
    /// </summary>
    public List<ObjectTypeReferenceDto> ObjectTypes { get; set; } = new();

    /// <summary>
    /// The attribute's counterparts in other standards, such as SCIM 2.0 and LDAP. They are guidance only;
    /// synchronisation never uses them.
    /// </summary>
    public List<StandardMappingDto> StandardMappings { get; set; } = new();

    /// <summary>
    /// Creates a detailed DTO from a MetaverseAttribute entity.
    /// </summary>
    public static MetaverseAttributeDetailDto FromEntity(MetaverseAttribute entity)
    {
        return new MetaverseAttributeDetailDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Created = entity.Created,
            Type = entity.Type,
            AttributePlurality = entity.AttributePlurality,
            BuiltIn = entity.BuiltIn,
            RenderingHint = entity.RenderingHint,
            ObjectTypes = entity.MetaverseObjectTypes?
                .Select(ot => new ObjectTypeReferenceDto { Id = ot.Id, Name = ot.Name })
                .ToList() ?? new(),
            StandardMappings = entity.StandardMappings
                .OrderBy(m => m.Standard).ThenBy(m => m.CounterpartName, StringComparer.Ordinal)
                .Select(m => new StandardMappingDto { Standard = m.Standard, CounterpartName = m.CounterpartName, Notes = m.Notes })
                .ToList()
        };
    }
}

/// <summary>
/// Lightweight reference to a MetaverseObjectType.
/// </summary>
public class ObjectTypeReferenceDto
{
    /// <summary>
    /// The unique identifier of the Object Type.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The Object Type's name.
    /// </summary>
    public string Name { get; set; } = null!;
}

/// <summary>
/// An advisory Standard Mapping on a Metaverse Attribute: the counterpart attribute name in a wire standard's
/// vocabulary, with optional nuance notes.
/// </summary>
public class StandardMappingDto
{
    public AttributeStandard Standard { get; set; }
    public string CounterpartName { get; set; } = null!;
    public string? Notes { get; set; }
}
