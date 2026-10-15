// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers;

/// <summary>What is wrong with an Attribute Flow's expression, as <see cref="AttributeFlowExpressionValidator"/> finds it.</summary>
public enum AttributeFlowExpressionProblemKind
{
    /// <summary>It does not parse, or calls a function there is none of.</summary>
    DoesNotParse,

    /// <summary>It reads the side a synchronisation does not give it: an export flow reading <c>cs[...]</c>.</summary>
    WrongSide,

    /// <summary>It reads an attribute the object type does not have, so that input never has a value.</summary>
    UnknownAttribute,

    /// <summary>Its result is a type the target can never hold.</summary>
    ResultTypeMismatch,

    /// <summary>It calls a function that returns a different value each time; allowed, but worth knowing.</summary>
    NonRepeatable
}

/// <summary>One problem with an Attribute Flow's expression, with the message an administrator is shown.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Message">What is wrong, naming the Attribute Flow by its target.</param>
public sealed record AttributeFlowExpressionProblem(AttributeFlowExpressionProblemKind Kind, string Message)
{
    /// <summary>
    /// True for a problem that never stops a save. The others stop the save of a mapping that is new or changed, and
    /// are reported as warnings on one the save leaves as it was (#2055 Phase 3).
    /// </summary>
    public bool IsWarningOnly => Kind == AttributeFlowExpressionProblemKind.NonRepeatable;
}

/// <summary>
/// Checks an Attribute Flow's expressions when it is saved (#2055 Phase 3), the Attribute Flow counterpart of
/// <see cref="ObjectMatchingRuleValidator"/>. Called by the application layer on every save path, so the portal, the
/// REST API and PowerShell refuse the same expressions with the same message. It reports; whether a problem blocks is
/// the save's decision, by whether the mapping is new or changed.
/// </summary>
/// <remarks>
/// A Metaverse-Derived Attribute Flow (an import flow reading <c>mv[...]</c>, #1750) has its <c>mv</c> inputs and its
/// non-repeatable functions checked by <c>DerivedFlowValidator</c>, so they are not checked twice here.
/// </remarks>
public sealed class AttributeFlowExpressionValidator(IExpressionEvaluator expressionEvaluator)
{
    /// <summary>
    /// Every problem with <paramref name="mapping"/>'s expression sources, for a Synchronisation Rule of
    /// <paramref name="direction"/>. A null object type skips only the checks that need it.
    /// </summary>
    public IReadOnlyList<AttributeFlowExpressionProblem> Validate(
        SyncRuleMapping mapping,
        SyncRuleDirection direction,
        ConnectedSystemObjectType? connectedSystemObjectType,
        MetaverseObjectType? metaverseObjectType)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var problems = new List<AttributeFlowExpressionProblem>();
        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "this Attribute Flow";

        foreach (var source in mapping.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Expression)).OrderBy(s => s.Order))
            problems.AddRange(Validate(source.Expression!, mapping, direction, targetName, connectedSystemObjectType, metaverseObjectType));

        return problems;
    }

    private IEnumerable<AttributeFlowExpressionProblem> Validate(
        string expression,
        SyncRuleMapping mapping,
        SyncRuleDirection direction,
        string targetName,
        ConnectedSystemObjectType? connectedSystemObjectType,
        MetaverseObjectType? metaverseObjectType)
    {
        var parsed = expressionEvaluator.Validate(expression);
        if (!parsed.IsValid)
        {
            // Nothing else can be said about an expression that does not parse.
            yield return Problem(AttributeFlowExpressionProblemKind.DoesNotParse,
                $"The expression for '{targetName}' does not parse: {parsed.ErrorMessage}");
            yield break;
        }

        var inputs = ExpressionInputResolver.Resolve(expression);
        var isExport = direction == SyncRuleDirection.Export;
        var readsMetaverseOnImport = !isExport && inputs.Any(i => i.Source == ExpressionInputSource.Metaverse);

        foreach (var input in inputs)
        {
            if (isExport && input.Source == ExpressionInputSource.ConnectedSystem)
            {
                yield return Problem(AttributeFlowExpressionProblemKind.WrongSide,
                    $"The expression for '{targetName}' reads {input.Accessor}, but an export Attribute Flow reads the Metaverse Object, " +
                    "so it never has a value. Read mv[\"...\"] instead.");
                continue;
            }

            var unknown = input.Source switch
            {
                ExpressionInputSource.ConnectedSystem when connectedSystemObjectType != null &&
                    !connectedSystemObjectType.Attributes.Any(a => Same(a.Name, input.AttributeName)) => connectedSystemObjectType.Name,
                ExpressionInputSource.Metaverse when isExport && metaverseObjectType != null &&
                    !metaverseObjectType.Attributes.Any(a => Same(a.Name, input.AttributeName)) => metaverseObjectType.Name,
                _ => null
            };
            if (unknown != null)
            {
                yield return Problem(AttributeFlowExpressionProblemKind.UnknownAttribute,
                    $"The expression for '{targetName}' reads {input.Accessor}, which is not an attribute of '{unknown}', so it never has a value.");
            }
        }

        // A Generated Value's base has a token added to it as text; the generation validator owns the target's type.
        var targetType = isExport ? mapping.TargetConnectedSystemAttribute?.Type : mapping.TargetMetaverseAttribute?.Type;
        if (mapping.Generation == null && targetType is { } type &&
            ExpressionResultTypeCheck.DescribeProblem(expression, type, targetName) is { } mismatch)
        {
            yield return Problem(AttributeFlowExpressionProblemKind.ResultTypeMismatch, mismatch);
        }

        if (!readsMetaverseOnImport)
        {
            var nonRepeatable = NonRepeatableFunctionDetector.Find(expression);
            if (nonRepeatable.Count > 0)
            {
                yield return Problem(AttributeFlowExpressionProblemKind.NonRepeatable,
                    $"The expression for '{targetName}' calls {string.Join(", ", nonRepeatable)}, which returns a different value each " +
                    "time, so every synchronisation can change the value it flows.");
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static AttributeFlowExpressionProblem Problem(AttributeFlowExpressionProblemKind kind, string message) => new(kind, message);
}
