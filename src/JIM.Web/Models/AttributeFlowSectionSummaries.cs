// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web.Localisation;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// The one-line summary each collapsible section of the Attribute Flow dialog shows while it is closed (#2471), so that
/// collapsing a section never hides what it is set to. Each names the choice as the form offers it.
/// <para>
/// Every word comes from <c>SyncRuleText</c> in the viewer's language (#3030). Each summary is a whole template, never
/// English assembled from translated fragments.
/// </para>
/// </summary>
public static class AttributeFlowSectionSummaries
{
    /// <summary>The uniqueness token a Generated Value adds.</summary>
    public static string UniquenessToken(GeneratedValueTokenKind kind) => kind switch
    {
        GeneratedValueTokenKind.Sequence => SyncRuleText.UniqueSequence,
        GeneratedValueTokenKind.Random => SyncRuleText.UniqueRandom,
        _ => SyncRuleText.UniqueIfTaken
    };

    /// <summary>
    /// The Missing Input Behaviour. A Generated Value offers "Contribute no value" as waiting, since a value generated
    /// from a missing input would be kept for ever.
    /// </summary>
    public static string MissingInput(MissingInputBehaviour behaviour, bool generated) => behaviour switch
    {
        MissingInputBehaviour.ContributeNoValue => generated ? SyncRuleText.MissingInputWait : SyncRuleText.MissingInputContributeNoValue,
        MissingInputBehaviour.FailMapping => SyncRuleText.MissingInputFailMapping,
        MissingInputBehaviour.FailObject => SyncRuleText.MissingInputFailObject,
        _ => SyncRuleText.MissingInputEvaluateAnyway
    };

    /// <summary>Where a Generated Value is checked for availability; null while the Connected Systems load.</summary>
    public static string Availability(IReadOnlyList<GeneratedValueParticipant>? participants)
    {
        if (participants == null)
            return SyncRuleText.AvailabilityLoading;
        if (participants.Count == 0)
            return SyncRuleText.AvailabilityNotExported;

        var checkedCount = participants.Count(participant => !participant.IsExcluded);
        var excludedCount = participants.Count - checkedCount;
        var checkedTemplate = PluralRules.Choose(checkedCount, SyncRuleText.CheckedInOne, SyncRuleText.CheckedInOther);
        var summary = string.Format(CultureInfo.CurrentCulture, checkedTemplate, checkedCount);
        return excludedCount > 0
            ? string.Format(CultureInfo.CurrentCulture, SyncRuleText.AvailabilityExcluded, summary, excludedCount)
            : summary;
    }

    /// <summary>
    /// Collision Remediation, which reads off while no system the value is exported to can report a collision,
    /// whatever is stored.
    /// </summary>
    public static string CollisionRemediation(SyncRuleMappingGeneration generation, IReadOnlyList<GeneratedValueParticipant>? participants)
    {
        if (participants?.Any(participant => participant.ReportsCollisions) != true)
            return SyncRuleText.NotAvailable;
        return generation.CollisionRemediation ? SyncRuleText.SwitchOn : SyncRuleText.SwitchOff;
    }

    /// <summary>
    /// How an import Attribute Flow treats no value and processes text: "Null is a value", then each value processing
    /// step that is on. Value processing applies to Text attributes only.
    /// </summary>
    public static string ValueHandling(SyncRuleMapping mapping, bool textTarget)
    {
        var parts = new List<string>();
        if (mapping.NullIsValue)
            parts.Add(SyncRuleText.NullIsValueLabel);

        if (textTarget)
        {
            if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.TreatWhitespaceAsNoValue))
                parts.Add(SyncRuleText.TreatWhitespaceAsNoValue);
            if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.TrimWhitespace))
                parts.Add(SyncRuleText.TrimWhitespaceShort);
            if (mapping.InboundValueProcessing.HasFlag(InboundValueProcessing.CollapseInternalWhitespace))
                parts.Add(SyncRuleText.CollapseInternalWhitespace);
            if (mapping.CaseNormalisation != InboundCaseNormalisation.None)
                parts.Add(CaseNormalisationName(mapping.CaseNormalisation));
        }

        return parts.Count > 0 ? string.Join(SyncRuleText.ListSeparator, parts) : SyncRuleText.NullIsNotAValue;
    }

    /// <summary>The name of a case normalisation, as the value processing options and this summary show it.</summary>
    public static string CaseNormalisationName(InboundCaseNormalisation normalisation) => normalisation switch
    {
        InboundCaseNormalisation.Upper => SyncRuleText.CaseUpper,
        InboundCaseNormalisation.Lower => SyncRuleText.CaseLower,
        InboundCaseNormalisation.Title => SyncRuleText.CaseTitle,
        _ => CommonText.None
    };

    /// <summary>The name of a mapping type chip.</summary>
    public static string MappingTypeName(AttributeFlowMappingType type) => type switch
    {
        AttributeFlowMappingType.AttributeMapping => SyncRuleText.MappingTypeAttributeMapping,
        AttributeFlowMappingType.ExpressionMapping => SyncRuleText.MappingTypeExpressionMapping,
        AttributeFlowMappingType.Derived => SyncRuleText.MappingTypeDerived,
        AttributeFlowMappingType.GeneratedMapping => SyncRuleText.MappingTypeGeneratedMapping,
        AttributeFlowMappingType.AdvancedMapping => SyncRuleText.MappingTypeAdvancedMapping,
        _ => SyncRuleText.NotSet
    };

    /// <summary>The name of a mapping's source type, as the Type column shows it.</summary>
    public static string MappingTypeName(SyncRuleMappingSourcesType sourceType) => sourceType switch
    {
        SyncRuleMappingSourcesType.AttributeMapping => SyncRuleText.MappingTypeAttributeMapping,
        SyncRuleMappingSourcesType.ExpressionMapping => SyncRuleText.MappingTypeExpressionMapping,
        SyncRuleMappingSourcesType.AdvancedMapping => SyncRuleText.MappingTypeAdvancedMapping,
        SyncRuleMappingSourcesType.GeneratedMapping => SyncRuleText.MappingTypeGeneratedMapping,
        _ => SyncRuleText.NotSet
    };

    /// <summary>The name of an attribute data type, as shown beside an attribute.</summary>
    public static string DataTypeName(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => SyncRuleText.DataTypeText,
        AttributeDataType.Number => SyncRuleText.DataTypeNumber,
        AttributeDataType.DateTime => SyncRuleText.DataTypeDateTime,
        AttributeDataType.Binary => SyncRuleText.DataTypeBinary,
        AttributeDataType.Reference => SyncRuleText.DataTypeReference,
        AttributeDataType.Guid => SyncRuleText.DataTypeGuid,
        AttributeDataType.Boolean => SyncRuleText.DataTypeBoolean,
        AttributeDataType.LongNumber => SyncRuleText.DataTypeLongNumber,
        _ => SyncRuleText.NotSet
    };

    /// <summary>The name of an attribute's plurality, as the filter chips show it.</summary>
    public static string PluralityName(AttributePlurality plurality) => plurality == AttributePlurality.SingleValued
        ? SyncRuleText.PluralitySingleValued
        : SyncRuleText.PluralityMultiValued;

    /// <summary>The plurality as it reads beside an attribute in a list, with no hyphen.</summary>
    public static string PluralityWords(AttributePlurality plurality) => plurality == AttributePlurality.SingleValued
        ? SyncRuleText.PluralitySingleValuedWords
        : SyncRuleText.PluralityMultiValuedWords;
}
