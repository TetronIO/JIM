// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
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
    /// Resolved inputs by Expression text. Resolution depends on nothing but the text, and the synchronisation
    /// path resolves the same handful of Expressions once per object, which at customer scale is a regex sweep per
    /// object per mapping for an answer that cannot have changed. Bounded by the number of distinct Expressions in
    /// the configuration.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ExpressionInput>> Cache = new();

    /// <summary>
    /// Returns every attribute the Expression reads, resolving each distinct Expression once. Use this on the
    /// synchronisation path; use <see cref="Resolve"/> where the Expression is one an administrator just typed.
    /// </summary>
    public static IReadOnlyList<ExpressionInput> ResolveCached(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Array.Empty<ExpressionInput>();

        return Cache.GetOrAdd(expression, Resolve);
    }

    /// <summary>
    /// Returns the inputs an Expression reads from one side of the Metaverse that the object has no value for, as
    /// the Expression addresses them (for example <c>cs["lastName"]</c>).
    /// </summary>
    /// <remarks>
    /// "No value" is an absent key, a null, or an empty string: an attribute present but blank is no value
    /// everywhere else in Attribute Flow, and an Expression concatenating it produces the same broken output as
    /// one reading an attribute that is not there at all. Inputs from the other side are left alone, because each
    /// evaluation only carries one side's values (#1361).
    /// </remarks>
    /// <param name="expression">The Expression whose inputs to check.</param>
    /// <param name="side">Which side the supplied values are.</param>
    /// <param name="availableAttributes">The attribute values the evaluation will run against.</param>
    public static IReadOnlyList<string> FindMissingInputs(
        string? expression,
        ExpressionInputSource side,
        IDictionary<string, object?> availableAttributes)
    {
        return ResolveCached(expression)
            .Where(input => input.Source == side && HasNoValue(availableAttributes, input.AttributeName))
            .Select(input => input.Accessor)
            .ToList();
    }

    private static bool HasNoValue(IDictionary<string, object?> availableAttributes, string attributeName)
    {
        if (!availableAttributes.TryGetValue(attributeName, out var value))
            return true;

        return value == null || (value is string text && text.Length == 0);
    }

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
