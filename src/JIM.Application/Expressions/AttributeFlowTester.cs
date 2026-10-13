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
/// The Attribute Flow tester (#2055): what synchronisation would do with an Expression-based Attribute Flow, given
/// sample values for the attributes it reads. The portal tester, the REST API's <c>test-expression</c> and
/// <c>Test-JIMExpression</c> all call it.
/// </summary>
/// <remarks>
/// Each sample is converted to its schema attribute's type and held as an attribute value on a transient Connected
/// System Object (import) or Metaverse Object (export, and an import flow that reads <c>mv[...]</c>). The context is
/// then built from that object by the same code synchronisation uses, and <see cref="AttributeFlowPipeline"/> runs
/// over it. So a date reaches the Expression as a date, a multi-valued attribute reads exactly as synchronisation
/// reads it, and a result the target cannot hold fails here as it fails there.
/// </remarks>
public static class AttributeFlowTester
{
    /// <summary>
    /// Tests <paramref name="source"/>'s Expression as it would run on <paramref name="mapping"/>, an Attribute Flow of
    /// <paramref name="syncRule"/>, against the sample <paramref name="inputs"/>. An input not supplied has no value.
    /// </summary>
    /// <param name="syncRule">The Synchronisation Rule, with its object types' attributes, which type the samples.</param>
    /// <param name="mapping">The Attribute Flow: its target attribute and value processing.</param>
    /// <param name="source">The source being tested: its Expression and Missing Input Behaviour. It need not be saved,
    /// so an edit can be tried before saving it.</param>
    public static AttributeFlowTestResult Test(
        SyncRule syncRule,
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        IReadOnlyList<AttributeFlowTestInput> inputs,
        IExpressionEvaluator expressionEvaluator)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(expressionEvaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Expression);

        var errors = new List<string>();
        var exporting = syncRule.Direction == SyncRuleDirection.Export;
        var connectedSystemValues = new List<ConnectedSystemObjectAttributeValue>();
        var metaverseValues = new List<MetaverseObjectAttributeValue>();

        foreach (var input in inputs.Where(input => input.Values.Count > 0))
        {
            if (input.Source == ExpressionInputSource.ConnectedSystem)
            {
                if (exporting)
                {
                    errors.Add($"{input.Accessor} has a value, but an export Attribute Flow reads only the Metaverse Object, so the Expression would never see it.");
                    continue;
                }

                var attribute = syncRule.ConnectedSystemObjectType?.Attributes
                    .FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase));
                if (attribute == null)
                {
                    errors.Add(NotInSchema(input, syncRule.ConnectedSystemObjectType?.Name));
                    continue;
                }

