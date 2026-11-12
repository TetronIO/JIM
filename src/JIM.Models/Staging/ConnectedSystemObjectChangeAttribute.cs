// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Core;

namespace JIM.Models.Staging;

public class ConnectedSystemObjectChangeAttribute
{
    public Guid Id { get; set; }

    /// <summary>
    /// The parent for this object.
    /// Required for establishing an Entity Framework relationship.
    /// </summary>
    public ConnectedSystemObjectChange ConnectedSystemChange { get; set; } = null!;

    /// <summary>
    /// The id of the Connected System attribute this change is to. Written once and never rewritten (#2931): once the
    /// attribute is deleted it names an attribute that no longer exists, so readers decide whether the attribute is live
    /// by looking it up, and otherwise show <see cref="AttributeName"/> and <see cref="AttributeType"/>. Null only on
    /// rows whose id an earlier release cleared when the attribute was deleted.
    /// </summary>
    public int? AttributeId { get; set; }

    /// <summary>
    /// The Connected System attribute definition, as a working reference for the code that builds the change. Not a
    /// relationship: whoever writes the row writes <see cref="AttributeId"/>, and a row read back carries only the id.
    /// </summary>
    /// <remarks>
    /// History names its attribute by plain id with no foreign key (#2931), so deleting the attribute never rewrites the
    /// row. A mapped navigation would make EF Core walk the attribute's graph on <c>DbSet.Add</c> and keep a
    /// relationship the database no longer has.
    /// </remarks>
    [NotMapped]
    public ConnectedSystemObjectTypeAttribute? Attribute { get; set; }

    /// <summary>
    /// The attribute id a writer writes for this row: the one set, else the working reference's. Null when neither names
    /// a saved attribute.
    /// </summary>
    public int? AttributeIdForWrite() =>
        AttributeId is > 0 ? AttributeId : Attribute is { Id: > 0 } attribute ? attribute.Id : null;

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
    public List<ConnectedSystemObjectChangeAttributeValue> ValueChanges { get; } = new();

    public override string ToString()
    {
        return AttributeName;
    }
}