// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Core;

public class MetaverseObjectChangeAttribute
{
    public Guid Id { get; set; }

    /// <summary>
    /// The parent for this Metaverse Object change item.
    /// </summary>
    public MetaverseObjectChange MetaverseObjectChange { get; set; } = null!;

    /// <summary>
    /// The id of the Metaverse Attribute this change is to. Written once and never rewritten (#2931): once the
    /// attribute is deleted it names an attribute that no longer exists, so readers decide whether the attribute is live
    /// by looking it up, and otherwise show <see cref="AttributeName"/> and <see cref="AttributeType"/>. Null only on
    /// rows whose id an earlier release cleared when the attribute was deleted.
    /// </summary>
    public int? AttributeId { get; set; }

    /// <summary>
    /// The Metaverse Attribute definition, as a working reference for the code that builds the change. Not a
    /// relationship: whoever writes the row writes <see cref="AttributeId"/>, and a row read back carries only the id.
    /// </summary>
    /// <remarks>
    /// History names its attribute by plain id with no foreign key (#2931), so deleting the attribute never rewrites the
    /// row. A mapped navigation would make EF Core walk the attribute's graph on <c>DbSet.Add</c> and keep a
    /// relationship the database no longer has.
    /// </remarks>
    [NotMapped]
    public MetaverseAttribute? Attribute { get; set; }

    /// <summary>
    /// The attribute id a writer writes for this row: the one set, else the working reference's. Null when neither names
    /// a saved attribute.
    /// </summary>
    public int? AttributeIdForWrite() =>
        AttributeId is > 0 ? AttributeId : Attribute is { Id: > 0 } attribute ? attribute.Id : null;

    /// <summary>
    /// <see cref="AttributeIdForWrite"/> for a raw writer, which refuses a row that names no saved attribute: with no
    /// foreign key, nothing in the database would refuse it, and it would be written as a row no reader could place.
    /// </summary>
    public int RequiredAttributeIdForWrite() =>
        AttributeIdForWrite() ?? throw new InvalidOperationException(
            $"The change record attribute row for '{AttributeName}' names no saved attribute. Save the attribute before " +
            "writing history that names it.");

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