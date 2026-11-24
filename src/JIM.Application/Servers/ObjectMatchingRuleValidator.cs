// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Localisation;
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
    public string? DescribeProblem(ObjectMatchingRule rule, ConnectedSystemObjectType objectType, MetaverseAttribute target) =>
        DescribeProblemDetail(rule, objectType, target)?.ToEnglish();

    /// <summary>
    /// <see cref="DescribeProblem"/> as a message, so the portal can show it in the reader's language.
    /// </summary>
    public JimMessage? DescribeProblemDetail(ObjectMatchingRule rule, ConnectedSystemObjectType objectType, MetaverseAttribute target)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(target);

        var shape = rule.FindComparisonInvalidity() switch
        {
            ObjectMatchingRuleShapeProblem.NoSource => ServerMessage.Create(nameof(ServerMessages.MatchNoSource)),
            ObjectMatchingRuleShapeProblem.SeveralSources => ServerMessage.Create(nameof(ServerMessages.MatchSeveralSources), rule.Sources.Count.ToString()),
            ObjectMatchingRuleShapeProblem.AttributeAndExpression => ServerMessage.Create(nameof(ServerMessages.MatchAttributeAndExpression)),
            ObjectMatchingRuleShapeProblem.NeitherAttributeNorExpression => ServerMessage.Create(nameof(ServerMessages.MatchNeitherAttributeNorExpression)),
            ObjectMatchingRuleShapeProblem.NoTarget => ServerMessage.Create(nameof(ServerMessages.MatchNoTarget)),
            _ => null
        };
        if (shape != null)
            return shape;

        if (!ObjectMatchingRule.IsMatchableType(target.Type))
            return ServerMessage.Create(nameof(ServerMessages.MatchTargetTypeNotMatchable), target.Name, target.Type.ToString(),
                ObjectMatchingRule.MatchableTypesDescription);

        var source = rule.Sources[0];
        return string.IsNullOrWhiteSpace(source.Expression)
            ? DescribeAttributeProblem(source, objectType, target)
            : DescribeExpressionProblemDetail(source.Expression, objectType);
    }

    private static JimMessage? DescribeAttributeProblem(
        ObjectMatchingRuleSource source,
        ConnectedSystemObjectType objectType,
        MetaverseAttribute target)
    {
        var attributeId = source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId;
        var attribute = objectType.Attributes.FirstOrDefault(a => a.Id == attributeId);

        if (attribute == null)
        {
            return source.ConnectedSystemAttribute != null
                ? ServerMessage.Create(nameof(ServerMessages.MatchAttributeNamedNotOnType), source.ConnectedSystemAttribute.Name, objectType.Name)
                : ServerMessage.Create(nameof(ServerMessages.MatchAttributeIdNotOnType), attributeId.ToString(), objectType.Name);
        }

        if (attribute.Type != target.Type)
            return ServerMessage.Create(nameof(ServerMessages.MatchAttributeTypeMismatch), attribute.Name, attribute.Type.ToString(),
                target.Name, target.Type.ToString());

        return null;
    }

    /// <summary>
    /// Why <paramref name="expression"/> could never work as a matching expression over objects of
    /// <paramref name="objectType"/>, or null when it can.
    /// </summary>
    public string? DescribeExpressionProblem(string expression, ConnectedSystemObjectType objectType) =>
        DescribeExpressionProblemDetail(expression, objectType)?.ToEnglish();

    /// <summary>
    /// <see cref="DescribeExpressionProblem"/> as a message, so the portal can show it in the reader's language.
    /// </summary>
    public JimMessage? DescribeExpressionProblemDetail(string expression, ConnectedSystemObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(objectType);

        if (expression.Length > DynamicExpressoEvaluator.MaxExpressionLength)
            return ServerMessage.Create(nameof(ServerMessages.MatchExpressionTooLong), expression.Length,
                DynamicExpressoEvaluator.MaxExpressionLength);

        var parsed = _expressionEvaluator.Validate(expression);
        if (!parsed.IsValid)
            return ServerMessage.Create(nameof(ServerMessages.MatchExpressionDoesNotParse), parsed.ErrorMessage ?? string.Empty);

        var inputs = ExpressionInputResolver.Resolve(expression);

        // The Metaverse Object is what the rule is looking for, so there is nothing on that side to read yet. Asking
        // for the Metaverse side to be normalised too is a reasonable thing to want; a derived Metaverse attribute
        // (#1750) is how to have it.
        var metaverseInput = inputs.FirstOrDefault(input => input.Source == ExpressionInputSource.Metaverse);
        if (metaverseInput != null)
            return ServerMessage.Create(nameof(ServerMessages.MatchExpressionReadsMetaverse), metaverseInput.Accessor);

        var connectedSystemInputs = inputs.Where(input => input.Source == ExpressionInputSource.ConnectedSystem).ToList();
        if (connectedSystemInputs.Count == 0)
            return ServerMessage.Create(nameof(ServerMessages.MatchExpressionReadsNothing));

        // Inputs are looked up case-insensitively when the expression runs (MatchValueResolver), so they are here too.
        var unknown = connectedSystemInputs.FirstOrDefault(input =>
            !objectType.Attributes.Any(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase)));
        if (unknown != null)
            return ServerMessage.Create(nameof(ServerMessages.MatchExpressionUnknownAttribute), unknown.Accessor, objectType.Name);

        var nonRepeatable = NonRepeatableFunctionDetector.Find(expression);
        if (nonRepeatable.Count > 0)
            return ServerMessage.Create(nameof(ServerMessages.MatchExpressionNonRepeatable), string.Join(", ", nonRepeatable));

        return null;
    }
}
