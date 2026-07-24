// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// UI presentation layer for comparison operators in the criteria editors (Predefined Search and Synchronisation
/// Rule scoping). The set and order of valid operators per attribute type comes from the shared, layer-agnostic
/// <see cref="SearchComparisonOperators"/> rule; this type only adds the friendly, type-specific labels
/// (for example DateTime renders "before" / "on or before" rather than "less than"). Keeping the labels here
/// means both editors stay consistent without duplicating either the validity rule or the wording.
/// </summary>
public static class CriterionOperatorOptions
{
    /// <summary>
    /// An operator paired with its friendly, type-appropriate label, for binding to a select control.
    /// </summary>
    public sealed record OperatorOption(SearchComparisonType Operator, string Label);

    /// <summary>
    /// Returns the operators valid for the attribute type (per <see cref="SearchComparisonOperators"/>),
    /// in display order, each with its friendly label.
    /// </summary>
    public static IReadOnlyList<OperatorOption> ForType(AttributeDataType type) =>
        SearchComparisonOperators.ValidOperatorsFor(type)
            .Select(op => new OperatorOption(op, LabelFor(op, type)))
            .ToList();

    /// <summary>
    /// The friendly label for an operator in the context of an attribute type. DateTime uses date-oriented
    /// wording; numeric types spell out the magnitude comparison; everything else uses plain wording.
    /// Falls back to the split enum name for any operator not explicitly mapped.
    /// </summary>
    public static string LabelFor(SearchComparisonType op, AttributeDataType type)
    {
        if (type == AttributeDataType.DateTime)
        {
            return op switch
            {
                SearchComparisonType.LessThan => "before",
                SearchComparisonType.LessThanOrEquals => "on or before",
                SearchComparisonType.GreaterThan => "after",
                SearchComparisonType.GreaterThanOrEquals => "on or after",
                SearchComparisonType.Equals => "equals",
                SearchComparisonType.NotEquals => "does not equal",
                _ => op.ToString().SplitOnCapitalLetters()
            };
        }

        if (type is AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Decimal)
        {
            return op switch
            {
                SearchComparisonType.Equals => "equals",
                SearchComparisonType.NotEquals => "does not equal",
                SearchComparisonType.LessThan => "less than",
                SearchComparisonType.LessThanOrEquals => "less than or equal to",
                SearchComparisonType.GreaterThan => "greater than",
                SearchComparisonType.GreaterThanOrEquals => "greater than or equal to",
                _ => op.ToString().SplitOnCapitalLetters()
            };
        }

        return op switch
        {
            SearchComparisonType.Equals => "equals",
            SearchComparisonType.NotEquals => "does not equal",
            SearchComparisonType.StartsWith => "starts with",
            SearchComparisonType.NotStartsWith => "does not start with",
            SearchComparisonType.EndsWith => "ends with",
            SearchComparisonType.NotEndsWith => "does not end with",
            SearchComparisonType.Contains => "contains",
            SearchComparisonType.NotContains => "does not contain",
            _ => op.ToString().SplitOnCapitalLetters()
        };
    }

    /// <summary>
    /// Renders a configured criterion's operator in friendly wording for display (the criteria chips).
    /// When the attribute type is unknown, falls back to the split enum name.
    /// </summary>
    public static string FriendlyComparison(SearchComparisonType op, AttributeDataType? type) =>
        type.HasValue ? LabelFor(op, type.Value) : op.ToString().SplitOnCapitalLetters();
}
