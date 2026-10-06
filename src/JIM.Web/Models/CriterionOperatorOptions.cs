// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// UI presentation layer for comparison operators in the criteria editors (Predefined Search and Synchronisation
/// Rule scoping). The set and order of valid operators per attribute type comes from the shared, layer-agnostic
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
    /// Returns the operators valid for the attribute type (per <see cref="SearchComparisonOperators"/>),
    /// in display order, each with its friendly label.
    /// </summary>
    public static IReadOnlyList<OperatorOption> ForType(AttributeDataType type) =>
        SearchComparisonOperators.ValidOperatorsFor(type)
            .Select(op => new OperatorOption(op, LabelFor(op, type)))
            .ToList();

    /// <summary>
    /// The friendly label for an operator in the context of an attribute type; see
    /// <see cref="SearchComparisonOperators.LabelFor"/>, which scoping explanations share.
    /// </summary>
    public static string LabelFor(SearchComparisonType op, AttributeDataType type) => SearchComparisonOperators.LabelFor(op, type);

    /// <summary>
    /// Renders a configured criterion's operator in friendly wording for display (the criteria chips).
    /// When the attribute type is unknown, falls back to the split enum name.
    /// </summary>
    public static string FriendlyComparison(SearchComparisonType op, AttributeDataType? type) =>
        type.HasValue ? LabelFor(op, type.Value) : op.ToString().SplitOnCapitalLetters();
}
