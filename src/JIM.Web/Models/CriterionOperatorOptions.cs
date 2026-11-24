// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Utilities;
using JIM.Web.Localisation;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// UI presentation layer for comparison operators in the criteria editors (Predefined Search and Synchronisation
/// Rule scoping), each offering the operators its context allows. The set and order of valid operators per attribute type comes from the shared, layer-agnostic
/// <see cref="SearchComparisonOperators"/> rule, as do the friendly, type-specific labels (for example DateTime
/// renders "before" / "on or before" rather than "less than"); this type pairs them up for the editors' select
/// controls, so both editors and scoping explanations stay consistent without duplicating either.
/// </summary>
public static class CriterionOperatorOptions
{
    /// <summary>
    /// An operator paired with its friendly, type-appropriate label, for binding to a select control.
    /// </summary>
    public sealed record OperatorOption(SearchComparisonType Operator, string Label);

    /// <summary>
    /// Returns the operators valid for the attribute type in the editor's context (per
    /// <see cref="SearchComparisonOperators"/>), in display order, each with its friendly label.
    /// </summary>
    public static IReadOnlyList<OperatorOption> ForType(AttributeDataType type, CriterionContext context) =>
        SearchComparisonOperators.ValidOperatorsFor(type, context)
            .Select(op => new OperatorOption(op, LabelFor(op, type)))
            .ToList();

    /// <summary>
    /// The friendly label for an operator in the context of an attribute type; see
    /// <see cref="SearchComparisonOperators.LabelFor"/>, which scoping explanations share.
    /// </summary>
    public static string LabelFor(SearchComparisonType op, AttributeDataType type) =>
        LocalisedLabel(op, type) ?? SearchComparisonOperators.LabelFor(op, type);

    /// <summary>
    /// The label in the viewer's language (#3030), word for word what <see cref="SearchComparisonOperators.LabelFor"/>
    /// says in English; null for an operator with no label of its own, which keeps that English. Read on every call,
    /// never held, so each viewer sees their own language.
    /// </summary>
    private static string? LocalisedLabel(SearchComparisonType op, AttributeDataType type)
    {
        if (type == AttributeDataType.DateTime)
        {
            switch (op)
            {
                case SearchComparisonType.LessThan: return CriterionOperatorText.LabelDateBefore;
                case SearchComparisonType.LessThanOrEquals: return CriterionOperatorText.LabelDateOnOrBefore;
                case SearchComparisonType.GreaterThan: return CriterionOperatorText.LabelDateAfter;
                case SearchComparisonType.GreaterThanOrEquals: return CriterionOperatorText.LabelDateOnOrAfter;
            }
        }

        return op switch
        {
            SearchComparisonType.Equals => CriterionOperatorText.LabelEquals,
            SearchComparisonType.NotEquals => CriterionOperatorText.LabelNotEquals,
            SearchComparisonType.StartsWith => CriterionOperatorText.LabelStartsWith,
            SearchComparisonType.NotStartsWith => CriterionOperatorText.LabelNotStartsWith,
            SearchComparisonType.EndsWith => CriterionOperatorText.LabelEndsWith,
            SearchComparisonType.NotEndsWith => CriterionOperatorText.LabelNotEndsWith,
            SearchComparisonType.Contains => CriterionOperatorText.LabelContains,
            SearchComparisonType.NotContains => CriterionOperatorText.LabelNotContains,
            SearchComparisonType.LessThan => CriterionOperatorText.LabelLessThan,
            SearchComparisonType.LessThanOrEquals => CriterionOperatorText.LabelLessThanOrEquals,
            SearchComparisonType.GreaterThan => CriterionOperatorText.LabelGreaterThan,
            SearchComparisonType.GreaterThanOrEquals => CriterionOperatorText.LabelGreaterThanOrEquals,
            SearchComparisonType.HasValue => CriterionOperatorText.LabelHasValue,
            SearchComparisonType.HasNoValue => CriterionOperatorText.LabelHasNoValue,
            SearchComparisonType.MatchesPattern => CriterionOperatorText.LabelMatchesPattern,
            SearchComparisonType.NotMatchesPattern => CriterionOperatorText.LabelNotMatchesPattern,
            _ => null
        };
    }

    /// <summary>
    /// Renders a configured criterion's operator in friendly wording for display (the criteria chips).
    /// When the attribute type is unknown, falls back to the split enum name.
    /// </summary>
    public static string FriendlyComparison(SearchComparisonType op, AttributeDataType? type) =>
        type.HasValue ? LabelFor(op, type.Value) : op.ForDisplay();

    /// <summary>
    /// A condition as a phrase, for example "Employee Type equals Contractor" or "Leaving Date has no value": the
    /// attribute, the operator's label, and the value unless the operator takes none.
    /// </summary>
    public static string DescribeCondition(string attributeName, SearchComparisonType op, AttributeDataType type, string? value) =>
        SearchComparisonOperators.TakesValue(op)
            ? $"{attributeName} {LabelFor(op, type)} {value}"
            : $"{attributeName} {LabelFor(op, type)}";
}
