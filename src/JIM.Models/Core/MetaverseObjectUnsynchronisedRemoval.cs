// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Models.Core;

/// <summary>
/// One attribute value removed from a Metaverse Object by a write outside synchronisation, as recorded in
/// <see cref="MetaverseObjectUnsynchronisedChanges.Removals"/>. It carries exactly the typed value fields export
/// staging matches a removal by (the referenced object, or the value's content), so the next synchronisation can stage
/// the same removal a synchronisation-driven change would have. A binary value is never recorded: export staging has no
/// way to match one by content.
/// </summary>
public sealed record MetaverseObjectUnsynchronisedRemoval
{
    /// <summary>The Metaverse Attribute the value was removed from.</summary>
    public int AttributeId { get; init; }

    public string? StringValue { get; init; }

    public int? IntValue { get; init; }

    public long? LongValue { get; init; }

    public decimal? DecimalValue { get; init; }

    public Guid? GuidValue { get; init; }

    public bool? BoolValue { get; init; }

    public DateTime? DateTimeValue { get; init; }

    /// <summary>The Metaverse Object a reference value pointed at.</summary>
    public Guid? ReferenceValueId { get; init; }

    /// <summary>
    /// Whether the record carries a value to match by. A removal with none (a binary value, an asserted-null marker)
    /// matches nothing in export staging, and one that was staged would match every valueless row of the attribute.
    /// </summary>
    [JsonIgnore]
    public bool HasContent => StringValue != null || IntValue.HasValue || LongValue.HasValue || DecimalValue.HasValue ||
                              GuidValue.HasValue || BoolValue.HasValue || DateTimeValue.HasValue || ReferenceValueId.HasValue;

    /// <summary>
    /// Snapshots a value that is leaving a Metaverse Object.
    /// </summary>
    /// <param name="value">The value being removed.</param>
    /// <param name="attributeId">The Metaverse Attribute it belongs to.</param>
    public static MetaverseObjectUnsynchronisedRemoval From(MetaverseObjectAttributeValue value, int attributeId)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new MetaverseObjectUnsynchronisedRemoval
        {
            AttributeId = attributeId,
            StringValue = value.StringValue,
            IntValue = value.IntValue,
            LongValue = value.LongValue,
            DecimalValue = value.DecimalValue,
            GuidValue = value.GuidValue,
            BoolValue = value.BoolValue,
            DateTimeValue = value.DateTimeValue,
            ReferenceValueId = value.ReferenceValue?.Id ?? value.ReferenceValueId
        };
    }

    /// <summary>
    /// The removal as an unsaved attribute value (no id), which is how export staging's removal matching reads one: by
    /// the referenced object first, then by content.
    /// </summary>
    public MetaverseObjectAttributeValue ToAttributeValue() => new()
    {
        AttributeId = AttributeId,
        StringValue = StringValue,
        IntValue = IntValue,
        LongValue = LongValue,
        DecimalValue = DecimalValue,
        GuidValue = GuidValue,
        BoolValue = BoolValue,
        DateTimeValue = DateTimeValue,
        ReferenceValueId = ReferenceValueId
    };
}
