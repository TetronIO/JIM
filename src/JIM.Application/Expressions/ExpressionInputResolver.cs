// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Models.Expressions;

namespace JIM.Application.Expressions;

/// <summary>
/// Resolves the attributes an Expression reads, from the Expression's own text.
/// </summary>
/// <remarks>
/// Expressions address attributes through two accessors the evaluator binds as parameters,
/// <c>mv["Attribute Name"]</c> and <c>cs["Attribute Name"]</c> (see <c>DynamicExpressoEvaluator</c>), so the set of
/// inputs is readable without evaluating anything. That is what lets the portal offer a sample value per input, and
/// what a "which inputs could be missing?" check would ask for.
///
/// This reads the text rather than the parse tree: DynamicExpresso exposes no syntax tree, and an accessor is a
/// literal-indexed parameter, so there is nothing to infer. The consequence is that an accessor whose attribute name
/// is computed rather than written out (<c>cs[someVariable]</c>) is not an input this can see; nothing in JIM
/// produces one today, and a caller must treat the result as "the inputs written down", not "everything reachable".
/// </remarks>
public static partial class ExpressionInputResolver
{
    /// <summary>
    /// Returns every attribute the Expression reads, in the order it first mentions them, without duplicates.
    /// </summary>
    /// <remarks>
    /// The same attribute name on both sides yields two inputs: <c>mv["mail"]</c> and <c>cs["mail"]</c> hold
    /// different values, and collapsing them would test the Expression with one value where it reads two.
    /// An attribute name carrying escaped quotes is returned exactly as written in the Expression.
    /// </remarks>
    /// <param name="expression">The Expression text. Null, empty or whitespace yields no inputs.</param>
    public static IReadOnlyList<ExpressionInput> Resolve(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Array.Empty<ExpressionInput>();

        var inputs = new List<ExpressionInput>();
        var seen = new HashSet<(ExpressionInputSource Source, string AttributeName)>();

        foreach (var match in AccessorRegex().Matches(expression).Cast<Match>())
        {
            var source = match.Groups[1].Value == "mv"
                ? ExpressionInputSource.Metaverse
                : ExpressionInputSource.ConnectedSystem;
            var attributeName = match.Groups[2].Value;

            if (seen.Add((source, attributeName)))
                inputs.Add(new ExpressionInput(source, attributeName));
        }

        return inputs;
    }

    /// <summary>
    /// Matches an accessor and captures its side and attribute name. The leading word boundary keeps an identifier
    /// that merely ends in the accessor's name (<c>abcs["x"]</c>) from being read as one.
    /// </summary>
    [GeneratedRegex(@"\b(mv|cs)\s*\[\s*""((?:[^""\\]|\\.)*)""\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex AccessorRegex();
}
