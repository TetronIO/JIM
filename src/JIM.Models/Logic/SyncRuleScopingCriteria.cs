// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Staging;
namespace JIM.Models.Logic;

public class SyncRuleScopingCriteria
{
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse Attribute to evaluate for outbound (export) Synchronisation Rules.
    /// One of MetaverseAttribute or ConnectedSystemAttribute must be set.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; set; }
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// The Connected System Object Type Attribute to evaluate for inbound (import) Synchronisation Rules.
    /// One of MetaverseAttribute or ConnectedSystemAttribute must be set.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }
    public int? ConnectedSystemAttributeId { get; set; }

    public SearchComparisonType ComparisonType { get; set; } = SearchComparisonType.NotSet;

    public string? StringValue { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public DateTime? DateTimeValue { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// Only applies to text/string comparisons.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// For DateTime attributes, whether this criterion compares against a fixed (Absolute) date held in
    /// <see cref="DateTimeValue"/>, or a date resolved Relative to "now" at each evaluation. Defaults to Absolute.
    /// </summary>
    public DateCriteriaValueMode ValueMode { get; set; } = DateCriteriaValueMode.Absolute;

    /// <summary>
    /// The number of units to offset from "now" when <see cref="ValueMode"/> is Relative. Zero or positive.
    /// </summary>
    public int? RelativeCount { get; set; }

    /// <summary>
    /// The unit of the relative offset (Hours, Days, Weeks, Months, Years) when <see cref="ValueMode"/> is Relative.
    /// </summary>
    public RelativeDateUnit? RelativeUnit { get; set; }

    /// <summary>
    /// The direction of the relative offset (Ago or FromNow) when <see cref="ValueMode"/> is Relative.
    /// </summary>
    public RelativeDateDirection? RelativeDirection { get; set; }

    /// <summary>
    /// Gets the data type of the attribute being evaluated (from either MV or CS attribute).
    /// </summary>
    public AttributeDataType? GetAttributeDataType()
    {
        if (MetaverseAttribute != null)
            return MetaverseAttribute.Type;
        if (ConnectedSystemAttribute != null)
            return ConnectedSystemAttribute.Type;
        return null;
    }

    /// <summary>
    /// Gets the name of the attribute being evaluated (from either MV or CS attribute).
    /// </summary>
    public string? GetAttributeName()
    {
        if (MetaverseAttribute != null)
            return MetaverseAttribute.Name;
        if (ConnectedSystemAttribute != null)
            return ConnectedSystemAttribute.Name;
        return null;
    }

    public override string ToString()
    {
        var attributeType = GetAttributeDataType();
        if (attributeType == null)
            return "No attribute set";

        return attributeType switch
        {
            AttributeDataType.Text => "Text: " + StringValue,
            AttributeDataType.Number => "Number: " + IntValue,
            AttributeDataType.LongNumber => "LongNumber: " + LongValue,
            AttributeDataType.Boolean => "Boolean: " + (BoolValue is null ? "Null" : BoolValue.Value.ToString()),
            AttributeDataType.DateTime => DescribeDateValue(),
            AttributeDataType.Guid => "Guid: " + GuidValue,
            _ => "Unsupported data type"
        };
    }

    /// <summary>
    /// Renders the DateTime value for display: a relative phrase ("30 days ago") when Relative, otherwise the absolute date.
    /// </summary>
    private string DescribeDateValue()
    {
        if (ValueMode == DateCriteriaValueMode.Relative && RelativeCount.HasValue && RelativeUnit.HasValue && RelativeDirection.HasValue)
            return RelativeDateResolver.Describe(RelativeCount.Value, RelativeUnit.Value, RelativeDirection.Value);

        return "Date: " + DateTimeValue;
    }
}