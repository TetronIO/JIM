// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Expressions;
using JIM.Models.Interfaces;

namespace JIM.Web;

/// <summary>
/// Whether an expression typed into the portal parses (#2053, PRD Phase 2 requirement 9): the same check the REST API
/// makes before it saves an Attribute Flow, so the portal refuses what the API refuses, an unknown function included,
/// and says why under the field as it is typed.
/// </summary>
public static partial class ExpressionSyntaxCheck
{
    /// <summary>
    /// Why <paramref name="expression"/> cannot be saved, or null when it parses or is empty (an empty expression is
    /// the field's Required check, not a syntax error).
    /// </summary>
    public static string? ErrorFor(IExpressionEvaluator evaluator, string? expression)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var result = evaluator.Validate(expression);
        if (result.IsValid)
            return null;

        var message = result.ErrorMessage ?? "The expression does not parse.";

        // Function names are case-sensitive, and the commonest unknown function is a real one in the wrong case.
        var unknown = UnknownIdentifier().Match(message);
        if (unknown.Success && ExpressionFunctionCatalogue.All.FirstOrDefault(f =>
                string.Equals(f.Name, unknown.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)) is { } function)
        {
            message += $" Function names are case-sensitive: did you mean {function.Name}?";
        }

        return message;
    }

    [GeneratedRegex(@"Unknown identifier '(?<name>\w+)'", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownIdentifier();
}
