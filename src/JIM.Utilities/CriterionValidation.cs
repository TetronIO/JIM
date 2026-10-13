// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities;

/// <summary>
/// Whether a criterion can be saved (#2053): its operator applies to its attribute's data type in its context, it
/// carries the value its operator compares against, and a pattern is a regular expression JIM can run. Every write path
/// (the portal's editors, the REST API and the Synchronisation Rule write path) asks this, so the rules cannot differ by
/// surface. Relative-date fields are checked here only for completeness; their parsing stays at the API boundary.
/// </summary>
public static class CriterionValidation
{
    /// <summary>
    /// Why the criterion cannot be saved, or null when it can. A criterion whose operator takes no value is valid
    /// whatever its value carriers hold; call <see cref="ClearValues"/> so it is not stored with a stale one.
    /// </summary>
    public static string? Validate(ICriterionValues criterion, AttributeDataType type, CriterionContext context, string attributeName)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        var op = criterion.ComparisonType;

        if (!SearchComparisonOperators.All.Contains(op))
            return $"Invalid comparison type '{op}'.";

        if (!SearchComparisonOperators.OperatorsFor(context).Contains(op))
            return $"Operator '{op}' is not available in {ContextName(context)}, which run in the database; " +
                   "patterns are offered in Synchronisation Rule scoping and Example Data.";

        if (!SearchComparisonOperators.IsValid(op, type, context))
            return $"Operator '{op}' is not valid for the {type} attribute '{attributeName}'.";

        if (!SearchComparisonOperators.TakesValue(op))
            return null;

        if (criterion.ValueMode == DateCriteriaValueMode.Relative)
        {
            return criterion.RelativeCount.HasValue && criterion.RelativeUnit.HasValue && criterion.RelativeDirection.HasValue
                ? null
                : "A relative criterion needs a count, a unit and a direction.";
        }

        var missing = type switch
        {
            AttributeDataType.Text => string.IsNullOrEmpty(criterion.StringValue) ? "StringValue is required for a Text criterion." : null,
            AttributeDataType.Number => criterion.IntValue.HasValue ? null : "IntValue is required for a Number criterion.",
            AttributeDataType.LongNumber => criterion.LongValue.HasValue ? null : "LongValue is required for a LongNumber criterion.",
            AttributeDataType.Decimal => criterion.DecimalValue.HasValue ? null : "DecimalValue is required for a Decimal criterion.",
            AttributeDataType.DateTime => criterion.DateTimeValue.HasValue ? null : "DateTimeValue is required for a DateTime criterion.",
            AttributeDataType.Boolean => criterion.BoolValue.HasValue ? null : "BoolValue is required for a Boolean criterion.",
            AttributeDataType.Guid => criterion.GuidValue.HasValue ? null : "GuidValue is required for a Guid criterion.",
            _ => null
        };
        if (missing != null)
            return $"{missing} Use 'HasValue' or 'HasNoValue' to test whether '{attributeName}' holds a value at all.";

        return SearchComparisonOperators.IsPattern(op) ? SearchComparisonOperators.ValidatePattern(criterion.StringValue) : null;
    }

    /// <summary>
    /// Builds and validates a criterion whose value is held as text (an Example Data dependency), reading the text as the
    /// attribute's data type first. Returns why it cannot be saved or evaluated, or null with <paramref name="criterion"/>
    /// ready to evaluate.
    /// </summary>
    public static string? ValidateText(SearchComparisonType op, string? text, AttributeDataType type, CriterionContext context,
        string attributeName, bool caseSensitive, out CriterionValues criterion)
    {
        criterion = new CriterionValues { ComparisonType = op, CaseSensitive = caseSensitive };
        if (SearchComparisonOperators.TakesValue(op) && SearchComparisonOperators.IsValid(op, type, context))
        {
            var readError = CriterionValueText.Read(text, type, criterion);
            if (readError != null)
                return readError;
        }

        return Validate(criterion, type, context, attributeName);
    }

    /// <summary>
    /// Clears every value carrier, and the relative-date fields, for a criterion whose operator takes no value, so it
    /// is never stored holding a value that plays no part in it. Does nothing for an operator that takes a value.
    /// </summary>
    public static void ClearValues(ICriterionValues criterion)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        if (SearchComparisonOperators.TakesValue(criterion.ComparisonType))
            return;

        criterion.StringValue = null;
        criterion.IntValue = null;
        criterion.LongValue = null;
        criterion.DecimalValue = null;
        criterion.DateTimeValue = null;
        criterion.BoolValue = null;
        criterion.GuidValue = null;
        criterion.ValueMode = DateCriteriaValueMode.Absolute;
        criterion.RelativeCount = null;
        criterion.RelativeUnit = null;
        criterion.RelativeDirection = null;
    }

    private static string ContextName(CriterionContext context) => context switch
    {
        CriterionContext.PredefinedSearch => "Predefined Searches",
        CriterionContext.ExampleData => "Example Data",
        _ => "Synchronisation Rule scoping"
    };
}
