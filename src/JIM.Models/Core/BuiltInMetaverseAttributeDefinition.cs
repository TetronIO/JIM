// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Declares one built-in Metaverse Attribute within <see cref="BuiltInMetaverseSchema"/>: its
/// shape (data type, plurality, rendering hint), which built-in Metaverse Object Types it binds
/// to, and its advisory Standard Mappings. The declarative source the built-in schema
/// synchronisation pass converges the database towards on every startup.
/// </summary>
public class BuiltInMetaverseAttributeDefinition
{
    public BuiltInMetaverseAttributeDefinition(
        string name,
        AttributeDataType type,
        AttributePlurality plurality,
        IReadOnlyList<string> objectTypeNames,
        AttributeRenderingHint renderingHint = AttributeRenderingHint.Default,
        IReadOnlyList<StandardMappingDefinition>? standardMappings = null)
    {
        Name = name;
        Type = type;
        Plurality = plurality;
        ObjectTypeNames = objectTypeNames;
        RenderingHint = renderingHint;
        StandardMappings = standardMappings ?? Array.Empty<StandardMappingDefinition>();
    }

    public string Name { get; }

    public AttributeDataType Type { get; }

    public AttributePlurality Plurality { get; }

    /// <summary>
    /// The built-in Metaverse Object Types (by name) this attribute is bound to,
    /// i.e. values from <see cref="Constants.BuiltInObjectTypes"/>.
    /// </summary>
    public IReadOnlyList<string> ObjectTypeNames { get; }

    public AttributeRenderingHint RenderingHint { get; }

    /// <summary>
    /// Advisory Standard Mappings for this attribute. May be empty where no clean counterpart
    /// exists in any wire standard's vocabulary.
    /// </summary>
    public IReadOnlyList<StandardMappingDefinition> StandardMappings { get; }
}