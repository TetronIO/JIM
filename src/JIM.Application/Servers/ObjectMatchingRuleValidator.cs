// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers;

/// <summary>
/// Decides whether what an Object Matching Rule compares can ever be worked out, before the rule is saved (#2052).
/// </summary>
/// <remarks>
/// <see cref="MatchValueResolver"/> fails a rule of the wrong shape or types for every object it meets, which stops
/// each of them with an error rather than letting it project or provision a duplicate. That is the right outcome at
/// synchronisation time and the wrong place to learn about it, so every surface that saves a rule (the portal, the
/// REST API and PowerShell, through <c>ConnectedSystemServer</c>) refuses it here first, with a reason. The two must
/// agree: anything refused here would fail there, and anything this passes is either resolvable or fails only on a
/// particular object's data (a value that will not convert, an expression that throws on one input).
/// </remarks>
public sealed class ObjectMatchingRuleValidator
{
    private readonly IExpressionEvaluator _expressionEvaluator;

    public ObjectMatchingRuleValidator(IExpressionEvaluator expressionEvaluator)
    {
        _expressionEvaluator = expressionEvaluator;
    }

    /// <summary>
    /// Why <paramref name="rule"/> could never compare anything for objects of <paramref name="objectType"/>, or null
    /// when it can.
    /// </summary>
    /// <param name="rule">The rule; its owner is not checked here (see <see cref="ObjectMatchingRule.DescribeInvalidity"/>).</param>
    /// <param name="objectType">The Connected System Object Type the rule reads, with its attributes loaded.</param>
    /// <param name="target">The Metaverse attribute the rule compares with.</param>
    public string? DescribeProblem(ObjectMatchingRule rule, ConnectedSystemObjectType objectType, MetaverseAttribute target)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(target);

        var shape = rule.DescribeComparisonInvalidity();
        if (shape != null)
            return shape;

        if (!ObjectMatchingRule.IsMatchableType(target.Type))
            return $"The Metaverse Attribute '{target.Name}' is {target.Type}, which an Object Matching Rule cannot compare. " +
                   $"Match on a {ObjectMatchingRule.MatchableTypesDescription} attribute.";

        var source = rule.Sources[0];
        return string.IsNullOrWhiteSpace(source.Expression)
            ? DescribeAttributeProblem(source, objectType, target)
            : DescribeExpressionProblem(source.Expression, objectType);
    }

    private static string? DescribeAttributeProblem(
        ObjectMatchingRuleSource source,
        ConnectedSystemObjectType objectType,
        MetaverseAttribute target)
    {
        var attributeId = source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId;
        var attribute = objectType.Attributes.FirstOrDefault(a => a.Id == attributeId);

        if (attribute == null)
        {
            var named = source.ConnectedSystemAttribute != null
                ? $"'{source.ConnectedSystemAttribute.Name}'"
                : $"with id {attributeId}";
            return $"The Connected System attribute {named} is not an attribute of '{objectType.Name}', the Object Type this rule reads.";
        }

        if (attribute.Type != target.Type)
            return $"The Connected System attribute '{attribute.Name}' is {attribute.Type} but the Metaverse Attribute '{target.Name}' is " +
                   $"{target.Type}; an Object Matching Rule compares attributes of the same type. To compare them anyway, match on an " +
                   "expression that converts the value.";

        return null;
    }

    /// <summary>
    /// Why <paramref name="expression"/> could never work as a matching expression over objects of
    /// <paramref name="objectType"/>, or null when it can.
    /// </summary>
    public string? DescribeExpressionProblem(string expression, ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(objectType);

        if (expression.Length > DynamicExpressoEvaluator.MaxExpressionLength)
            return $"The expression is {expression.Length:N0} characters long; an expression can be at most " +
                   $"{DynamicExpressoEvaluator.MaxExpressionLength:N0}.";

        var parsed = _expressionEvaluator.Validate(expression);
        if (!parsed.IsValid)
            return $"The expression does not parse: {parsed.ErrorMessage}";

        var inputs = ExpressionInputResolver.Resolve(expression);

        // The Metaverse Object is what the rule is looking for, so there is nothing on that side to read yet. Asking
        // for the Metaverse side to be normalised too is a reasonable thing to want; a derived Metaverse attribute
        // (#1750) is how to have it.
        var metaverseInput = inputs.FirstOrDefault(input => input.Source == ExpressionInputSource.Metaverse);
        if (metaverseInput != null)
            return $"A matching expression reads only the Connected System Object, so it cannot read {metaverseInput.Accessor}: " +
                   "the Metaverse Object is what the rule is looking for. To compare with a normalised Metaverse value, derive " +
                   "a Metaverse attribute that holds it and match on that.";

        var connectedSystemInputs = inputs.Where(input => input.Source == ExpressionInputSource.ConnectedSystem).ToList();
        if (connectedSystemInputs.Count == 0)
            return "The expression reads no Connected System attribute, so it would compute the same value for every object and " +
                   "match them all with the same identity. Read at least one attribute, as cs[\"attributeName\"].";

        // Inputs are looked up case-insensitively when the expression runs (MatchValueResolver), so they are here too.
        var unknown = connectedSystemInputs.FirstOrDefault(input =>
            !objectType.Attributes.Any(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase)));
        if (unknown != null)
            return $"The expression reads {unknown.Accessor}, but '{objectType.Name}' has no attribute of that name.";

        var nonRepeatable = NonRepeatableFunctionDetector.Find(expression);
        if (nonRepeatable.Count > 0)
            return $"The expression calls {string.Join(", ", nonRepeatable)}, which returns a different value each time it runs, so " +
                   "the same object would match a different identity, or none, on every synchronisation.";

        return null;
    }
}
