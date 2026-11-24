// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Utilities;

namespace JIM.Application.Localisation;

/// <summary>
/// What a synchronisation run records on an object's execution item when an Attribute Flow, an expression or a generated
/// value stops it (#3039). Each message's English is the text the Worker wrote before there were messages.
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>
    /// Why an expression failed while flowing to an attribute, naming the Metaverse Object where the flow is an export.
    /// </summary>
    public static JimMessage ExpressionEvaluationFailed(SyncExpressionEvaluationException expressionEx, Guid? metaverseObjectId = null)
    {
        ArgumentNullException.ThrowIfNull(expressionEx);

        var attribute = expressionEx.TargetAttributeName ?? (MessageArgument)Create(nameof(ActivityMessages.UnknownValue));
        var expression = LogSanitiser.Sanitise(expressionEx.Expression) ?? (MessageArgument)Create(nameof(ActivityMessages.NoneValue));
        var reason = CauseOf(expressionEx.InnerException ?? expressionEx);
        MessageArgument subject = metaverseObjectId.HasValue
            ? Create(nameof(ActivityMessages.ExprFlowSubject), metaverseObjectId.Value.ToString())
            : string.Empty;
        return Create(nameof(ActivityMessages.ExprFlowFailed), subject, attribute, DerivedHost(expressionEx.SyncRuleName), reason, expression);
    }

    private static MessageArgument DerivedHost(string? syncRuleName) =>
        syncRuleName == null ? string.Empty : Create(nameof(ActivityMessages.ExprFlowDerived), syncRuleName);

    /// <summary>A mapping's expression failing because the object has no value for an input, on an export.</summary>
    public static JimMessage ExportMissingInput(SyncExpressionMissingInputException missingInputEx, Guid metaverseObjectId, string connectedSystemName)
    {
        ArgumentNullException.ThrowIfNull(missingInputEx);
        return Create(nameof(ActivityMessages.ExportMissingInput), CauseOf(missingInputEx), metaverseObjectId.ToString(), connectedSystemName);
    }

    /// <summary>Why drift detection did not check an object: the reason, after the sentence that says nothing was staged.</summary>
    public static JimMessage DriftNotChecked(MessageArgument reason) => Create(nameof(ActivityMessages.DriftNotChecked), reason);

    /// <summary>What an execution item reports for an Attribute Flow error.</summary>
    /// <param name="error">The error the flow recorded.</param>
    /// <param name="exporting">True when describing an export, which generates no Pending Export rather than flowing no value.</param>
    /// <param name="targetSystemName">The Connected System being exported to, named in the message; null when importing.</param>
    public static JimMessage DescribeAttributeFlowError(AttributeFlowError error, bool exporting, string? targetSystemName)
    {
        ArgumentNullException.ThrowIfNull(error);

        MessageArgument target = exporting && targetSystemName != null
            ? Create(nameof(ActivityMessages.AfTargetExport), error.TargetAttributeName, targetSystemName)
            : error.SyncRuleName == null
                ? Create(nameof(ActivityMessages.AfTargetImport), error.TargetAttributeName)
                : Create(nameof(ActivityMessages.AfTargetImportDerived), error.TargetAttributeName, error.SyncRuleName);
        MessageArgument outcome = Create(exporting ? nameof(ActivityMessages.AfOutcomeExport) : nameof(ActivityMessages.AfOutcomeImport));

        switch (error.Kind)
        {
            case AttributeFlowErrorKind.ExpressionMissingInput:
                return Create(nameof(ActivityMessages.AfMissingInput), target, string.Join(", ", error.MissingInputs), outcome);

            case AttributeFlowErrorKind.GeneratedBaseNotSingleValue:
                return Create(nameof(ActivityMessages.AfGeneratedBaseNotSingle), target, outcome, error.Expression ?? string.Empty);

            case AttributeFlowErrorKind.ExpressionResultNotConvertible:
                return Create(nameof(ActivityMessages.AfNotConvertible), target, error.Value ?? string.Empty,
                    error.TargetAttributeType?.ToString() ?? string.Empty, outcome, error.Expression ?? string.Empty);

            case AttributeFlowErrorKind.ExpressionResultMultiValued when error.SourceAttributeName != null:
                return Create(nameof(ActivityMessages.AfMultiValuedNamed), error.SourceAttributeName, Plain(error.ValueCount), target, outcome,
                    error.Expression ?? string.Empty);

            case AttributeFlowErrorKind.ExpressionResultMultiValued:
                return Create(nameof(ActivityMessages.AfMultiValued), target, Plain(error.ValueCount), outcome, error.Expression ?? string.Empty);

            case AttributeFlowErrorKind.DatesCollapseAtTargetResolution:
                MessageArgument collapsedSource = error.SourceAttributeName != null
                    ? Create(nameof(ActivityMessages.AfDatesNamed), error.SourceAttributeName, Plain(error.ValueCount))
                    : Create(nameof(ActivityMessages.AfDatesExpression), target, Plain(error.ValueCount));
                return Create(nameof(ActivityMessages.AfDatesCollapse), collapsedSource, target, DateTimeResolutionPhrase(error.TargetDateTimeResolution),
                    Plain(error.TargetValueCount));

            case AttributeFlowErrorKind.CredentialAttributeTarget:
                return Create(nameof(ActivityMessages.AfCredential), target, outcome);

            default:
                MessageArgument source = Create(exporting ? nameof(ActivityMessages.AfSourceExport) : nameof(ActivityMessages.AfSourceImport),
                    error.SourceAttributeName ?? string.Empty);
                return Create(nameof(ActivityMessages.AfMultiToSingle), source, Plain(error.ValueCount), target, outcome);
        }
    }

    private static JimMessage DateTimeResolutionPhrase(DateTimeResolution? resolution) => Create(resolution switch
    {
        DateTimeResolution.TenMicroseconds => nameof(ActivityMessages.AfResolutionTenMicroseconds),
        DateTimeResolution.HundredMicroseconds => nameof(ActivityMessages.AfResolutionHundredMicroseconds),
        DateTimeResolution.Millisecond => nameof(ActivityMessages.AfResolutionMillisecond),
        DateTimeResolution.TenMilliseconds => nameof(ActivityMessages.AfResolutionTenMilliseconds),
        DateTimeResolution.HundredMilliseconds => nameof(ActivityMessages.AfResolutionHundredMilliseconds),
        DateTimeResolution.Second => nameof(ActivityMessages.AfResolutionSecond),
        DateTimeResolution.Minute => nameof(ActivityMessages.AfResolutionMinute),
        DateTimeResolution.Day => nameof(ActivityMessages.AfResolutionDay),
        _ => nameof(ActivityMessages.AfResolutionMicrosecond)
    });

    /// <summary>The warning for a generated value that lost a race to another object holding it.</summary>
    public static JimMessage GeneratedValueLostRace(string attributeName, string? value) =>
        Create(nameof(ActivityMessages.GeneratedValueLostRace), attributeName, value ?? string.Empty);
}
