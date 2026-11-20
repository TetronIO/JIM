// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Activities;

/// <summary>
/// One attribute of an <see cref="ImportedObjectSnapshot"/>: its name and type as the Connector returned them, and the
/// values kept of it.
/// </summary>
public class ImportedObjectSnapshotAttribute
{
    /// <summary>The attribute's name, in the case the Connector returned it.</summary>
    public string Name { get; set; } = null!;

    /// <summary>The attribute's type, as the Connector returned it.</summary>
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// The values kept, in the order the Connector returned them, each as text. A value longer than
    /// <see cref="ImportedObjectSnapshot.MaxValueLength"/> is cut short and ends in "...". Binary values are described
    /// (<c>[binary, 2,048 bytes]</c>) rather than kept. Empty when <see cref="Withheld"/> is set.
    /// </summary>
    public List<string> Values { get; set; } = [];

    /// <summary>
    /// How many of the attribute's values were not kept, because of the per-attribute or the per-snapshot limit; null
    /// when every value was kept.
    /// </summary>
    public int? OmittedValueCount { get; set; }

    /// <summary>
    /// True when the attribute holds, or may hold, credential material, so its values were never recorded; null otherwise.
    /// </summary>
    public bool? Withheld { get; set; }

    /// <summary>
    /// True when the item's error is about this attribute (a repeated attribute, one the schema does not hold, or the
    /// External ID), so the portal highlights it and the snapshot keeps it longest when cut to size; null otherwise.
    /// </summary>
    public bool? NamedInError { get; set; }
}
