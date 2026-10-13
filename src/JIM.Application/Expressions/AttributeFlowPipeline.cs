// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Expressions;

/// <summary>
/// The one evaluation pipeline for an Expression-based Attribute Flow (#2055), run by synchronisation and by the
/// tester alike, so a test cannot pass what synchronisation fails or fail what it passes. In synchronisation's order:
/// <list type="number">
/// <item>the context, built from the object as synchronisation builds it;</item>
/// <item>Missing Input Behaviour (#1361), before anything is evaluated;</item>
/// <item>the Expression itself;</item>
/// <item>value processing (#843, import to a Text attribute only) and conversion to the target's type (#2179).</item>
/// </list>
/// The outcome is the final values, no value, or an error.
/// </summary>
/// <remarks>
/// Pure: nothing here changes an object, records an error or throws for an expression's fault. Synchronisation turns
/// the outcome into pending value changes, an Attribute Flow error or a failed object; the tester shows it.
/// </remarks>
public static class AttributeFlowPipeline
{
    /// <summary>
    /// The first half of the pipeline: Missing Input Behaviour, then the Expression. A Generated Value's base
    /// expression stops here.
    /// </summary>
    /// <param name="connectedSystemAttributes">The <c>cs[...]</c> view, or null where the Expression has none (export).</param>
    /// <param name="metaverseAttributes">The <c>mv[...]</c> view, or null where the Expression has none (the ordinary
    /// inbound pass). Absent inputs are counted only on the sides that have a view: an Expression with no Metaverse view
    /// reads nothing from <c>mv[...]</c>, and counting it as missing would fail every object.</param>
    public static ExpressionSourceEvaluation EvaluateSource(
        IExpressionEvaluator expressionEvaluator,
        string expression,
        MissingInputBehaviour missingInputBehaviour,
        IDictionary<string, object?>? connectedSystemAttributes,
        IDictionary<string, object?>? metaverseAttributes)
    {
        ArgumentNullException.ThrowIfNull(expressionEvaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        if (missingInputBehaviour != MissingInputBehaviour.EvaluateAnyway)
        {
            var missingInputs = FindMissingInputs(expression, connectedSystemAttributes, metaverseAttributes);
            if (missingInputs.Count > 0)
            {
                return missingInputBehaviour switch
                {
                    MissingInputBehaviour.ContributeNoValue => ExpressionSourceEvaluation.MissingInput(ExpressionSourceOutcome.NoValue, missingInputs),
                    MissingInputBehaviour.FailMapping => ExpressionSourceEvaluation.MissingInput(ExpressionSourceOutcome.MissingInputFailedMapping, missingInputs),
                    MissingInputBehaviour.FailObject => ExpressionSourceEvaluation.MissingInput(ExpressionSourceOutcome.MissingInputFailedObject, missingInputs),
                    _ => throw new ArgumentOutOfRangeException(nameof(missingInputBehaviour), missingInputBehaviour, null)
                };
            }
        }

        // Only the evaluation itself is guarded. Every failure an expression can cause arrives as the one
        // ExpressionEvaluationException (#2153, N5); anything else is a JIM fault and propagates.
        object? result;
        try
        {
            result = expressionEvaluator.Evaluate(expression, new ExpressionContext(metaverseAttributes, connectedSystemAttributes));
        }
        catch (ExpressionEvaluationException ex)
        {
            return ExpressionSourceEvaluation.Failed(ex);
        }

        // A null result is a positive "no value" assertion, not "no opinion".
        return result == null ? ExpressionSourceEvaluation.ReturnedNull() : ExpressionSourceEvaluation.Returned(result);
    }

    /// <summary>
    /// The second half of the pipeline: the Expression's result resolved against the target attribute, through value
    /// processing and conversion to its type.
    /// </summary>
    /// <param name="processText">For a Text target, the mapping's text processing applied to each value; a value it
    /// collapses to null is dropped. Null applies none.</param>
    public static AttributeFlowEvaluation Resolve(
        ExpressionSourceEvaluation source,
        AttributeDataType targetType,
        AttributePlurality targetPlurality,
        Func<string, string?>? processText)
    {
        ArgumentNullException.ThrowIfNull(source);

        switch (source.Outcome)
        {
            case ExpressionSourceOutcome.NoValue:
                return new AttributeFlowEvaluation { Outcome = AttributeFlowOutcome.NoValue, Source = source, TargetType = targetType };
            case ExpressionSourceOutcome.MissingInputFailedMapping:
                return Error(AttributeFlowErrorReason.MissingInputFailedMapping);
            case ExpressionSourceOutcome.MissingInputFailedObject:
                return Error(AttributeFlowErrorReason.MissingInputFailedObject);
            case ExpressionSourceOutcome.EvaluationFailed:
                return Error(AttributeFlowErrorReason.EvaluationFailed);
        }

        var resolution = ExpressionResultValues.Resolve(source.Result, targetType, targetPlurality, processText);
        return resolution.Kind switch
        {
            ExpressionResultResolutionKind.Values => Resolved(AttributeFlowOutcome.Values, null),
            ExpressionResultResolutionKind.NoValue => Resolved(AttributeFlowOutcome.NoValue, null),
            ExpressionResultResolutionKind.NotConvertible => Resolved(AttributeFlowOutcome.Error, AttributeFlowErrorReason.ResultNotConvertible),
            ExpressionResultResolutionKind.SeveralValues => Resolved(AttributeFlowOutcome.Error, AttributeFlowErrorReason.ResultMultiValued),
            _ => throw new InvalidOperationException($"Unknown expression result resolution '{resolution.Kind}'.")
        };

        AttributeFlowEvaluation Error(AttributeFlowErrorReason reason) => new()
        {
            Outcome = AttributeFlowOutcome.Error, ErrorReason = reason, Source = source, TargetType = targetType
        };

        AttributeFlowEvaluation Resolved(AttributeFlowOutcome outcome, AttributeFlowErrorReason? reason) => new()
        {
            Outcome = outcome, ErrorReason = reason, Source = source, Resolution = resolution, TargetType = targetType
        };
    }

    /// <summary>
    /// The whole pipeline for an import Attribute Flow: the <c>cs[...]</c> view built from
    /// <paramref name="connectedSystemObject"/> exactly as synchronisation builds it, then the mapping's Missing Input
    /// Behaviour, the Expression, its value processing and conversion to the target Metaverse attribute's type.
    /// </summary>
    /// <param name="metaverseAttributes">The Metaverse Object's effective values, for a Metaverse-Derived Attribute
    /// Flow (#1750) only; null for the ordinary inbound pass.</param>
    public static AttributeFlowEvaluation EvaluateInbound(
        ConnectedSystemObject connectedSystemObject,
        ConnectedSystemObjectType connectedSystemObjectType,
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        IExpressionEvaluator expressionEvaluator,
        IDictionary<string, object?>? metaverseAttributes)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(source);

        var target = mapping.TargetMetaverseAttribute
            ?? throw new ArgumentException("An import Attribute Flow has a target Metaverse attribute.", nameof(mapping));
        var connectedSystemAttributes = SyncEngine.BuildCsoAttributeDictionary(connectedSystemObject, connectedSystemObjectType);

        var evaluated = EvaluateSource(expressionEvaluator, source.Expression!, source.MissingInputBehaviour, connectedSystemAttributes, metaverseAttributes);
        return Resolve(evaluated, target.Type, target.AttributePlurality, InboundTextProcessing(mapping, target));
    }

