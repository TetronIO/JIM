// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities;

/// <summary>
/// The single source of truth for which <see cref="SearchComparisonType"/> operators are valid for a given
/// attribute <see cref="AttributeDataType"/>. Pure and deterministic, with no dependencies on data access or DI,
/// so every layer can share one rule: the REST API and PowerShell validation (<c>BuildCriterion</c>), the
/// Predefined Search and Synchronisation Rule scoping editors (operator dropdowns), the Synchronisation Rule write path
/// (<c>CreateOrUpdateSyncRuleAsync</c>), and the scoping evaluator.
///
/// The returned lists are in canonical display order. <see cref="LabelFor"/> gives each operator its friendly,
/// type-appropriate label (for example "on or before" for dates), shared by the criteria editors and by scoping
/// explanations (#348) so the two never word a comparison differently.
/// </summary>
public static class SearchComparisonOperators
{
    // Text supports the substring/affix family plus equality.
    private static readonly SearchComparisonType[] TextOperators =
    {
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals,
        SearchComparisonType.StartsWith,
        SearchComparisonType.NotStartsWith,
        SearchComparisonType.EndsWith,
        SearchComparisonType.NotEndsWith,
        SearchComparisonType.Contains,
        SearchComparisonType.NotContains
    };

    // DateTime is ordered; display leads with the date-oriented wording (before / on or before / after / ...).
    private static readonly SearchComparisonType[] DateTimeOperators =
    {
        SearchComparisonType.LessThan,
        SearchComparisonType.LessThanOrEquals,
        SearchComparisonType.GreaterThan,
        SearchComparisonType.GreaterThanOrEquals,
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals
    };

    // Numbers are ordered; display leads with equality.
    private static readonly SearchComparisonType[] NumberOperators =
    {
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals,
        SearchComparisonType.LessThan,
        SearchComparisonType.LessThanOrEquals,
        SearchComparisonType.GreaterThan,
        SearchComparisonType.GreaterThanOrEquals
    };

    // Boolean and Guid support equality only.
    private static readonly SearchComparisonType[] EqualityOperators =
    {
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals
    };

    private static readonly SearchComparisonType[] NoOperators = Array.Empty<SearchComparisonType>();

    /// <summary>
    /// Returns the comparison operators valid for the supplied attribute data type, in canonical display order.
    /// Unsupported types (Binary, Reference, NotSet) return an empty list.
    /// </summary>
    public static IReadOnlyList<SearchComparisonType> ValidOperatorsFor(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => TextOperators,
        AttributeDataType.DateTime => DateTimeOperators,
        AttributeDataType.Number => NumberOperators,
        AttributeDataType.LongNumber => NumberOperators,
        AttributeDataType.Decimal => NumberOperators,
        AttributeDataType.Boolean => EqualityOperators,
        AttributeDataType.Guid => EqualityOperators,
        _ => NoOperators
    };

    /// <summary>
    /// Returns true if the operator is applicable to the attribute data type. <see cref="SearchComparisonType.NotSet"/>
    /// is never valid.
    /// </summary>
    public static bool IsValid(SearchComparisonType op, AttributeDataType type) =>
        op != SearchComparisonType.NotSet && ValidOperatorsFor(type).Contains(op);

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
}
