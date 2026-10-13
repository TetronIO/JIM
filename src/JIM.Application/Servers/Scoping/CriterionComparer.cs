// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Utilities;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// The one implementation of a criterion's comparison (#2053): what each operator in
/// <see cref="SearchComparisonOperators"/> means for one value, and how the values an attribute holds combine into one
/// answer. Synchronisation Rule scoping and Example Data dependencies both evaluate through it, so an operator cannot
/// mean one thing in one and something else in the other; the Predefined Search SQL builder is held to it by the
/// parity tests in <c>PredefinedSearchQueryDatabaseTests</c>.
/// </summary>
/// <remarks>
/// Over the values an attribute holds (#1923):
/// <list type="bullet">
/// <item><description>a positive operator is met when any value meets it;</description></item>
/// <item><description>a negated operator is met when the attribute holds at least one value and none meets the operator it negates;</description></item>
/// <item><description>"has a value" is met by one value or more, and "has no value" by none.</description></item>
/// </list>
/// Text comparisons are ordinal, ignoring case unless the criterion is case-sensitive. A pattern that is not a valid
/// regular expression throws <see cref="ArgumentException"/>, and one that runs past its time limit throws
/// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>: either way the caller fails the object
/// rather than deciding its scope on a guess.
/// </remarks>
internal static class CriterionComparer
{
    /// <summary>
    /// Evaluates a criterion over every value <paramref name="values"/> yields, stopping at the first value that decides
    /// it. <paramref name="expected"/> carries the criterion's value in the slot for <paramref name="type"/> (for a date,
    /// the boundary already resolved).
    /// </summary>
    internal static CriterionVerdict Evaluate<TValues>(ref TValues values, AttributeDataType type, SearchComparisonType op,
        in ScopingValue expected, bool caseSensitive)
        where TValues : struct, IScopingValueCursor
    {
        if (!SearchComparisonOperators.TakesValue(op))
        {
            // Presence turns on whether any value is held, never on which one, so no value is the deciding one.
            var holdsAValue = values.TryGetNext(out var first);
            var met = op == SearchComparisonType.HasValue ? holdsAValue : !holdsAValue;
            return new CriterionVerdict(met, holdsAValue ? 1 : 0, Decided: false, holdsAValue ? first : default);
        }

        // A positive operator is met by the first value that meets it; a negated operator needs every value to meet it,
        // so the first value that does not decides it is not met. For one value both reduce to comparing that value.
        var negated = SearchComparisonOperators.IsNegated(op);
        var pattern = SearchComparisonOperators.IsPattern(op) ? CriterionPatterns.Get(expected.StringValue ?? string.Empty, caseSensitive) : null;
        var heldCount = 0;
        ScopingValue lastHeld = default;
        while (values.TryGetNext(out var value))
        {
            heldCount++;
            lastHeld = value;
            if (CompareValue(type, value, expected, op, caseSensitive, pattern) != negated)
                return new CriterionVerdict(!negated, heldCount, Decided: true, value);
        }

        // No value decided it: a negated operator was met by every value it saw, provided it saw one.
        return new CriterionVerdict(negated && heldCount > 0, heldCount, Decided: false, lastHeld);
    }

    /// <summary>
    /// Whether one value meets a value-taking operator. For a negated operator, whether the value meets it, i.e. does
    /// not meet the operator it negates.
    /// </summary>
    private static bool CompareValue(AttributeDataType type, in ScopingValue actual, in ScopingValue expected, SearchComparisonType op,
        bool caseSensitive, Regex? pattern) => type switch
    {
        AttributeDataType.Text => CompareText(actual.StringValue, expected.StringValue, op, caseSensitive, pattern),
        AttributeDataType.Number => CompareOrdered(actual.IntValue, expected.IntValue, op),
        AttributeDataType.LongNumber => CompareOrdered(actual.LongValue, expected.LongValue, op),
        // Numeric and scale-insensitive: an actual of 5.00 equals a criterion of 5.0.
        AttributeDataType.Decimal => CompareOrdered(actual.DecimalValue, expected.DecimalValue, op),
        AttributeDataType.DateTime => CompareOrdered(actual.DateTimeValue, expected.DateTimeValue, op),
        AttributeDataType.Boolean => CompareEquality(actual.BoolValue, expected.BoolValue, op),
        AttributeDataType.Guid => CompareEquality(actual.GuidValue, expected.GuidValue, op),
        _ => false
    };

    private static bool CompareText(string? actual, string? expected, SearchComparisonType op, bool caseSensitive,
        Regex? pattern)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return op switch
        {
            SearchComparisonType.Equals => string.Equals(actual, expected, comparison),
            SearchComparisonType.NotEquals => !string.Equals(actual, expected, comparison),
            SearchComparisonType.StartsWith => actual?.StartsWith(expected ?? "", comparison) ?? false,
            SearchComparisonType.NotStartsWith => !(actual?.StartsWith(expected ?? "", comparison) ?? false),
            SearchComparisonType.EndsWith => actual?.EndsWith(expected ?? "", comparison) ?? false,
            SearchComparisonType.NotEndsWith => !(actual?.EndsWith(expected ?? "", comparison) ?? false),
            SearchComparisonType.Contains => actual?.Contains(expected ?? "", comparison) ?? false,
            SearchComparisonType.NotContains => !(actual?.Contains(expected ?? "", comparison) ?? false),
            SearchComparisonType.MatchesPattern => actual != null && pattern!.IsMatch(actual),
            SearchComparisonType.NotMatchesPattern => !(actual != null && pattern!.IsMatch(actual)),
            _ => false
        };
    }

    private static bool CompareOrdered<T>(T? actual, T? expected, SearchComparisonType op) where T : struct, IComparable<T>
    {
        if (!actual.HasValue || !expected.HasValue)
            return op == SearchComparisonType.Equals && actual.HasValue == expected.HasValue;

        var order = actual.GetValueOrDefault().CompareTo(expected.GetValueOrDefault());
        return op switch
        {
            SearchComparisonType.Equals => order == 0,
            SearchComparisonType.NotEquals => order != 0,
            SearchComparisonType.LessThan => order < 0,
            SearchComparisonType.LessThanOrEquals => order <= 0,
            SearchComparisonType.GreaterThan => order > 0,
            SearchComparisonType.GreaterThanOrEquals => order >= 0,
            _ => false
        };
    }

    private static bool CompareEquality<T>(T? actual, T? expected, SearchComparisonType op) where T : struct, IEquatable<T>
    {
        var equal = actual.HasValue
            ? expected.HasValue && actual.GetValueOrDefault().Equals(expected.GetValueOrDefault())
            : !expected.HasValue;
        return op switch
        {
            SearchComparisonType.Equals => equal,
            SearchComparisonType.NotEquals => !equal,
            _ => false
        };
    }
}
