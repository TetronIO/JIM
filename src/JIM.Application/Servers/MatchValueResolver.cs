// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Works out what an Object Matching Rule compares for one Connected System Object (#2052): the values to look up,
/// no value (so the next rule is tried), or why the rule could not be worked out (so the object is stopped). Import
/// join, export matching and both previews resolve through here, so they cannot disagree about what a rule computes.
/// </summary>
/// <remarks>
/// Nothing is ever skipped quietly. A value that cannot be converted to the Metaverse attribute's type, an expression
/// that throws, and a rule whose shape cannot be compared are all failures: skipping them would let the object match
/// nothing and project or provision a duplicate.
/// </remarks>
public sealed class MatchValueResolver
{
    private readonly IExpressionEvaluator _expressionEvaluator;

    public MatchValueResolver(IExpressionEvaluator expressionEvaluator)
    {
        _expressionEvaluator = expressionEvaluator;
    }

    /// <summary>
    /// Resolves <paramref name="rule"/> against an object's attribute values.
    /// </summary>
    /// <param name="rule">The rule, with its Target Metaverse Attribute and source loaded.</param>
    /// <param name="attributeValues">The Connected System Object's attribute values. Each value's
    /// <see cref="ConnectedSystemObjectAttributeValue.Attribute"/> names it for an expression; where a loader left it
    /// unset, <paramref name="objectType"/> supplies the definition.</param>
    /// <param name="objectType">The object's type, used only to name values whose attribute definition is not loaded.</param>
    public MatchValueResolution Resolve(
        ObjectMatchingRule rule,
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(attributeValues);

        var target = rule.TargetMetaverseAttribute;
        var source = rule.Sources.Count == 1 ? rule.Sources[0] : null;
        var expression = string.IsNullOrWhiteSpace(source?.Expression) ? null : source.Expression;

        if (target == null)
            return MatchValueResolution.Failed("The rule has no Metaverse attribute to compare with.", AttributeDataType.NotSet, expression);

        if (!ObjectMatchingRule.IsMatchableType(target.Type))
            return MatchValueResolution.Failed(
                $"The Metaverse attribute '{target.Name}' is {target.Type}, which Object Matching cannot compare. " +
                $"Match on a {ObjectMatchingRule.MatchableTypesDescription} attribute.",
                target.Type, expression);

        if (source == null)
            return MatchValueResolution.Failed(
                $"An Object Matching Rule needs exactly one source; this one has {rule.Sources.Count}.", target.Type, expression);

        return expression != null
            ? ResolveExpression(expression, target, attributeValues, objectType)
            : ResolveAttribute(source, target, attributeValues, objectType);
    }

    private static MatchValueResolution ResolveAttribute(
        ObjectMatchingRuleSource source,
        MetaverseAttribute target,
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType)
    {
        var attributeId = source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId;
        var attribute = source.ConnectedSystemAttribute
                        ?? objectType?.Attributes.FirstOrDefault(a => a.Id == attributeId);

        if (attributeId == null || attribute == null)
            return MatchValueResolution.Failed("The rule's Connected System attribute is not set.", target.Type, null);

        if (attribute.Type != target.Type)
            return MatchValueResolution.Failed(
                $"The Connected System attribute '{attribute.Name}' is {attribute.Type} but the Metaverse attribute '{target.Name}' " +
                $"is {target.Type}; a rule compares attributes of the same type.",
                target.Type, null);

        var values = new List<object>();
        foreach (var value in attributeValues
                     .Where(av => av.AttributeId == attributeId)
                     .Select(av => SyncEngine.ToExpressionValue(av, attribute.Type))
                     .Where(value => value != null && value is not "" && !values.Contains(value)))
        {
            values.Add(value!);
        }

        return values.Count == 0
            ? MatchValueResolution.NoValue(target.Type, null)
            : MatchValueResolution.WithValues(values, target.Type, null);
    }

    private MatchValueResolution ResolveExpression(
        string expression,
        MetaverseAttribute target,
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType)
    {
        var context = new ExpressionContext(connectedSystemAttributes: BuildInputs(attributeValues, objectType));

        // Only the evaluation is guarded, matching Attribute Flow (SyncEngine.EvaluateExpressionSource): every failure
        // an expression can cause arrives as the one ExpressionEvaluationException (#2153, N5). Anything else is a JIM
        // fault and propagates.
        object? result;
        try
        {
            result = _expressionEvaluator.Evaluate(expression, context);
        }
        catch (ExpressionEvaluationException ex)
        {
            return MatchValueResolution.Failed($"The expression could not be evaluated: {ex.Message}", target.Type, expression);
        }

        // Every value of the result is a candidate (#2179): one value, or each element of a collection of any type,
        // each converted to the Metaverse attribute's type. One that cannot be converted fails the resolution, since
        // matching on part of what the rule computes would be an arbitrary choice.
        var values = new List<object>();
        foreach (var item in ExpressionResultValues.Elements(result).Where(item => item != null && !(item is string text && string.IsNullOrWhiteSpace(text))))
        {
            if (!ExpressionResultConverter.TryConvert(item, target.Type, out var value) || value == null)
                return MatchValueResolution.Failed(
                    $"The expression's result '{ExpressionText.Describe(item!)}' cannot be converted to {target.Type}, the type of the Metaverse attribute '{target.Name}'.",
                    target.Type, expression);

            if (!values.Any(held => ExpressionResultValues.Key(held).Equals(ExpressionResultValues.Key(value))))
                values.Add(value);
        }

        return values.Count == 0
            ? MatchValueResolution.NoValue(target.Type, expression)
            : MatchValueResolution.WithValues(values, target.Type, expression);
    }

    /// <summary>
    /// The <c>cs["..."]</c> values an expression reads, named and typed as Attribute Flow names and types them
    /// (<see cref="SyncEngine.BuildCsoAttributeDictionary"/>). Built from each value's own attribute definition, which
    /// every loader includes, so it works where the object's type is not loaded (the previews).
    /// </summary>
    private static Dictionary<string, object?> BuildInputs(
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType)
    {
        var inputs = new ExpressionAttributeDictionary();

        foreach (var attributeValue in attributeValues)
        {
            var attribute = (ConnectedSystemObjectTypeAttribute?)attributeValue.Attribute
                            ?? objectType?.Attributes.FirstOrDefault(a => a.Id == attributeValue.AttributeId);
            if (attribute == null)
            {
                Log.Warning("MatchValueResolver: attribute value with AttributeId={AttributeId} has no attribute definition, so a matching expression cannot read it",
                    attributeValue.AttributeId);
                continue;
            }

            inputs.Add(attribute.Name, SyncEngine.ToExpressionValue(attributeValue, attribute.Type));
        }

        return inputs.Build();
    }
}
