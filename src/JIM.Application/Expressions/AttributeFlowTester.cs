// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Localisation;
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
    /// <param name="asOfUtc">The instant to run the test as of: what <c>Now()</c> and <c>Today()</c> read. Null for
    /// the real time.</param>
    public static AttributeFlowTestResult Test(
        SyncRule syncRule,
        SyncRuleMapping mapping,
        SyncRuleMappingSource? source,
        IReadOnlyList<AttributeFlowTestInput> inputs,
        IExpressionEvaluator expressionEvaluator,
        DateTime? asOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(syncRule);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(expressionEvaluator);

        if (mapping.Generation != null)
            return TestGeneratedBase(syncRule, mapping, source, inputs, expressionEvaluator, asOfUtc);

        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Expression);

        var tested = TestOnce(syncRule, mapping, source, inputs, expressionEvaluator, asOfUtc);
        if (tested.Evaluation is not { } evaluation)
            return tested;

        // Each input that has a value, made absent in turn with the others as given: what the Missing Input Behaviour
        // would then do. Only inputs the Expression reads; an input it does not read changes nothing.
        var readAccessors = ExpressionInputResolver.Resolve(source.Expression).Select(input => input.Accessor).ToHashSet(StringComparer.Ordinal);
        var ifInputHasNoValue = inputs
            .Where(input => input.Values.Count > 0 && readAccessors.Contains(input.Accessor))
            .Select(absent => new AttributeFlowAbsentInputOutcome(absent.Accessor,
                TestOnce(syncRule, mapping, source,
                    inputs.Select(input => ReferenceEquals(input, absent) ? input with { Values = [] } : input).ToList(),
                    expressionEvaluator, asOfUtc).Evaluation!))
            .ToList();

        return new AttributeFlowTestResult
        {
            Evaluation = evaluation,
            ValueProcessing = DescribeValueProcessing(syncRule, mapping, evaluation),
            IfInputHasNoValue = ifInputHasNoValue
        };
    }

    /// <summary>
    /// The inputs <paramref name="expression"/> reads, each described from <paramref name="syncRule"/>'s object types:
    /// its type and plurality, or none where the object being synchronised cannot hold it.
    /// </summary>
    public static IReadOnlyList<AttributeFlowTestInputDescriptor> DescribeInputs(SyncRule syncRule, string? expression)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        var exporting = syncRule.Direction == SyncRuleDirection.Export;
        return ExpressionInputResolver.Resolve(expression)
            .Select(input =>
            {
                // An export Attribute Flow reads only the Metaverse Object, so a cs[...] input has no type there.
                var (type, plurality) = input.Source switch
                {
                    ExpressionInputSource.ConnectedSystem when exporting => default,
                    ExpressionInputSource.ConnectedSystem => Find(syncRule.ConnectedSystemObjectType?.Attributes
                        .Select(a => (a.Name, a.Type, a.AttributePlurality)), input.AttributeName),
                    _ => Find(syncRule.MetaverseObjectType?.Attributes
                        .Select(a => (a.Name, a.Type, a.AttributePlurality)), input.AttributeName)
                };
                return new AttributeFlowTestInputDescriptor(input.Source, input.AttributeName, type, plurality);
            })
            .ToList();

        static (AttributeDataType?, AttributePlurality?) Find(
            IEnumerable<(string Name, AttributeDataType Type, AttributePlurality Plurality)>? attributes, string name) =>
            attributes?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } found
                ? (found.Type, found.Plurality)
                : (null, null);
    }

    /// <summary>
    /// The values <paramref name="connectedSystemObject"/> holds for each <c>cs[...]</c> input
    /// <paramref name="expression"/> reads, every value of a multi-valued attribute included, so a real object can be
    /// tested and then edited. An attribute it holds no value for is an input with no value.
    /// </summary>
    public static IReadOnlyList<AttributeFlowTestInput> InputsFromConnectedSystemObject(
        ConnectedSystemObject connectedSystemObject,
        ConnectedSystemObjectType connectedSystemObjectType,
        string? expression)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemObject);
        ArgumentNullException.ThrowIfNull(connectedSystemObjectType);

        return ExpressionInputResolver.Resolve(expression)
            .Where(input => input.Source == ExpressionInputSource.ConnectedSystem)
            .Select(input =>
            {
                var attribute = connectedSystemObjectType.Attributes
                    .FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase));
                var values = attribute == null
                    ? []
                    : connectedSystemObject.AttributeValues
                        .Where(value => value.AttributeId == attribute.Id)
                        .Select(value => TestSampleValues.FromConnectedSystemValue(value, attribute.Type))
                        .Where(value => value != null)
                        .Select(value => value!)
                        .ToList();
                return new AttributeFlowTestInput(input.Source, input.AttributeName, values);
            })
            .ToList();
    }

    /// <summary>
    /// The values <paramref name="metaverseObject"/> holds for each <c>mv[...]</c> input <paramref name="expression"/>
    /// reads, every value of a multi-valued attribute included. An asserted-null marker carries no value, as
    /// synchronisation reads it.
    /// </summary>
    public static IReadOnlyList<AttributeFlowTestInput> InputsFromMetaverseObject(MetaverseObject metaverseObject, string? expression)
    {
        ArgumentNullException.ThrowIfNull(metaverseObject);

        return ExpressionInputResolver.Resolve(expression)
            .Where(input => input.Source == ExpressionInputSource.Metaverse)
            .Select(input => new AttributeFlowTestInput(input.Source, input.AttributeName, metaverseObject.AttributeValues
                .Where(value => !value.NullValue && value.Attribute != null
                    && string.Equals(value.Attribute.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase))
                .Select(value => TestSampleValues.FromMetaverseValue(value, value.Attribute!.Type))
                .Where(value => value != null)
                .Select(value => value!)
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// The value processing step of the trace: the transforms an import Attribute Flow to a Text attribute applies, in
    /// synchronisation's order, and whether they changed the Expression's result. Null where none can apply.
    /// </summary>
    private static AttributeFlowValueProcessing? DescribeValueProcessing(SyncRule syncRule, SyncRuleMapping mapping, AttributeFlowEvaluation evaluation)
    {
        if (syncRule.Direction == SyncRuleDirection.Export
            || mapping.TargetMetaverseAttribute is not { Type: AttributeDataType.Text } target
            || evaluation.Source.Outcome != ExpressionSourceOutcome.Result)
            return null;

        // The same transform the pipeline applies, over the same text forms it converts to.
        var processText = AttributeFlowPipeline.InboundTextProcessing(mapping, target)!;
        var changed = ExpressionResultValues.Elements(evaluation.Source.Result)
            .Select(element => ExpressionText.TryFormat(element, out var text) ? text : null)
            .Any(text => text != null && !string.Equals(processText(text), text, StringComparison.Ordinal));

        return new AttributeFlowValueProcessing { Steps = ValueProcessingSteps(mapping), Changed = changed };
    }

    /// <summary>
    /// The value processing step for a Generated Value's base: the same transforms, applied to the one base value on
    /// import. Null on export, which applies none, and where there was no base value to process.
    /// </summary>
    private static AttributeFlowValueProcessing? DescribeValueProcessing(SyncRule syncRule, SyncRuleMapping mapping, GeneratedBaseEvaluation generated)
    {
        if (syncRule.Direction == SyncRuleDirection.Export || generated.UnprocessedBaseValue == null)
            return null;

        return new AttributeFlowValueProcessing
        {
            Steps = ValueProcessingSteps(mapping),
            Changed = !string.Equals(generated.UnprocessedBaseValue, generated.BaseValue, StringComparison.Ordinal)
        };
    }

    private static List<AttributeFlowValueProcessingStep> ValueProcessingSteps(SyncRuleMapping mapping)
    {
        var steps = new List<AttributeFlowValueProcessingStep>();
        if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.TrimWhitespace))
            steps.Add(AttributeFlowValueProcessingStep.TrimWhitespace);
        if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.CollapseInternalWhitespace))
            steps.Add(AttributeFlowValueProcessingStep.CollapseInternalWhitespace);
        switch (mapping.CaseNormalisation)
        {
            case InboundCaseNormalisation.Upper: steps.Add(AttributeFlowValueProcessingStep.UpperCase); break;
            case InboundCaseNormalisation.Lower: steps.Add(AttributeFlowValueProcessingStep.LowerCase); break;
            case InboundCaseNormalisation.Title: steps.Add(AttributeFlowValueProcessingStep.TitleCase); break;
        }
        if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.TreatWhitespaceAsNoValue))
            steps.Add(AttributeFlowValueProcessingStep.TreatWhitespaceAsNoValue);
        return steps;
    }

    /// <summary>
    /// Tests a Generated Value's base expression (#242), stopping before generation as synchronisation does: generation
    /// needs the database to know which candidates are taken.
    /// </summary>
    private static AttributeFlowTestResult TestGeneratedBase(
        SyncRule syncRule,
        SyncRuleMapping mapping,
        SyncRuleMappingSource? source,
        IReadOnlyList<AttributeFlowTestInput> inputs,
        IExpressionEvaluator expressionEvaluator,
        DateTime? asOfUtc)
    {
        var tested = TestGeneratedBaseOnce(syncRule, mapping, source, inputs, expressionEvaluator, asOfUtc);
        if (tested.GeneratedBase is not { } generated || string.IsNullOrWhiteSpace(source?.Expression))
            return tested;

        var readAccessors = ExpressionInputResolver.Resolve(source.Expression).Select(input => input.Accessor).ToHashSet(StringComparer.Ordinal);
        var ifInputHasNoValue = inputs
            .Where(input => input.Values.Count > 0 && readAccessors.Contains(input.Accessor))
            .Select(absent => new GeneratedBaseAbsentInputOutcome(absent.Accessor,
                TestGeneratedBaseOnce(syncRule, mapping, source,
                    inputs.Select(input => ReferenceEquals(input, absent) ? input with { Values = [] } : input).ToList(),
                    expressionEvaluator, asOfUtc).GeneratedBase!))
            .ToList();

        return new AttributeFlowTestResult
        {
            GeneratedBase = generated,
            ValueProcessing = DescribeValueProcessing(syncRule, mapping, generated),
            GeneratedBaseIfInputHasNoValue = ifInputHasNoValue
        };
    }

    private static AttributeFlowTestResult TestGeneratedBaseOnce(
        SyncRule syncRule,
        SyncRuleMapping mapping,
        SyncRuleMappingSource? source,
        IReadOnlyList<AttributeFlowTestInput> inputs,
        IExpressionEvaluator expressionEvaluator,
        DateTime? asOfUtc)
    {
        var objects = BuildTestObjects(syncRule, source?.Expression, inputs);
        if (objects.InputErrors.Count > 0)
            return new AttributeFlowTestResult { InputErrorDetails = objects.InputErrors };

        return new AttributeFlowTestResult
        {
            GeneratedBase = objects.ExportView is { } exportView
                ? AttributeFlowPipeline.EvaluateGeneratedBaseExport(exportView, mapping, source, expressionEvaluator, asOfUtc)
                : AttributeFlowPipeline.EvaluateGeneratedBaseInbound(objects.ConnectedSystemObject!, objects.ConnectedSystemObjectType!, mapping,
                    source, expressionEvaluator, objects.MetaverseView, asOfUtc)
        };
    }

    private static AttributeFlowTestResult TestOnce(
        SyncRule syncRule,
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        IReadOnlyList<AttributeFlowTestInput> inputs,
        IExpressionEvaluator expressionEvaluator,
        DateTime? asOfUtc)
    {
        var objects = BuildTestObjects(syncRule, source.Expression, inputs);
        if (objects.InputErrors.Count > 0)
            return new AttributeFlowTestResult { InputErrorDetails = objects.InputErrors };

        return new AttributeFlowTestResult
        {
            Evaluation = objects.ExportView is { } exportView
                ? AttributeFlowPipeline.EvaluateExport(exportView, mapping, source, expressionEvaluator, asOfUtc)
                : AttributeFlowPipeline.EvaluateInbound(objects.ConnectedSystemObject!, objects.ConnectedSystemObjectType!, mapping, source,
                    expressionEvaluator, objects.MetaverseView, asOfUtc)
        };
    }

    /// <summary>
    /// The transient objects a test evaluates against, holding the sample inputs as attribute values converted to their
    /// schema types, and the views synchronisation would build from them: the Metaverse Object's persisted values for
    /// export, or the Connected System Object (with the Metaverse Object's effective values when the Expression reads
    /// <c>mv[...]</c>) for import.
    /// </summary>
    private sealed record TestObjects(
        IReadOnlyList<JimMessage> InputErrors,
        IDictionary<string, object?>? ExportView = null,
        ConnectedSystemObject? ConnectedSystemObject = null,
        ConnectedSystemObjectType? ConnectedSystemObjectType = null,
        IDictionary<string, object?>? MetaverseView = null);

    private static TestObjects BuildTestObjects(SyncRule syncRule, string? expression, IReadOnlyList<AttributeFlowTestInput> inputs)
    {
        var errors = new List<JimMessage>();
        var exporting = syncRule.Direction == SyncRuleDirection.Export;
        var connectedSystemValues = new List<ConnectedSystemObjectAttributeValue>();
        var metaverseValues = new List<MetaverseObjectAttributeValue>();

        foreach (var input in inputs.Where(input => input.Values.Count > 0))
        {
            if (input.Source == ExpressionInputSource.ConnectedSystem)
            {
                if (exporting)
                {
                    errors.Add(ExpressionMessage.Of(nameof(ExpressionMessages.FlowExportReadsOnlyMetaverse), input.Accessor));
                    continue;
                }

                var attribute = syncRule.ConnectedSystemObjectType?.Attributes
                    .FirstOrDefault(a => string.Equals(a.Name, input.AttributeName, StringComparison.OrdinalIgnoreCase));
                if (attribute == null)
                {
                    errors.Add(NotInSchema(input, syncRule.ConnectedSystemObjectType?.Name));
                    continue;
                }

                foreach (var typed in TestSampleValues.Typed(input.Accessor, input.Values, attribute.Type, attribute.AttributePlurality, errors))
                    connectedSystemValues.Add(TestSampleValues.ToConnectedSystemValue(attribute, typed));
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

                foreach (var typed in TestSampleValues.Typed(input.Accessor, input.Values, attribute.Type, attribute.AttributePlurality, errors))
                    metaverseValues.Add(TestSampleValues.ToMetaverseValue(attribute, typed));
            }
        }

        if (errors.Count > 0)
            return new TestObjects(errors);

        var metaverseObject = new MetaverseObject { Type = syncRule.MetaverseObjectType ?? new MetaverseObjectType() };
        foreach (var value in metaverseValues)
        {
            value.MetaverseObject = metaverseObject;
            metaverseObject.AttributeValues.Add(value);
        }

        // Export evaluates against the Metaverse Object's persisted values.
        if (exporting)
            return new TestObjects([], ExportView: SyncEngine.BuildAttributeDictionary(metaverseObject));

        var connectedSystemObjectType = syncRule.ConnectedSystemObjectType ?? new ConnectedSystemObjectType();
        var connectedSystemObject = new ConnectedSystemObject { TypeId = connectedSystemObjectType.Id, MetaverseObject = metaverseObject };
        foreach (var value in connectedSystemValues)
        {
            value.ConnectedSystemObject = connectedSystemObject;
            connectedSystemObject.AttributeValues.Add(value);
        }

        // An import flow that reads mv[...] is a Metaverse-Derived Attribute Flow (#1750), evaluated by the derived
        // pass against the Metaverse Object's effective values; every other import flow has no Metaverse view.
        var readsMetaverse = ExpressionInputResolver.Resolve(expression)
            .Any(input => input.Source == ExpressionInputSource.Metaverse);
        var metaverseView = readsMetaverse ? SyncEngine.BuildEffectiveAttributeDictionary(metaverseObject) : null;

        return new TestObjects([], ConnectedSystemObject: connectedSystemObject, ConnectedSystemObjectType: connectedSystemObjectType,
            MetaverseView: metaverseView);
    }

    /// <summary>
    /// What synchronisation would do with <paramref name="evaluation"/>'s outcome, in one sentence an administrator
    /// reads beside the result. The portal tester and the REST API word it the same way: this is the message's British
    /// English, and <see cref="DescribeMessage"/> is the message itself, which the portal renders in the viewer's language.
    /// </summary>
    public static string Describe(AttributeFlowEvaluation evaluation, string targetAttributeName) =>
        DescribeMessage(evaluation, targetAttributeName).ToEnglish();

    /// <summary>
    /// <see cref="Describe"/> as a message (#3030), so the portal can show the sentence in the viewer's language.
    /// </summary>
    public static JimMessage DescribeMessage(AttributeFlowEvaluation evaluation, string targetAttributeName)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        MessageArgument target = targetAttributeName;
        var missingInputs = string.Join(", ", evaluation.Source.MissingInputs);
        var keepsValue = ExpressionMessage.Of(nameof(ExpressionMessages.FlowKeepsValue), target);
        var failsObject = ExpressionMessage.Of(nameof(ExpressionMessages.FlowFailsObject));

        return evaluation.Outcome switch
        {
            AttributeFlowOutcome.Values => evaluation.Values.Count == 1
                ? ExpressionMessage.Of(nameof(ExpressionMessages.DescribeSingleValue), target)
                : ExpressionMessage.Of(nameof(ExpressionMessages.DescribeSeveralValues), target, ExpressionMessage.Digits(evaluation.Values.Count)),

            AttributeFlowOutcome.NoValue when evaluation.Source.MissingInputs.Count > 0 =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeNoValueMissingInput), target, missingInputs),

            AttributeFlowOutcome.NoValue =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeNoValueExpression), target),

            AttributeFlowOutcome.LeftAsHeld =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeLeftAsHeld), target, HeldInputs(evaluation.Source.HeldInputs)),

            _ => evaluation.ErrorReason switch
            {
                AttributeFlowErrorReason.MissingInputFailedMapping =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeMissingFailMapping), missingInputs, keepsValue),
                AttributeFlowErrorReason.MissingInputFailedObject =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeMissingFailObject), missingInputs, failsObject),
                AttributeFlowErrorReason.EvaluationFailed =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeEvaluationFailed), failsObject,
                        ExpressionMessage.Reason(evaluation.Source.EvaluationException)),
                AttributeFlowErrorReason.ResultNotConvertible =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeNotConvertible),
                        evaluation.Resolution?.UnconvertibleValue?.ToString() ?? string.Empty, ExpressionMessage.DataType(evaluation.TargetType), keepsValue),
                AttributeFlowErrorReason.ResultMultiValued when evaluation.Resolution?.SourceAttributeName is { } sourceAttribute =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeMultiValuedNamed), sourceAttribute,
                        ExpressionMessage.Digits(evaluation.Resolution.ValueCount), target, keepsValue, "Where", "Join", "Count", "Min", "Max"),
                AttributeFlowErrorReason.ResultMultiValued =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeMultiValuedUnnamed),
                        evaluation.Resolution is { } resolution ? ExpressionMessage.Digits(resolution.ValueCount) : string.Empty, target, keepsValue),
                _ => throw new InvalidOperationException($"Unknown Attribute Flow error reason '{evaluation.ErrorReason}'.")
            }
        };
    }

    /// <summary>
    /// What synchronisation would do with a Generated Value's base, in one or two sentences an administrator reads beside
    /// it. The test stops before generation, so this says how the token would be added, never which candidate is free.
    /// The portal tester and the REST API word it the same way; <see cref="DescribeGeneratedBaseMessage"/> is the message.
    /// </summary>
    public static string DescribeGeneratedBase(GeneratedBaseEvaluation generated, SyncRuleMappingGeneration generation, string targetAttributeName) =>
        DescribeGeneratedBaseMessage(generated, generation, targetAttributeName).ToEnglish();

    /// <summary>
    /// <see cref="DescribeGeneratedBase"/> as a message (#3030), so the portal can show it in the viewer's language.
    /// </summary>
    public static JimMessage DescribeGeneratedBaseMessage(GeneratedBaseEvaluation generated, SyncRuleMappingGeneration generation, string targetAttributeName)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(generation);

        MessageArgument target = targetAttributeName;
        var missingInputs = generated.Source == null ? string.Empty : string.Join(", ", generated.Source.MissingInputs);
        var nothingGenerated = ExpressionMessage.Of(nameof(ExpressionMessages.GenNothingGenerated), target);
        var failsObject = ExpressionMessage.Of(nameof(ExpressionMessages.FlowFailsObject));
        var waits = ExpressionMessage.Of(nameof(ExpressionMessages.GenWaits));
        var token = generation.TokenKind switch
        {
            GeneratedValueTokenKind.Sequence => ExpressionMessage.Of(nameof(ExpressionMessages.GenTokenSequence)),
            GeneratedValueTokenKind.Random => ExpressionMessage.Of(nameof(ExpressionMessages.GenTokenRandom)),
            _ => generation.SuffixStyle == GeneratedValueSuffixStyle.Letter
                ? ExpressionMessage.Of(nameof(ExpressionMessages.GenTokenLetter))
                : ExpressionMessage.Of(nameof(ExpressionMessages.GenTokenNumber))
        };

        return generated.Outcome switch
        {
            GeneratedBaseOutcome.Base when generation.TokenKind == GeneratedValueTokenKind.OnlyIfTaken =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeBaseOnlyIfTaken), target, generated.BaseValue ?? string.Empty, token),

            GeneratedBaseOutcome.Base =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeBase), target, generated.BaseValue ?? string.Empty, token),

            GeneratedBaseOutcome.NoBaseExpression =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeNoBaseExpression), target, token),

            GeneratedBaseOutcome.Waits when generated.Source?.MissingInputs.Count > 0 =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeGenWaitsMissing), missingInputs, waits),

            GeneratedBaseOutcome.Waits when generated.Source is { HeldInputs.Count: > 0 } heldSource =>
                ExpressionMessage.Of(nameof(ExpressionMessages.DescribeGenWaitsHeld), HeldInputs(heldSource.HeldInputs), waits),

            GeneratedBaseOutcome.Waits => ExpressionMessage.Of(nameof(ExpressionMessages.DescribeGenWaitsNoValue), waits),

            _ => generated.ErrorReason switch
            {
                AttributeFlowErrorReason.GeneratedBaseNotSingleValue =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeGenNotSingle),
                        ExpressionMessage.Digits(ExpressionResultValues.Elements(generated.Source?.Result).Count()), nothingGenerated),
                AttributeFlowErrorReason.ResultNotConvertible =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeGenNotConvertible), generated.UnconvertibleValue?.ToString() ?? string.Empty, nothingGenerated),
                AttributeFlowErrorReason.MissingInputFailedMapping =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeMissingFailMapping), missingInputs, nothingGenerated),
                AttributeFlowErrorReason.MissingInputFailedObject =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeMissingFailObject), missingInputs, failsObject),
                AttributeFlowErrorReason.EvaluationFailed =>
                    ExpressionMessage.Of(nameof(ExpressionMessages.DescribeGenEvaluationFailed), failsObject,
                        ExpressionMessage.Reason(generated.Source?.EvaluationException)),
                _ => throw new InvalidOperationException($"Unknown generated base error reason '{generated.ErrorReason}'.")
            }
        };
    }

    /// <summary>The inputs held under another data type, as a list in the reader's language: <c>cs["sn"] (held as Text)</c>.</summary>
    private static MessageArgument HeldInputs(IEnumerable<HeldExpressionInput> held) =>
        ExpressionMessage.List(held.Select(input =>
            (MessageArgument)ExpressionMessage.Of(nameof(ExpressionMessages.HeldInput), input.Accessor, ExpressionMessage.DataType(input.HeldDataType))));

    private static JimMessage NotInSchema(AttributeFlowTestInput input, string? objectTypeName) =>
        objectTypeName == null
            ? ExpressionMessage.Of(nameof(ExpressionMessages.FlowNotInSchemaUnnamed), input.Accessor, input.AttributeName)
            : ExpressionMessage.Of(nameof(ExpressionMessages.FlowNotInSchemaNamed), input.Accessor, objectTypeName, input.AttributeName);

}
