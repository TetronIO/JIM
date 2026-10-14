// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using DynamicExpresso;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Search;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// The functions that mirror the operator catalogue (#2053, PRD Phase 2 requirement 7). The text comparisons and
/// <c>IsMatch</c> compare through <see cref="CriterionComparer"/>, one value at a time, so <c>StartsWith</c> in an
/// expression means exactly what "starts with" means in a scoping criterion; the pattern functions use the same
/// cached, time-limited regular expressions (<see cref="CriterionPatterns"/>) as the pattern operators.
/// </summary>
public partial class DynamicExpressoEvaluator
{
    private static void RegisterCatalogueComparisons(Interpreter target)
    {
        // True when any value satisfies them, so ! means "no value does". Exact (ordinal) unless ignoreCase is true.
        target.SetFunction("StartsWith", (Func<object?, object?, bool>)((o, prefix) => CompareText(o, prefix, SearchComparisonType.StartsWith, ignoreCase: false)));
        target.SetFunction("StartsWith", (Func<object?, object?, bool, bool>)((o, prefix, ignoreCase) => CompareText(o, prefix, SearchComparisonType.StartsWith, ignoreCase)));
        target.SetFunction("EndsWith", (Func<object?, object?, bool>)((o, suffix) => CompareText(o, suffix, SearchComparisonType.EndsWith, ignoreCase: false)));
        target.SetFunction("EndsWith", (Func<object?, object?, bool, bool>)((o, suffix, ignoreCase) => CompareText(o, suffix, SearchComparisonType.EndsWith, ignoreCase)));
        target.SetFunction("Contains", (Func<object?, object?, bool>)((o, search) => CompareText(o, search, SearchComparisonType.Contains, ignoreCase: false)));
        target.SetFunction("Contains", (Func<object?, object?, bool, bool>)((o, search, ignoreCase) => CompareText(o, search, SearchComparisonType.Contains, ignoreCase)));

        // HasValue is exactly !IsNullOrEmpty: JIM's one definition of no value.
        target.SetFunction("HasValue", (Func<object?, bool>)(o => !ExpressionValueSets.HasNoValue(o)));

        target.SetFunction("IsMatch", (Func<object?, object?, bool>)((o, pattern) => IsMatch(o, pattern, ignoreCase: false)));
        target.SetFunction("IsMatch", (Func<object?, object?, bool, bool>)IsMatch);

        target.SetFunction("RegexReplace", (Func<object?, object?, object?, object?>)((o, pattern, replacement) =>
            RegexReplace(o, pattern, replacement, ignoreCase: false)));
        target.SetFunction("RegexReplace", (Func<object?, object?, object?, bool, object?>)RegexReplace);

        target.SetFunction("RegexExtract", (Func<object?, object?, object?>)((o, pattern) => RegexExtract(o, pattern, group: 0)));
        target.SetFunction("RegexExtract", (Func<object?, object?, int, object?>)RegexExtract);
    }

    private static bool CompareText(object? input, object? operand, SearchComparisonType op, bool ignoreCase)
    {
        var expected = new ScopingValue(AsString(operand) ?? string.Empty, null, null, null, null, null, null);
        return ExpressionValueSets.Any(input, value => MeetsText(AsString(value), op, expected, ignoreCase));
    }

    private static bool IsMatch(object? input, object? pattern, bool ignoreCase)
    {
        var expected = new ScopingValue(PatternText(pattern), null, null, null, null, null, null);
        return WithinTimeLimit(() =>
            ExpressionValueSets.Any(input, value => MeetsText(AsString(value), SearchComparisonType.MatchesPattern, expected, ignoreCase)));
    }

    private static object? RegexReplace(object? input, object? pattern, object? replacement, bool ignoreCase)
    {
        var regex = CriterionPatterns.Get(PatternText(pattern), caseSensitive: !ignoreCase);
        var replacementText = AsString(replacement) ?? string.Empty;
        return WithinTimeLimit(() => ExpressionValueSets.Map(input, value =>
            AsString(value) is { } text ? regex.Replace(text, replacementText) : null));
    }

    private static object? RegexExtract(object? input, object? pattern, int group)
    {
        var regex = CriterionPatterns.Get(PatternText(pattern), caseSensitive: true);
        if (group < 0 || group >= regex.GetGroupNumbers().Length)
            throw new ExpressionEvaluationException($"RegexExtract's pattern has no group {group}.");

        return WithinTimeLimit(() => ExpressionValueSets.Map(input, value =>
        {
            if (AsString(value) is not { } text)
                return null;

            var match = regex.Match(text);
            return match.Success && match.Groups[group].Success ? match.Groups[group].Value : null;
        }));
    }

    /// <summary>
    /// One value against a text operator, through the one comparison every criterion uses. No value never meets it.
    /// </summary>
    private static bool MeetsText(string? value, SearchComparisonType op, ScopingValue expected, bool ignoreCase)
    {
        if (value == null)
            return false;

        var cursor = new OneValueCursor(new ScopingValue(value, null, null, null, null, null, null));
        return CriterionComparer.Evaluate(ref cursor, AttributeDataType.Text, op, expected, caseSensitive: !ignoreCase).Met;
    }

    /// <summary>
    /// A pattern argument as text, refused as the pattern operators refuse one when it is not a regular expression (or
    /// is too long), so the message is the same everywhere a pattern is written.
    /// </summary>
    private static string PatternText(object? pattern)
    {
        var text = AsString(pattern);
        if (SearchComparisonOperators.ValidatePattern(text) is { } error)
            throw new ExpressionEvaluationException(error);

        return text!;
    }

    /// <summary>
    /// Runs a pattern function, turning a match that runs past the time limit into an expression error on the object
    /// rather than a guess at its result.
    /// </summary>
    private static T WithinTimeLimit<T>(Func<T> evaluate)
    {
        try
        {
            return evaluate();
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ExpressionEvaluationException(
                $"The pattern ran past its time limit of {CriterionPatterns.MatchTimeout.TotalMilliseconds:0} ms on one of the values; " +
                "simplify the pattern (nested repetition such as (a+)+ is the usual cause).");
        }
    }
}
