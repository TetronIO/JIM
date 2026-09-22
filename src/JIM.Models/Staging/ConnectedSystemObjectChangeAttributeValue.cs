// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Enums;
namespace JIM.Models.Staging;

public class ConnectedSystemObjectChangeAttributeValue
{
    #region accessors
    public Guid Id { get; set; }

    /// <summary>
    /// The parent for this object.
    /// Required for establishing an Entity Framework relationship.
    /// </summary>
    public ConnectedSystemObjectChangeAttribute ConnectedSystemObjectChangeAttribute { get; set; } = null!;

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

    public ConnectedSystemObject? ReferenceValue { get; set; }

    /// <summary>
    /// When true, the <see cref="ReferenceValue"/> points to a stub CSO that has not yet been
    /// exported to the target Connected System (status PendingProvisioning). The UI should render
    /// this differently from a fully resolved reference — e.g. no clickable link, with a
    /// "Pending Export" visual indicator.
    /// </summary>
    public bool IsPendingExportStub { get; set; }

    /// <summary>
    /// The export Synchronisation Rule whose mapping produced this value, copied from
    /// <see cref="JIM.Models.Transactional.PendingExportAttributeValueChange.SyncRuleId"/> by
    /// <see cref="JIM.Application.Utilities.ExportChangeHistoryBuilder"/>. Deliberately a soft pointer: no
    /// foreign key and no index, matching <see cref="JIM.Models.Transactional.PendingExportAttributeValueChange.ResolvedReferenceCsoId"/>'s
    /// rationale, so a deleted rule leaves this dangling rather than requiring every export write path to fix
    /// it up. <see cref="SyncRuleName"/> is the denormalised record that survives the rule's deletion; treat a
    /// non-resolving id as "the rule that produced this value is gone" rather than a data error.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of the contributing Synchronisation Rule's name at export time. Denormalised so it survives
    /// deletion of the rule.
    /// </summary>
    public string? SyncRuleName { get; set; }
    #endregion

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(StringValue))
            return StringValue;

        if (DateTimeValue.HasValue)
            return DateTimeValue.Value.ToString(CultureInfo.InvariantCulture);

        if (IntValue.HasValue)
            return IntValue.Value.ToString();

        if (LongValue.HasValue)
            return LongValue.Value.ToString();

        if (DecimalValue.HasValue)
            return DecimalValue.Value.ToString(CultureInfo.InvariantCulture);

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
    public ConnectedSystemObjectChangeAttributeValue()
    {
        // default constructor still required for EntityFramework
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, string stringValue)
    {
        StringValue = stringValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, int intValue)
    {
        IntValue = intValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, long longValue)
    {
        LongValue = longValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, decimal decimalValue)
    {
        DecimalValue = decimalValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, DateTime dateTimeValue)
    {
        DateTimeValue = dateTimeValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, Guid guidValue)
    {
        GuidValue = guidValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, bool boolValue)
    {
        BoolValue = boolValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, bool isByteValueLength, int byteValueLength)
    {
        // we use isByteValueLength to enable us to have a unique constructor signature for an int arg type
        if (isByteValueLength)
            ByteValueLength = byteValueLength;
        else
            throw new ArgumentException("Expected isByteValueLength == true");

        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    public ConnectedSystemObjectChangeAttributeValue(ConnectedSystemObjectChangeAttribute connectedSystemObjectChangeAttribute, ValueChangeType valueChangeType, ConnectedSystemObject referenceValue)
    {
        ReferenceValue = referenceValue;
        ConnectedSystemObjectChangeAttribute = connectedSystemObjectChangeAttribute;
        ValueChangeType = valueChangeType;
    }

    #endregion
}