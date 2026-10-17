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
        var anyHeldUnderAnotherType = false;
        foreach (var attributeValue in attributeValues.Where(av => av.AttributeId == attributeId))
        {
            var value = SyncEngine.ToExpressionValue(attributeValue, attribute.Type);
            if (value == null)
                anyHeldUnderAnotherType |= attributeValue.IsHeldUnderAnotherDataType(attribute.Type);
            else if (value is not "" && !values.Contains(value))
                values.Add(value);
        }

        if (values.Count > 0)
            return MatchValueResolution.WithValues(values, target.Type, null);

        // Nothing of the attribute's type. A value held under another one (#2319) is not no value: matching on it as
        // no value would find nothing and project a duplicate of the identity the object already has.
        return anyHeldUnderAnotherType &&
               SyncEngine.HeldOnlyUnderAnotherDataType(attributeValues, attributeId.Value, attribute.Type) is { } heldType
            ? MatchValueResolution.Failed(DescribeHeldUnderAnotherDataType($"The Connected System attribute '{attribute.Name}'", heldType, attribute.Type),
                target.Type, null)
            : MatchValueResolution.NoValue(target.Type, null);
    }

    /// <summary>
    /// Why a rule reading a value held only under another data type cannot be worked out (#2319), and what resolves it.
    /// </summary>
    private static string DescribeHeldUnderAnotherDataType(string subject, AttributeDataType heldType, AttributeDataType? currentType)
    {
        var current = currentType.HasValue ? $", not as its {currentType.Value} type" : string.Empty;
        return $"{subject} is held as {heldType}{current}: a schema refresh changed its data type and no import has read it since. " +
               "Run a Full Import to read it as its new type; the object is matched on the next synchronisation.";
    }

    private MatchValueResolution ResolveExpression(
        string expression,
        MetaverseAttribute target,
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType)
    {
        var inputs = BuildInputs(attributeValues, objectType, out var heldUnderAnotherDataType);

        // An input held only under another data type (#2319) reads as no value, and the expression would compute nothing,
        // or a wrong value, from it. Only an input the expression reads counts.
        if (heldUnderAnotherDataType != null)
        {
            var heldInputs = ExpressionInputResolver.FindHeldInputs(expression, heldUnderAnotherDataType);
            if (heldInputs.Count > 0)
            {
                var subject = heldInputs.Count == 1 ? $"The input {heldInputs[0].Accessor}" : $"The inputs {string.Join(", ", heldInputs)}";
                return MatchValueResolution.Failed(
                    DescribeHeldUnderAnotherDataType(subject, heldInputs[0].HeldDataType, currentType: null), target.Type, expression);
            }
        }

        var context = new ExpressionContext(connectedSystemAttributes: inputs);

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
    internal static Dictionary<string, object?> BuildInputs(
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType) =>
        BuildInputs(attributeValues, objectType, out _);

    /// <summary>
    /// As <see cref="BuildInputs(IReadOnlyCollection{ConnectedSystemObjectAttributeValue}, ConnectedSystemObjectType?)"/>,
    /// also returning the attributes held only under another data type (#2319), exactly as
    /// <see cref="SyncEngine.BuildCsoAttributeDictionary(ConnectedSystemObject, ConnectedSystemObjectType, out IReadOnlyDictionary{string, AttributeDataType}?)"/>
    /// does for Attribute Flow; null when there are none, the usual case.
    /// </summary>
    internal static Dictionary<string, object?> BuildInputs(
        IReadOnlyCollection<ConnectedSystemObjectAttributeValue> attributeValues,
        ConnectedSystemObjectType? objectType,
        out IReadOnlyDictionary<string, AttributeDataType>? heldUnderAnotherDataType)
    {
        var inputs = new ExpressionAttributeDictionary();
        List<ConnectedSystemObjectTypeAttribute>? heldCandidates = null;

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

            var value = SyncEngine.ToExpressionValue(attributeValue, attribute.Type);
            if (value == null && attributeValue.IsHeldUnderAnotherDataType(attribute.Type))
                (heldCandidates ??= []).Add(attribute);

            inputs.Add(attribute.Name, value);
        }

        heldUnderAnotherDataType = SyncEngine.FindAttributesHeldOnlyUnderAnotherDataType(attributeValues, heldCandidates);
        return inputs.Build();
    }
}
