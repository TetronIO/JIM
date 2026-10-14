// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// A Metaverse Attribute in summary, as the list returns it.
/// </summary>
public class MetaverseAttributeHeader
{
    /// <summary>
    /// The unique identifier of the attribute.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// When the attribute was created (UTC).
    /// </summary>
    public DateTime Created { set; get; }

    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string Name { get; set; } = null!;

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
    /// The Object Types the attribute is bound to, each as a Key (the Object Type's ID) and a Value (its name).
    /// </summary>
    public IEnumerable<KeyValuePair<int, string>>? MetaverseObjectTypes { get; set; }

    /// <summary>
    /// Creates a header from a MetaverseAttribute entity.
    /// </summary>
    public static MetaverseAttributeHeader FromEntity(MetaverseAttribute entity)
    {
        return new MetaverseAttributeHeader
        {
            Id = entity.Id,
            Created = entity.Created,
            Name = entity.Name,
            Type = entity.Type,
            AttributePlurality = entity.AttributePlurality,
            BuiltIn = entity.BuiltIn,
            MetaverseObjectTypes = entity.MetaverseObjectTypes?
                .Select(ot => new KeyValuePair<int, string>(ot.Id, ot.Name))
                .ToList()
        };
    }
}