// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Interfaces;
using JIM.Models.Logic;

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

    /// <summary>
    /// Why <paramref name="expression"/> cannot be saved as a source of <paramref name="mapping"/> on
    /// <paramref name="syncRule"/> (#2055 Phase 3): the syntax error first, as <see cref="ErrorFor"/> words it, then the
    /// first problem the save would refuse a new or changed mapping for, from the validator the application layer runs.
    /// Null when it would be saved; a warning (a non-repeatable function) never blocks.
    /// </summary>
    public static string? ErrorForAttributeFlow(IExpressionEvaluator evaluator, string? expression, SyncRuleMapping mapping, SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(syncRule);

        var syntaxError = ErrorFor(evaluator, expression);
        if (syntaxError != null || string.IsNullOrWhiteSpace(expression))
            return syntaxError;

        // The expression being typed, checked as the mapping it would become; nothing on the page is changed.
        var proposed = new SyncRuleMapping
        {
            TargetMetaverseAttribute = mapping.TargetMetaverseAttribute,
            TargetMetaverseAttributeId = mapping.TargetMetaverseAttributeId,
            TargetConnectedSystemAttribute = mapping.TargetConnectedSystemAttribute,
            TargetConnectedSystemAttributeId = mapping.TargetConnectedSystemAttributeId,
            Generation = mapping.Generation
        };
        proposed.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });

        // A type loaded without its attributes says nothing about which it has, so it is not used to call one unknown.
        var connectedSystemObjectType = syncRule.ConnectedSystemObjectType is { Attributes.Count: > 0 } cs ? cs : null;
        var metaverseObjectType = syncRule.MetaverseObjectType is { Attributes.Count: > 0 } mv ? mv : null;

        return new AttributeFlowExpressionValidator(evaluator)
            .Validate(proposed, syncRule.Direction, connectedSystemObjectType, metaverseObjectType)
            .FirstOrDefault(problem => !problem.IsWarningOnly)?.Message;
    }

    [GeneratedRegex(@"Unknown identifier '(?<name>\w+)'", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownIdentifier();
}
