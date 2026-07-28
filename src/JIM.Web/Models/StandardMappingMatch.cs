// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A Metaverse Attribute whose counterpart name matches the name of the Connected System attribute being
/// mapped in the Attribute Flow editor. A match is a suggestion, never a decision.
/// </summary>
/// <param name="MetaverseAttributeId">The Metaverse Attribute the standard says the name corresponds to.</param>
/// <param name="StandardLabel">The standard whose vocabulary produced the match.</param>
/// <param name="CounterpartName">The counterpart name that matched.</param>
/// <param name="Notes">Optional nuance about the correspondence, worth surfacing where the match is stated.</param>
public sealed record StandardMappingMatch(int MetaverseAttributeId, string StandardLabel, string CounterpartName, string? Notes);
