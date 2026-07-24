// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Declares one advisory Standard Mapping for a built-in Metaverse Attribute within
/// <see cref="BuiltInMetaverseSchema"/>: the counterpart attribute name in a wire standard's
/// vocabulary, plus optional nuance. Seeded as <see cref="MetaverseAttributeStandardMapping"/>
/// rows; never consulted by the synchronisation engine.
/// </summary>
public class StandardMappingDefinition
{
    public StandardMappingDefinition(AttributeStandard standard, string counterpartName, string? notes = null)
    {
        Standard = standard;
        CounterpartName = counterpartName;
        Notes = notes;
    }

    public AttributeStandard Standard { get; }

    public string CounterpartName { get; }

    public string? Notes { get; }
}