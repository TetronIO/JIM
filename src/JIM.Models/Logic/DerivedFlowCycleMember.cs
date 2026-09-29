// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// One step of a Metaverse-Derived Attribute Flow dependency cycle (#1750): the derived mapping that writes
/// <see cref="MetaverseAttributeName"/> reads <see cref="ReadsMetaverseAttributeName"/>, which is the attribute the
/// next member writes.
/// </summary>
/// <param name="Mapping">The derived import mapping on the cycle.</param>
/// <param name="SyncRule">The Synchronisation Rule hosting it.</param>
/// <param name="MetaverseAttributeId">The attribute the mapping writes.</param>
/// <param name="MetaverseAttributeName">The written attribute's name.</param>
/// <param name="ReadsMetaverseAttributeId">The attribute on the cycle the mapping reads.</param>
/// <param name="ReadsMetaverseAttributeName">The read attribute's name.</param>
public sealed record DerivedFlowCycleMember(
    SyncRuleMapping Mapping,
    SyncRule SyncRule,
    int MetaverseAttributeId,
    string MetaverseAttributeName,
    int ReadsMetaverseAttributeId,
    string ReadsMetaverseAttributeName);
