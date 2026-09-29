// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A Metaverse-Derived Attribute Flow (#1750, FR 3) that a configuration change leaves with a missing input: at least
/// one Metaverse attribute it reads, directly or through other derived attributes, would lose its last enabled
/// contributor. The change is allowed; the dependant is surfaced so the administrator knows its Missing Input
/// Behaviour now decides what it writes.
/// </summary>
/// <param name="Flow">The derived flow, as it stands after the change.</param>
/// <param name="MissingInputs">The inputs it reads that would have no contributor able to supply a value, in the order
/// the flow's expression first mentions them.</param>
public sealed record DerivedFlowDependent(DerivedFlow Flow, IReadOnlyList<DerivedFlowMissingInput> MissingInputs);

/// <summary>
/// One input of a <see cref="DerivedFlowDependent"/> that would be left without a value.
/// </summary>
/// <param name="MetaverseAttributeId">The Metaverse attribute read.</param>
/// <param name="MetaverseAttributeName">Its name, for messages.</param>
/// <param name="ThroughDerivedFlows">False when the attribute would have no enabled contributor at all; true when its
/// only remaining enabled contributors are derived flows that are themselves missing an input (the transitive case).</param>
public sealed record DerivedFlowMissingInput(int MetaverseAttributeId, string MetaverseAttributeName, bool ThroughDerivedFlows);
