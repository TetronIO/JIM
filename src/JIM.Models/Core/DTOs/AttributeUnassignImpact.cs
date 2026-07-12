// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of unassigning (unbinding) a Metaverse Attribute from a single Metaverse Object Type. Stored
/// values held by Metaverse Objects <em>of that type</em> are the only hard block. When none exist the binding is
/// removed along with the configuration references <em>scoped to that type</em> (references owned by Synchronisation
/// Rules targeting the type, and Predefined Searches / Example Data templates belonging to it); attribute-global
/// references (those owned by rules targeting other types, and the Service Settings SSO mapping) are left untouched
/// because the attribute still exists on other types. Returned by both the preview and the execute method so all
/// callers get the same block/allow decision and the same list to render the type-the-name confirmation dialog.
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
    /// The configuration references scoped to the target Object Type that would be cascade-removed, in dependency
    /// order, when the unassignment proceeds, plus the binding row itself. Empty of cascade items (binding only) when
    /// no Synchronisation Rule targeting the type references the attribute.
    /// </summary>
    public List<AttributeReference> References { get; set; } = [];

    /// <summary>
    /// Set true by the execute method when the binding (and its type-scoped references) were actually removed.
    /// </summary>
    public bool Unassigned { get; set; }

    /// <summary>
    /// True when stored values of the target type exist: the unassignment is a hard block.
    /// </summary>
    public bool BlockedByValues => ObjectsWithValues > 0;

    /// <summary>
    /// True when the unassignment may proceed but would remove references beyond the plain binding, so a type-the-name
    /// confirmation is required.
    /// </summary>
    public bool RequiresConfirmation => WasBound && !BuiltIn && !BlockedByValues && References.Any(r => r.Kind != AttributeReferenceKind.Binding);
}
