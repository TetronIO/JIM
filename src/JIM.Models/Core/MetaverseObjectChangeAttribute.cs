// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

public class MetaverseObjectChangeAttribute
{
    public Guid Id { get; set; }

    /// <summary>
    /// The parent for this Metaverse Object change item.
    /// </summary>
    public MetaverseObjectChange MetaverseObjectChange { get; set; } = null!;

    /// <summary>
    /// The metaverse attribute definition. Nullable because the attribute may be deleted after
    /// the change was recorded. When null, use <see cref="AttributeName"/> and <see cref="AttributeType"/>.
    /// </summary>
    public MetaverseAttribute? Attribute { get; set; }

    /// <summary>
    /// Snapshot of the attribute name at the time of the change.
    /// Preserved even if the attribute definition is later deleted.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// Snapshot of the attribute data type at the time of the change.
    /// Preserved even if the attribute definition is later deleted.
    /// </summary>
    public AttributeDataType AttributeType { get; set; }

    /// <summary>
    /// A list of what values were added to or removed from this attribute.
    /// </summary>
    public List<MetaverseObjectChangeAttributeValue> ValueChanges { get; set; } = new();
}