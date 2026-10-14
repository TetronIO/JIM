// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Expressions;

namespace JIM.Application.Expressions;

/// <summary>
/// The rules every expression function follows for several values (#2053, PRD Phase 2 requirement 8): a transform
/// applies to each value, a comparison is true when any value satisfies it, a one-value position takes one value as it
/// is and refuses several, and the whole-set functions (<c>Count</c>, <c>Min</c>, <c>Max</c>, <c>Join</c>) work on the
/// set. "Several values" is <see cref="ExpressionValues"/>, or any other collection (an array an older function
/// returned), read the same way.
/// </summary>
internal static class ExpressionValueSets
{
    /// <summary>
    /// <paramref name="transform"/> applied to each value, in order: several values give several results, one value
    /// gives one.
    /// </summary>
    internal static object? Map(object? input, Func<object?, object?> transform) => RefuseReflection(input) switch
    {
        ExpressionValues values => new ExpressionValues(values.Select(transform), values.SourceName),
        _ when ExpressionResultValues.IsCollection(input) => new ExpressionValues(ExpressionResultValues.Elements(input).Select(transform), null),
        _ => transform(input)
    };

    /// <summary>
    /// Whether any value satisfies <paramref name="predicate"/>; one value is tested as it is, and no value never
    /// satisfies it.
    /// </summary>
    internal static bool Any(object? input, Func<object?, bool> predicate) =>
        IsSeveral(RefuseReflection(input)) ? ExpressionResultValues.Elements(input).Any(predicate) : predicate(input);

    /// <summary>
    /// The one value a one-value position needs: none is null, one is itself, and several are the error naming the
    /// attribute and the count.
    /// </summary>
    internal static object? One(object? input) => RefuseReflection(input) switch
    {
        ExpressionValues values => values.Single(),
        _ when ExpressionResultValues.IsCollection(input) => new ExpressionValues(ExpressionResultValues.Elements(input), null).Single(),
        _ => input
    };

    /// <summary>
    /// The values held, for a whole-set function: none for no value (null, empty text or an empty list), each value of
    /// several, or the one value.
    /// </summary>
    internal static IReadOnlyList<object?> Values(object? input) =>
        HasNoValue(RefuseReflection(input)) ? [] : ExpressionResultValues.Elements(input).Where(value => value is not null).ToList();

    /// <summary>
    /// <paramref name="value"/> as it is, unless it is a reflection object (a <see cref="Type"/>, member, assembly or
    /// module) or a delegate, which no attribute expression has a use for: that is an expression error. Every function
    /// that hands a value on calls this, and so does the evaluator on the result, so a reflection object that slipped
    /// past the parse-time guards still goes nowhere (see <c>engineering/EXPRESSION_SECURITY.md</c>, "Late binding").
    /// </summary>
    internal static object? RefuseReflection(object? value) =>
        value is Delegate || (value != null && ReflectionRefusingVisitor.IsReflectionType(value.GetType()))
            ? throw new ExpressionEvaluationException("Reflection is not available in expressions.")
            : value;

    /// <summary>
    /// JIM's one definition of no value: null, empty text, or a list with no values. A Binary value holding no bytes
    /// is no value too.
    /// </summary>
    internal static bool HasNoValue(object? input) => input switch
    {
        null => true,
        string text => text.Length == 0,
        byte[] bytes => bytes.Length == 0,
        _ when ExpressionResultValues.IsCollection(input) => !ExpressionResultValues.Elements(input).Any(value => !HasNoValue(value)),
        _ => false
    };

    /// <summary>
    /// The values of <paramref name="input"/> as the values a whole-set function returns: none is null, one is
    /// itself, several keep their order.
    /// </summary>
    internal static object? Collapse(IReadOnlyList<object?> values, string? sourceName) => values.Count switch
    {
        0 => null,
        1 => values[0],
        _ => new ExpressionValues(values, sourceName)
    };

    /// <summary>
    /// The smallest or largest value: numbers by value, dates by instant, text by ordinal. Null for no value; mixed
    /// kinds, Booleans, Guids and Binary have no order an administrator could rely on, so they are an error.
    /// </summary>
    internal static object? Extreme(object? input, bool largest, string functionName)
    {
        var values = Values(input);
        if (values.Count == 0)
            return null;

        var kind = KindOf(values[0]!, functionName);
        if (values.Any(value => KindOf(value!, functionName) != kind))
            throw new ExpressionEvaluationException($"{functionName} needs values of one kind: numbers, dates or text.");

        var best = values[0]!;
        foreach (var value in values.Skip(1))
        {
            var order = Compare(value!, best, kind);
            if (largest ? order > 0 : order < 0)
                best = value!;
        }

        return best;
    }

    private static bool IsSeveral(object? input) => input is ExpressionValues || ExpressionResultValues.IsCollection(input);

    private enum OrderedKind { Number, Date, Text }

    private static OrderedKind KindOf(object value, string functionName) => value switch
    {
        int or long or decimal or short or byte => OrderedKind.Number,
        DateTime or DateTimeOffset => OrderedKind.Date,
        string => OrderedKind.Text,
        _ => throw new ExpressionEvaluationException(
            $"{functionName} works on numbers, dates or text, not a {DescribeKind(value)} value.")
    };

    private static string DescribeKind(object value) => value switch
    {
        bool => "Boolean",
        Guid => "Guid",
        byte[] => "Binary",
        _ => value.GetType().Name
    };

    private static int Compare(object a, object b, OrderedKind kind) => kind switch
    {
        OrderedKind.Number => Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture)),
        OrderedKind.Date => ExpressionDates.TryRead(a, out var dateA) && ExpressionDates.TryRead(b, out var dateB) ? dateA.Ticks.CompareTo(dateB.Ticks) : 0,
        _ => string.CompareOrdinal((string)a, (string)b)
    };
}
