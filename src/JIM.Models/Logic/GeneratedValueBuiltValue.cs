// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// One value built from a generated value (Generated Value Forms, #2023): a Metaverse attribute a Derived Attribute Flow
/// builds from it (an Email from Account Name, a User Principal Name from that Email), or an export expression reading
/// it. Worked out from the Synchronisation Rules alone; nothing about it is persisted.
/// </summary>
public sealed class GeneratedValueBuiltValue
{
    /// <summary>
    /// The mapping that builds the value: an import mapping whose expression reads the Metaverse (a Derived Attribute
    /// Flow), or an export expression mapping. Its <see cref="SyncRuleMapping.KeepUnique"/> is the value's switch.
    /// </summary>
    public required SyncRuleMapping Mapping { get; init; }

    /// <summary>
    /// The Synchronisation Rule holding <see cref="Mapping"/>.
    /// </summary>
    public required SyncRule SyncRule { get; init; }

    /// <summary>
    /// The generated mapping the value is described relative to: the one it is built from, or, for a value built from
    /// several, the one asked about (or the first of them).
    /// </summary>
    public required SyncRuleMapping GeneratedMapping { get; init; }

    /// <summary>
    /// The names of every generated Metaverse attribute the value is built from, <see cref="GeneratedMapping"/>'s first:
    /// one name, or several for a value <see cref="GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues"/>.
    /// </summary>
    public IReadOnlyList<string> BuiltFromAttributeNames { get; init; } = [];

    /// <summary>
    /// How the value relates to the generated value, which decides whether it is checked before the generated value is
    /// issued and whether it is corrected with it.
    /// </summary>
    public required GeneratedValueBuiltValueKind Kind { get; init; }

    /// <summary>
    /// The Metaverse attribute the value is held in, for a value a Derived Attribute Flow builds; null for an export
    /// expression, whose value is only ever written to <see cref="ExportedTo"/>.
    /// </summary>
    public int? MetaverseAttributeId { get; init; }

    /// <summary>
    /// The name of <see cref="MetaverseAttributeId"/>; null for an export expression.
    /// </summary>
    public string? MetaverseAttributeName { get; init; }

    /// <summary>
    /// The Metaverse attributes the value is built through, nearest the generated value first: empty when it reads the
    /// generated value itself (an Email), ["Email"] for a User Principal Name built from that Email.
    /// </summary>
    public IReadOnlyList<string> Via { get; init; } = [];

    /// <summary>
    /// Every (Connected System, attribute) the value is written to unchanged: the enabled, single-source, direct export
    /// flows of <see cref="MetaverseAttributeId"/>, or an export expression's own target.
    /// </summary>
    public IReadOnlyList<(int ConnectedSystemId, int AttributeId)> ExportedTo { get; init; } = [];

    /// <summary>
    /// The value's "Keep unique with ..." switch (<see cref="SyncRuleMapping.KeepUnique"/>).
    /// </summary>
    public bool KeepUnique => Mapping.KeepUnique;
}
