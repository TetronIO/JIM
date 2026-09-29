// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Application.Expressions;

/// <summary>
/// Finds the calls in an Expression that return a different value each time they are evaluated: the clock and
/// random functions <see cref="DynamicExpressoEvaluator"/> registers (<c>Now()</c>, <c>Today()</c>,
/// <c>RandomPassword()</c>, <c>RandomPassphrase()</c>) and the equivalent members the interpreter's common types
/// expose (<c>DateTime.Now</c>, <c>DateTime.UtcNow</c>, <c>DateTime.Today</c>, <c>Guid.NewGuid()</c>).
/// </summary>
/// <remarks>
/// Used to warn, never to block, when such a call sits in an Attribute Flow that derives a Metaverse attribute
/// (#1750, plan decision 15): the derived value changes on every synchronisation. Like
/// <see cref="ExpressionInputResolver"/>, this reads the text, not a parse tree; string literals are removed first so
/// a function name inside quotes is not reported. Keep this list in step with the evaluator's registrations.
/// </remarks>
public static partial class NonRepeatableFunctionDetector
{
    /// <summary>
    /// The non-repeatable calls <paramref name="expression"/> makes, each named once in the form an administrator
    /// would write it (for example <c>Now()</c> or <c>DateTime.UtcNow</c>), in the order first made.
    /// </summary>
    public static IReadOnlyList<string> Find(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var code = StringLiteralRegex().Replace(expression, "\"\"");
        return NonRepeatableCallRegex().Matches(code)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(Match match)
    {
        if (match.Groups["member"].Success)
            return $"{match.Groups["type"].Value}.{match.Groups["member"].Value}";

        if (match.Groups["guid"].Success)
            return "Guid.NewGuid()";

        return $"{match.Groups["function"].Value}()";
    }

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(
        @"\b(?<type>DateTime|DateTimeOffset)\s*\.\s*(?<member>UtcNow|Now|Today)\b" +
        @"|(?<guid>\bGuid\s*\.\s*NewGuid\s*\()" +
        @"|(?<![\w.])(?<function>Now|Today|RandomPassword|RandomPassphrase)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex NonRepeatableCallRegex();
}
