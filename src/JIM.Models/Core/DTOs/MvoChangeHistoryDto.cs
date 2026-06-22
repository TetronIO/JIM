// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Flat projection of a Metaverse Object change record for display and API consumption.
/// Includes denormalised initiator, Synchronisation Rule, and Run Profile context so consumers can
/// render a row without further joins, and a list of attribute-level changes.
/// </summary>
public class MvoChangeHistoryDto
{
    public Guid Id { get; set; }

    public ObjectChangeType ChangeType { get; set; }

    public DateTime ChangeTime { get; set; }

    public ActivityInitiatorType InitiatedByType { get; set; }

    public Guid? InitiatedById { get; set; }

    public string? InitiatedByName { get; set; }

    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

    public int? SyncRuleId { get; set; }

    public string? SyncRuleName { get; set; }

    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The CSO id, if this change was raised by a sync Run Profile execution item.
    /// </summary>
    public Guid? CsoId { get; set; }

    /// <summary>
    /// The CSO external id snapshot (preserved even if the CSO is later deleted).
    /// </summary>
    public string? CsoExternalId { get; set; }

    /// <summary>
    /// The Connected System id behind the activity, if the change was sync-initiated.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System name (taken from <c>Activity.TargetContext</c>).
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The Run Profile name (taken from <c>Activity.TargetName</c>).
    /// </summary>
    public string? RunProfileName { get; set; }

    /// <summary>
    /// The Connected System run type for sync-initiated changes; used to render the
    /// change mechanism (Import / SynchronisationRule / Export) when the initiator
    /// type is the default <c>NotSet</c> sync value.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; }

    public List<MvoAttributeChangeDto> AttributeChanges { get; set; } = new();
}

/// <summary>
/// Per-attribute change within an <see cref="MvoChangeHistoryDto"/>.
/// </summary>
public class MvoAttributeChangeDto
{
    public string AttributeName { get; set; } = string.Empty;

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public List<MvoValueChangeDto> ValueChanges { get; set; } = new();
}

/// <summary>
/// Per-value change within an <see cref="MvoAttributeChangeDto"/>. Holds the typed
/// scalar value or, for reference attributes, a flattened reference target.
/// </summary>
public class MvoValueChangeDto
{
    public ValueChangeType ValueChangeType { get; set; }

    public string? StringValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public int? IntValue { get; set; }

    public int? ByteValueLength { get; set; }

    public Guid? GuidValue { get; set; }

    public bool? BoolValue { get; set; }

    public MvoChangeReferenceDto? ReferenceValue { get; set; }

    /// <summary>
    /// Returns the human-readable representation of the value, mirroring the
    /// behaviour of <c>MetaverseObjectChangeAttributeValue.ToString()</c> so
    /// the UI does not need access to the original entity.
    /// </summary>
    public string ToDisplayString()
    {
        if (!string.IsNullOrEmpty(StringValue))
            return StringValue;

        if (DateTimeValue.HasValue)
            return DateTimeValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (IntValue.HasValue)
            return IntValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (ByteValueLength.HasValue)
            return ByteValueLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (GuidValue.HasValue)
            return GuidValue.Value.ToString();

        if (BoolValue.HasValue)
            return BoolValue.Value.ToString();

        if (ReferenceValue != null)
            return ReferenceValue.Id.ToString();

        return string.Empty;
    }
}

/// <summary>
/// Flattened reference value for a value change. Avoids materialising the full
/// referenced <c>MetaverseObject</c> entity by carrying the display name (sourced
/// from <c>MetaverseObject.CachedDisplayName</c>) and type names directly.
/// </summary>
public class MvoChangeReferenceDto
{
    public Guid Id { get; set; }

    public string? DisplayName { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public string TypePluralName { get; set; } = string.Empty;
}
