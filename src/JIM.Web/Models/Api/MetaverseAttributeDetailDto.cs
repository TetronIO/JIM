// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// Detailed API representation of a MetaverseAttribute.
/// </summary>
public class MetaverseAttributeDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime Created { get; set; }
    public AttributeDataType Type { get; set; }
    public AttributePlurality AttributePlurality { get; set; }
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The rendering hint for multi-valued attributes (Table, ChipSet, List). Ignored for single-valued attributes.
    /// </summary>
    public AttributeRenderingHint RenderingHint { get; set; }

    public List<ObjectTypeReferenceDto> ObjectTypes { get; set; } = new();

    /// <summary>
    /// Advisory Standard Mappings documenting how the attribute corresponds to attributes in wire standard
    /// vocabularies (SCIM 2.0, LDAP/AD). Never consulted by the synchronisation engine.
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
    public int Id { get; set; }
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
