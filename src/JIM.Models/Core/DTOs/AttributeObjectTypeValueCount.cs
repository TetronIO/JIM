// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The number of Metaverse Objects of a specific Metaverse Object Type that hold a stored value for a given Metaverse
/// Attribute. Stored values are the only hard block on destructive attribute operations, so this per-type breakdown
/// lets the UI/API report the block precisely (and link to the affected objects) rather than a single opaque total.
/// </summary>
public class AttributeObjectTypeValueCount
{
    public int MetaverseObjectTypeId { get; set; }

    public string MetaverseObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// The plural name of the Metaverse Object Type, used to build the object-list deep link
    /// (<c>/t/{pluralName}?search=hasAttribute:{attributeName}</c>) that shows the admin exactly which objects hold a
    /// value for the attribute.
    /// </summary>
    public string MetaverseObjectTypePluralName { get; set; } = string.Empty;

    /// <summary>
    /// The number of Metaverse Objects of this type that hold at least one value for the attribute.
    /// </summary>
    public int ObjectCount { get; set; }
}
