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
    /// The connected system attribute definition. Nullable because the attribute may be deleted after
    /// the change was recorded. When null, use <see cref="AttributeName"/> and <see cref="AttributeType"/>.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? Attribute { get; set; }

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