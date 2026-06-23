// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Search;

/// <summary>
/// A single search criterion that compares a Metaverse attribute against a value using a specified comparison operator.
/// </summary>
public class PredefinedSearchCriteria
{
    public int Id { get; set; }

    /// <summary>
    /// The comparison operator to apply (e.g. Equals, Contains, StartsWith, GreaterThan).
    /// </summary>
    public SearchComparisonType ComparisonType { get; set; }

    /// <summary>
    /// The Metaverse attribute that this criterion evaluates.
    /// </summary>
    public MetaverseAttribute MetaverseAttribute { get; set; } = null!;

    /// <summary>
    /// The foreign key scalar for <see cref="MetaverseAttribute"/>. Prefer this over the
    /// navigation property for existence checks under AsNoTracking (see src/CLAUDE.md).
    /// </summary>
    public int MetaverseAttributeId { get; set; }

    /// <summary>
    /// The value to compare against, for Text attributes.
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// The value to compare against, for Number attributes.
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// The value to compare against, for LongNumber attributes.
    /// </summary>
    public long? LongValue { get; set; }

    /// <summary>
    /// The value to compare against, for DateTime attributes.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>
    /// The value to compare against, for Boolean attributes.
    /// </summary>
    public bool? BoolValue { get; set; }

    /// <summary>
    /// The value to compare against, for Guid attributes.
    /// </summary>
    public Guid? GuidValue { get; set; }

    /// <summary>
    /// When true (default), value comparisons are case-sensitive.
    /// When false, comparisons ignore case differences.
    /// Only applies to text/string comparisons.
    /// </summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// Gets the data type of the attribute being evaluated.
    /// </summary>
    public AttributeDataType? GetAttributeDataType()
    {
        return MetaverseAttribute?.Type;
    }

    /// <summary>
    /// Gets the name of the attribute being evaluated.
    /// </summary>
    public string? GetAttributeName()
    {
        return MetaverseAttribute?.Name;
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
            AttributeDataType.DateTime => "Date: " + DateTimeValue,
            AttributeDataType.Guid => "Guid: " + GuidValue,
            _ => "Unsupported data type"
        };
    }
}
