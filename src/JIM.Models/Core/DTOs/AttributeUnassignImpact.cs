// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of unassigning (unbinding) a Metaverse Attribute from a single Metaverse Object Type. Stored
/// values held by Metaverse Objects <em>of that type</em> are the only hard block; when none exist the binding is
/// removed. Returned by both the preview and the execute method so all callers get the same block/allow decision.
/// </summary>
public class AttributeUnassignImpact
{
    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the attribute is built-in. Built-in attributes can never be unassigned.
    /// </summary>
    public bool BuiltIn { get; set; }

    public int MetaverseObjectTypeId { get; set; }

    public string MetaverseObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the attribute was actually bound to the Object Type at evaluation time.
    /// </summary>
    public bool WasBound { get; set; }

    /// <summary>
    /// The number of Metaverse Objects of the target type that hold a stored value for the attribute.
    /// </summary>
    public int ObjectsWithValues { get; set; }

    /// <summary>
    /// Set true by the execute method when the binding was actually removed.
    /// </summary>
    public bool Unassigned { get; set; }

    /// <summary>
    /// True when stored values of the target type exist: the unassignment is a hard block.
    /// </summary>
    public bool BlockedByValues => ObjectsWithValues > 0;
}
