// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Logic;
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

    public long? LongValue { get; set; }

    public decimal? DecimalValue { get; set; }

    /// <summary>
    /// It would be inefficient, and not especially helpful to track the actual byte value changes, so just track the value lengths instead to show the change.
    /// </summary>
    public int? ByteValueLength { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    public MetaverseObject? ReferenceValue { get; set; }

    /// <summary>
    /// Foreign key for <see cref="ReferenceValue"/>. Exposed as a scalar so reference change
    /// records can be written when only the target MVO id is known (navigation not loaded),
    /// and so the navigation can be materialised later via <c>.Include(x =&gt; x.ReferenceValue)</c>.
    /// </summary>
    public Guid? ReferenceValueId { get; set; }

    /// <summary>
    /// The Synchronisation Rule whose mapping contributed this value at the time of the change.
    /// Together with the parent attribute this identifies the winning mapping. Null when the value was not
    /// contributed by a Synchronisation Rule, or when the contributing rule has since been deleted (the FK is
    /// set null on rule deletion; <see cref="ContributedBySyncRuleName"/> is retained as the denormalised record).
    /// Copied from <see cref="MetaverseObjectAttributeValue.ContributedBySyncRuleId"/> by
    /// <see cref="MetaverseObjectChange.AddAttributeValueChange"/> so change history is self-describing even
    /// after the live attribute value's provenance has moved on.
    /// </summary>
    public SyncRule? ContributedBySyncRule { get; set; }
    public int? ContributedBySyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing Synchronisation Rule's name at the time of the change. Denormalised so it
    /// survives deletion of the rule, matching <see cref="MetaverseObjectChange.SyncRuleName"/>'s pattern.
    /// </summary>
    public string? ContributedBySyncRuleName { get; set; }
    #endregion

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(StringValue))
            return StringValue;

        if (DateTimeValue.HasValue)
            return DateTimeValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (IntValue.HasValue)
            return IntValue.Value.ToString();

        if (LongValue.HasValue)
            return LongValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (DecimalValue.HasValue)
            return DecimalValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (ByteValueLength.HasValue)
            return ByteValueLength.Value.ToString();

        if (GuidValue.HasValue)
            return GuidValue.Value.ToString();

        if (BoolValue.HasValue)
            return BoolValue.Value.ToString();

        if (ReferenceValue != null)
            return ReferenceValue.Id.ToString();

        return string.Empty;
    }

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

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, long longValue)
    {
        LongValue = longValue;
        MetaverseObjectChangeAttribute = metaverseObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public MetaverseObjectChangeAttributeValue(MetaverseObjectChangeAttribute metaverseObjectChangeAttribute, ValueChangeType valueChangeType, decimal decimalValue)
    {
        DecimalValue = decimalValue;
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