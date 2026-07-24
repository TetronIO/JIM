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
    /// An export Synchronisation Rule mapping removed as a knock-on because removing its attribute-reading source
    /// would leave it with no sources at all. A source-less mapping is invalid configuration and cannot survive.
    /// </summary>
    ExportAttributeFlowMapping = 6,

    /// <summary>
    /// A Predefined Search display column (PredefinedSearchAttribute) that shows this Metaverse Attribute.
    /// </summary>
    PredefinedSearchAttribute = 8,

    /// <summary>
    /// A Predefined Search criterion that filters on this Metaverse Attribute.
    /// </summary>
    PredefinedSearchCriterion = 9,

    /// <summary>
    /// An Example Data template attribute that generates values for this Metaverse Attribute.
    /// </summary>
    ExampleDataTemplateAttribute = 10,

    /// <summary>
    /// An Example Data template attribute dependency that references this Metaverse Attribute.
    /// </summary>
    ExampleDataTemplateAttributeDependency = 11,

    /// <summary>
    /// The Service Settings SSO unique-identifier mapping that points at this Metaverse Attribute. This reference is
    /// cleared (set to null), not deleted, and audited as a Service Setting configuration change.
    /// </summary>
    ServiceSettingsSsoIdentifier = 12
}