                foreach (var typed in TypedValues(input, attribute.Type, attribute.AttributePlurality, errors))
                    connectedSystemValues.Add(ToConnectedSystemValue(attribute, typed));
            }
            else
            {
                var attribute = syncRule.MetaverseObjectType?.Attributes
                    .FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase));
                if (attribute == null)
                {
                    errors.Add(NotInSchema(input, syncRule.MetaverseObjectType?.Name));
                    continue;
                }

                foreach (var typed in TypedValues(input, attribute.Type, attribute.AttributePlurality, errors))
                    metaverseValues.Add(ToMetaverseValue(attribute, typed));
            }
        }

        if (errors.Count > 0)
            return new AttributeFlowTestResult { InputErrors = errors };

        var metaverseObject = new MetaverseObject { Type = syncRule.MetaverseObjectType ?? new MetaverseObjectType() };
        foreach (var value in metaverseValues)
        {
            value.MetaverseObject = metaverseObject;
            metaverseObject.AttributeValues.Add(value);
        }

        if (exporting)
        {
            // Export evaluates against the Metaverse Object's persisted values.
            var exportView = SyncEngine.BuildAttributeDictionary(metaverseObject);
            return new AttributeFlowTestResult { Evaluation = AttributeFlowPipeline.EvaluateExport(exportView, mapping, source, expressionEvaluator) };
        }

        var connectedSystemObjectType = syncRule.ConnectedSystemObjectType ?? new ConnectedSystemObjectType();
        var connectedSystemObject = new ConnectedSystemObject { TypeId = connectedSystemObjectType.Id, MetaverseObject = metaverseObject };
        foreach (var value in connectedSystemValues)
        {
            value.ConnectedSystemObject = connectedSystemObject;
            connectedSystemObject.AttributeValues.Add(value);
        }

        // An import flow that reads mv[...] is a Metaverse-Derived Attribute Flow (#1750), evaluated by the derived
        // pass against the Metaverse Object's effective values; every other import flow has no Metaverse view.
        var readsMetaverse = ExpressionInputResolver.Resolve(source.Expression)
            .Any(input => input.Source == ExpressionInputSource.Metaverse);
        var metaverseView = readsMetaverse ? SyncEngine.BuildEffectiveAttributeDictionary(metaverseObject) : null;

        return new AttributeFlowTestResult
        {
            Evaluation = AttributeFlowPipeline.EvaluateInbound(connectedSystemObject, connectedSystemObjectType, mapping, source,
                expressionEvaluator, metaverseView)
        };
    }

    /// <summary>
    /// What synchronisation would do with <paramref name="evaluation"/>'s outcome, in one sentence an administrator
    /// reads beside the result. The portal tester and the REST API word it the same way.
    /// </summary>
    public static string Describe(AttributeFlowEvaluation evaluation, string targetAttributeName)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        var target = $"'{targetAttributeName}'";
        var missingInputs = string.Join(", ", evaluation.Source.MissingInputs);
        var keepsValue = $"synchronisation records an error and {target} keeps its current value";
        const string failsObject = "synchronisation records an error and nothing on the object is synchronised";

        return evaluation.Outcome switch
        {
            AttributeFlowOutcome.Values => evaluation.Values.Count == 1
                ? $"{target} would be set to this value."
                : $"{target} would hold these {evaluation.Values.Count} values.",

            AttributeFlowOutcome.NoValue when evaluation.Source.MissingInputs.Count > 0 =>
                $"Not evaluated: the object has no value for {missingInputs}, and Missing Input Behaviour is Contribute no value, " +
                $"so {target} gets no value, resolved by Attribute Priority and \"Null is a value\".",

            AttributeFlowOutcome.NoValue =>
                $"The Expression produced no value, so {target} gets no value, resolved by Attribute Priority and \"Null is a value\".",

            _ => evaluation.ErrorReason switch
            {
                AttributeFlowErrorReason.MissingInputFailedMapping =>
                    $"Not evaluated: the object has no value for {missingInputs}, and Missing Input Behaviour is Fail this mapping, so " +
                    $"{keepsValue}.",
                AttributeFlowErrorReason.MissingInputFailedObject =>
                    $"Not evaluated: the object has no value for {missingInputs}, and Missing Input Behaviour is Fail the object, so {failsObject}.",
                AttributeFlowErrorReason.EvaluationFailed =>
                    $"The Expression failed: {evaluation.Source.EvaluationException?.Message} So {failsObject}.",
                AttributeFlowErrorReason.ResultNotConvertible =>
                    $"The Expression returned '{evaluation.Resolution?.UnconvertibleValue}', which a {evaluation.TargetType} attribute cannot hold, so " +
                    $"{keepsValue}.",
                AttributeFlowErrorReason.ResultMultiValued when evaluation.Resolution?.SourceAttributeName is { } sourceAttribute =>
                    $"'{sourceAttribute}' has {evaluation.Resolution.ValueCount} values, but {target} is single-valued, so {keepsValue}. " +
                    "Use Where, Join, Count, Min or Max to say which value it should have.",
                AttributeFlowErrorReason.ResultMultiValued =>
                    $"The Expression returned {evaluation.Resolution?.ValueCount} values, but {target} is single-valued, so " +
                    $"{keepsValue}.",
                _ => throw new InvalidOperationException($"Unknown Attribute Flow error reason '{evaluation.ErrorReason}'.")
            }
        };
    }

    private static string NotInSchema(AttributeFlowTestInput input, string? objectTypeName) =>
        $"{input.Accessor} has a value, but {(objectTypeName == null ? "the object type" : $"'{objectTypeName}'")} has no attribute " +
        $"named '{input.AttributeName}', so the Expression would never see one.";

    /// <summary>
    /// The input's samples converted to the attribute's type, as an imported value of it would be held. A sample that
    /// cannot be converted, or several samples for a single-valued attribute, adds an error instead.
    /// </summary>
    private static List<object> TypedValues(AttributeFlowTestInput input, AttributeDataType type, AttributePlurality plurality, List<string> errors)
    {
        if (plurality == AttributePlurality.SingleValued && input.Values.Count > 1)
        {
            errors.Add($"{input.Accessor} has {input.Values.Count} values, but the attribute is single-valued, so an object holds at most one.");
            return [];
        }

        var typedValues = new List<object>();
        foreach (var sample in input.Values)
        {
            if (TryConvertSample(sample, type, out var typed))
            {
                typedValues.Add(typed);
                continue;
            }

            errors.Add($"The value '{ExpressionText.Describe(sample)}' for {input.Accessor} is not a {type} value, the type of that attribute." +
                type switch
                {
                    AttributeDataType.DateTime => " Enter a date in ISO 8601, such as 2026-03-15 or 2026-03-15T10:30:00Z.",
                    AttributeDataType.Reference => " Enter the id of the object it refers to.",
                    AttributeDataType.Binary => " Enter the value in base64.",
                    _ => string.Empty
                });
        }

        return typedValues;
    }

    private static bool TryConvertSample(object sample, AttributeDataType type, out object typed)
    {
        typed = null!;
        switch (type)
        {
            case AttributeDataType.Reference:
                // A reference reads as the id of the object it refers to.
                if (sample is Guid guid || Guid.TryParse(sample as string, out guid))
                {
                    typed = guid;
                    return true;
                }
                return false;

            case AttributeDataType.Binary when sample is string base64:
                var buffer = new byte[base64.Length];
                if (!Convert.TryFromBase64String(base64, buffer, out var written))
                    return false;
                typed = buffer[..written];
                return true;

            default:
                if (!ExpressionResultConverter.TryConvert(sample, type, out var converted) || converted == null)
                    return false;
                typed = converted;
                return true;
        }
    }

    private static ConnectedSystemObjectAttributeValue ToConnectedSystemValue(ConnectedSystemObjectTypeAttribute attribute, object value)
    {
        var attributeValue = new ConnectedSystemObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id };
        if (attribute.Type == AttributeDataType.Reference)
        {
            var id = (Guid)value;
            attributeValue.ReferenceValue = new ConnectedSystemObject { Id = id };
            attributeValue.ReferenceValueId = id;
            return attributeValue;
        }

        switch (value)
        {
            case string text: attributeValue.StringValue = text; break;
            case int number: attributeValue.IntValue = number; break;
            case long longNumber: attributeValue.LongValue = longNumber; break;
            case decimal decimalNumber: attributeValue.DecimalValue = decimalNumber; break;
            case DateTime date: attributeValue.DateTimeValue = date; break;
            case bool flag: attributeValue.BoolValue = flag; break;
            case Guid guid: attributeValue.GuidValue = guid; break;
            case byte[] bytes: attributeValue.ByteValue = bytes; break;
        }

        return attributeValue;
    }

    private static MetaverseObjectAttributeValue ToMetaverseValue(MetaverseAttribute attribute, object value)
    {
        var attributeValue = new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id };
        if (attribute.Type == AttributeDataType.Reference)
        {
            attributeValue.ReferenceValueId = (Guid)value;
            return attributeValue;
        }

        switch (value)
        {
            case string text: attributeValue.StringValue = text; break;
            case int number: attributeValue.IntValue = number; break;
            case long longNumber: attributeValue.LongValue = longNumber; break;
            case decimal decimalNumber: attributeValue.DecimalValue = decimalNumber; break;
            case DateTime date: attributeValue.DateTimeValue = date; break;
            case bool flag: attributeValue.BoolValue = flag; break;
            case Guid guid: attributeValue.GuidValue = guid; break;
            case byte[] bytes: attributeValue.ByteValue = bytes; break;
        }

        return attributeValue;
    }
}
