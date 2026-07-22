// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of deleting a Metaverse Attribute: whether stored values block it (the only hard block), the
/// per-Object-Type breakdown of those values, and the configuration references that would be cascade-removed when it
/// proceeds. Returned by both the preview (read-only) and the execute method, so REST and PowerShell callers get the
/// same block/allow decision and the same list to render the type-the-name confirmation dialog.
/// </summary>
public class AttributeDeletionImpact
{
    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the attribute is built-in. Built-in attributes can never be deleted.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The total number of Metaverse Objects (across all types) that hold a stored value for the attribute.
    /// </summary>
    public int TotalObjectsWithValues { get; set; }

    /// <summary>
    /// The per-Object-Type breakdown of Metaverse Objects holding a stored value for the attribute.
    /// </summary>
    public List<AttributeObjectTypeValueCount> ObjectTypeValueCounts { get; set; } = [];

    /// <summary>
    /// The configuration references (bindings, Attribute Flows, scoping criteria, Object Matching Rules) that would
    /// be cascade-removed, in dependency order, when the deletion proceeds. Empty when the attribute is unreferenced.
    /// </summary>
    public List<AttributeReference> References { get; set; } = [];

    /// <summary>
    /// Set true by the execute method when the deletion actually happened. False on a preview, on a built-in, or when
    /// the deletion was refused because stored values exist.
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// True when stored values exist: the deletion is a hard block and must not proceed until the values are cleared.
    /// </summary>
    public bool BlockedByValues => TotalObjectsWithValues > 0;

    /// <summary>
    /// True when the deletion may proceed but would remove references, so a type-the-name confirmation is required.
    /// </summary>
    public bool RequiresConfirmation => !BuiltIn && !BlockedByValues && References.Count > 0;
}
