using JIM.Models.Enums;
namespace JIM.Models.Core;

public class MetaverseObjectChangeAttributeValue
{
    #region accessors
    public Guid Id { get; set; }

    /// <summary>
    /// The parent for this object.
    /// Required for establishing an Entity Framework relationship.
    /// </summary>
    public MetaverseObjectChangeAttribute MetaverseObjectChangeAttribute { get; set; } = null!;

    /// <summary>
    /// Was the value being added, or removed?
    /// </summary>
    public ValueChangeType ValueChangeType { get; set; } = ValueChangeType.NotSet;

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    /// <summary>
    /// It would be inefficient, and not especially helpful to track the actual byte value changes, so just track the value lengths instead to show the change.
    /// </summary>
    public int? ByteValueLength { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    public MetaverseObject? ReferenceValue { get; set; }
    #endregion

    #region constructors
    public MetaverseObjectChangeAttributeValue()
    {
        // default constructor still required for EntityFramework
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, string stringValue)
    {
        StringValue = stringValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, int intValue)
    {
        IntValue = intValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, DateTime dateTimeValue)
    {
        DateTimeValue = dateTimeValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, Guid guidValue)
    {
        GuidValue = guidValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, bool boolValue)
    {
        BoolValue = boolValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, bool isByteValueLength, int byteValueLength)
    {
        // we use isByteValueLength to enable us to have a unique constructor signature for an int arg type
        if (isByteValueLength)
            ByteValueLength = byteValueLength;
        else
            throw new ArgumentException("Expected isByteValueLength == true");

        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, MetaverseObject referenceValue)
    {
        ReferenceValue = referenceValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }
    #endregion
}