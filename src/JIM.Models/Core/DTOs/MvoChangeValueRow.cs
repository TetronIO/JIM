// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.History;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One row of a Metaverse Object change's values, read a window at a time (#2498).
/// </summary>
public class MvoChangeValueRow
{
    /// <summary>The change record the value belongs to.</summary>
    public Guid ChangeId { get; set; }

    /// <summary>The Metaverse Object the change was made to; null once that object has been deleted.</summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>The value change itself; for a Set row, the new value's.</summary>
    public Guid ValueChangeId { get; set; }

    public string AttributeName { get; set; } = string.Empty;

    /// <summary>The Metaverse Attribute's id; null when the attribute definition has since been deleted.</summary>
    public int? AttributeId { get; set; }

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public ChangeValueOperation Operation { get; set; }

    /// <summary>The value the row adds, sets or removes.</summary>
    public MvoValueChangeDto Value { get; set; } = new();

    /// <summary>For a Set row, the value it replaced; otherwise null.</summary>
    public MvoValueChangeDto? PreviousValue { get; set; }

    /// <summary>
    /// What is held of the object <see cref="Value"/> references (#2669): the live object, its deletion record, or
    /// nothing but the id. Null when the value is not a reference.
    /// </summary>
    public ResolvedObjectReference? ValueReference { get; set; }

    /// <summary>As <see cref="ValueReference"/>, for <see cref="PreviousValue"/>.</summary>
    public ResolvedObjectReference? PreviousValueReference { get; set; }
}
