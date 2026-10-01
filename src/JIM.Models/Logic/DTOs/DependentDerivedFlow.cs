// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// A Metaverse-Derived Attribute Flow (#1750, FR 3) that a saved change has left with a missing input, flattened for
/// the surfaces that report it: the mapping deletion, mapping update, Synchronisation Rule save and deletion
/// responses, and the schema refresh dependents report. The change is allowed; this tells the administrator that the
/// flow's Missing Input Behaviour now decides what it contributes.
/// </summary>
public class DependentDerivedFlow
{
    /// <summary>
    /// The derived flow's Attribute Flow mapping id.
    /// </summary>
    public int MappingId { get; set; }

    /// <summary>
    /// The Metaverse attribute the derived flow writes.
    /// </summary>
    public string TargetMetaverseAttributeName { get; set; } = null!;

    /// <summary>
    /// The Synchronisation Rule hosting the derived flow.
    /// </summary>
    public int SyncRuleId { get; set; }

    /// <summary>
    /// The hosting Synchronisation Rule's name.
    /// </summary>
    public string SyncRuleName { get; set; } = null!;

    /// <summary>
    /// The Connected System the hosting Synchronisation Rule belongs to; its synchronisation evaluates the flow.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The hosting Connected System's name.
    /// </summary>
    public string ConnectedSystemName { get; set; } = null!;

    /// <summary>
    /// The inputs that lost their last enabled contributor, nearest first.
    /// </summary>
    public List<DependentDerivedFlowInput> MissingInputs { get; set; } = new();
}
