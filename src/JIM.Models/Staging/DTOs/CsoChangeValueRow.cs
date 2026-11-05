// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.History;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One row of a Connected System Object change's values, read a window at a time (#2498).
/// </summary>
public class CsoChangeValueRow
{
    /// <summary>The change record the value belongs to.</summary>
    public Guid ChangeId { get; set; }

    /// <summary>The value change itself; for a Set row, the new value's.</summary>
    public Guid ValueChangeId { get; set; }

    public string AttributeName { get; set; } = string.Empty;

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public ChangeValueOperation Operation { get; set; }

    /// <summary>The value the row adds, sets or removes.</summary>
    public CsoValueChangeDto Value { get; set; } = new();

    /// <summary>For a Set row, the value it replaced; otherwise null.</summary>
    public CsoValueChangeDto? PreviousValue { get; set; }

    /// <summary>
    /// What is held of the object <see cref="Value"/> references (#2669): the live object, its deletion record, or
    /// nothing but the id. Null when the value is not a reference.
    /// </summary>
    public ResolvedObjectReference? ValueReference { get; set; }

    /// <summary>As <see cref="ValueReference"/>, for <see cref="PreviousValue"/>.</summary>
    public ResolvedObjectReference? PreviousValueReference { get; set; }
}