    /// <summary>
    /// The whole pipeline for an export Attribute Flow, against the Metaverse Object's <c>mv[...]</c> view: the
    /// mapping's Missing Input Behaviour, the Expression and conversion to the target Connected System attribute's
    /// type. A Reference target takes the reference's text (for example a DN). Export applies no value processing.
    /// </summary>
    public static AttributeFlowEvaluation EvaluateExport(
        IDictionary<string, object?> metaverseAttributes,
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        IExpressionEvaluator expressionEvaluator)
    {
        ArgumentNullException.ThrowIfNull(metaverseAttributes);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(source);

        var target = mapping.TargetConnectedSystemAttribute
            ?? throw new ArgumentException("An export Attribute Flow has a target Connected System attribute.", nameof(mapping));

        var evaluated = EvaluateSource(expressionEvaluator, source.Expression!, source.MissingInputBehaviour, connectedSystemAttributes: null, metaverseAttributes);
        return Resolve(evaluated, ExportValueType(target.Type), target.AttributePlurality, processText: null);
    }

    /// <summary>
    /// The type an export Expression's values are converted to for a target of <paramref name="targetType"/>: a
    /// Reference takes the reference's text, staged as text exactly as it always has been; every other type its own.
    /// </summary>
    public static AttributeDataType ExportValueType(AttributeDataType targetType) =>
        targetType == AttributeDataType.Reference ? AttributeDataType.Text : targetType;

    /// <summary>
    /// The mapping's inbound text processing (#843) for a Text target, or null for any other type.
    /// </summary>
    public static Func<string, string?>? InboundTextProcessing(SyncRuleMapping mapping, MetaverseAttribute target) =>
        target.Type == AttributeDataType.Text
            ? value => SyncEngine.ApplyInboundTextProcessing(value, mapping.InboundValueProcessing, mapping.CaseNormalisation)
            : null;

    private static IReadOnlyList<string> FindMissingInputs(
        string expression,
        IDictionary<string, object?>? connectedSystemAttributes,
        IDictionary<string, object?>? metaverseAttributes)
    {
        var missing = new List<string>();
        if (connectedSystemAttributes != null)
            missing.AddRange(ExpressionInputResolver.FindMissingInputs(expression, ExpressionInputSource.ConnectedSystem, connectedSystemAttributes));
        if (metaverseAttributes != null)
            missing.AddRange(ExpressionInputResolver.FindMissingInputs(expression, ExpressionInputSource.Metaverse, metaverseAttributes));
        return missing;
    }
}
