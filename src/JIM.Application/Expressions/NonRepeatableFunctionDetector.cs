// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Models.Expressions;

namespace JIM.Application.Expressions;

/// <summary>
/// Finds the calls in an Expression that return a different value each time they are evaluated: the functions the
/// function catalogue marks so (<see cref="ExpressionFunction.IsRepeatable"/>: <c>Now()</c>, <c>Today()</c>,
/// <c>RandomPassword()</c>, <c>RandomPassphrase()</c>) and the equivalent members the interpreter's common types
/// expose (<c>DateTime.Now</c>, <c>DateTime.UtcNow</c>, <c>DateTime.Today</c>, <c>Guid.NewGuid()</c>).
/// </summary>
/// <remarks>
/// Used to warn, never to block, when such a call sits in an Attribute Flow that derives a Metaverse attribute
/// (#1750, plan decision 15): the derived value changes on every synchronisation. Like
/// <see cref="ExpressionInputResolver"/>, this reads the text, not a parse tree; string literals are removed first so
/// a function name inside quotes is not reported. The functions come from <see cref="ExpressionFunctionCatalogue"/>, so a
/// new non-repeatable function is found as soon as the catalogue describes it (#2053).
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
        return NonRepeatableCallRegex.Matches(code)
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

    private static readonly Regex NonRepeatableCallRegex = new(
        @"\b(?<type>DateTime|DateTimeOffset)\s*\.\s*(?<member>UtcNow|Now|Today)\b" +
        @"|(?<guid>\bGuid\s*\.\s*NewGuid\s*\()" +
        $@"|(?<![\w.])(?<function>{string.Join('|', ExpressionFunctionCatalogue.All.Where(f => !f.IsRepeatable).Select(f => Regex.Escape(f.Name)))})\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
