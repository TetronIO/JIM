// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The kind of configuration reference that points at a Metaverse Attribute. Used by the destructive-operation
/// preview and cascade to describe, in dependency order, exactly what will be removed when an attribute (or one of
/// its Object Type bindings) is deleted. References never block deletion; they are cascade-removed (see
/// <see cref="AttributeReference"/> and the destructive methods on the Metaverse application server).
/// </summary>
public enum AttributeReferenceKind
{
    /// <summary>
    /// A Metaverse Object Type binding (the many-to-many association that makes the attribute available on a type).
    /// </summary>
    Binding = 0,

    /// <summary>
    /// An import Attribute Flow: a Synchronisation Rule mapping whose target is this Metaverse Attribute.
    /// </summary>
    ImportAttributeFlow = 1,

    /// <summary>
    /// An export Attribute Flow source: a Synchronisation Rule mapping source that reads this Metaverse Attribute.
    /// </summary>
    ExportAttributeFlowSource = 2,

    /// <summary>
    /// A Synchronisation Rule scoping criterion that evaluates this Metaverse Attribute.
    /// </summary>
    ScopingCriterion = 3,

    /// <summary>
    /// An Object Matching Rule whose target is this Metaverse Attribute.
    /// </summary>
    ObjectMatchingRuleTarget = 4,

    /// <summary>
    /// An Object Matching Rule source that reads this Metaverse Attribute.
    /// </summary>
    ObjectMatchingRuleSource = 5
}
