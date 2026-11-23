// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Logic;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// What the portal says about values built from a generated value (Generated Value Forms, #2014), in one place: the
/// "Built from generated value" block on an Attribute Flow and the "Values built from ..." table on a generated one. Plain
/// strings, rendered through Blazor's encoder, because attribute and Synchronisation Rule names are administrator data.
/// Written in the viewer's language (#3030): the words come from <c>GeneratedValueText</c>.
/// </summary>
public static class GeneratedValueBuiltValueWording
{
    /// <summary>
    /// What happens when a target refuses <paramref name="value"/> as already in use, given the generated Attribute Flow's
    /// own Collision Remediation switch as the form now holds it. A value built from several generated values is never
    /// corrected; then the generated flow's switch comes before the value's own.
    /// </summary>
    public static GeneratedValueBuiltValueRefusal RefusalOutcome(GeneratedValueBuiltValueDescription value, bool collisionRemediation)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)
            return GeneratedValueBuiltValueRefusal.BuiltFromSeveral;
        if (!collisionRemediation)
            return GeneratedValueBuiltValueRefusal.CollisionRemediationOff;
        return value.KeepUnique ? GeneratedValueBuiltValueRefusal.GeneratedValueCorrected : GeneratedValueBuiltValueRefusal.KeepUniqueOff;
    }

    /// <summary>The "If a target refuses it" column's text.</summary>
    public static string RefusalText(GeneratedValueBuiltValueRefusal outcome, string generatedAttributeName) => outcome switch
    {
        GeneratedValueBuiltValueRefusal.GeneratedValueCorrected => Format(GeneratedValueText.RefusalCorrected, generatedAttributeName),
        GeneratedValueBuiltValueRefusal.KeepUniqueOff => GeneratedValueText.RefusalKeepUniqueOff,
        GeneratedValueBuiltValueRefusal.CollisionRemediationOff => GeneratedValueText.RefusalCollisionOff,
        _ => GeneratedValueText.RefusalBuiltFromSeveral
    };

    /// <summary>
    /// How the value is built, after its Synchronisation Rule's name: from the generated value itself, through other
    /// built values, by an export expression, or from other generated values as well.
    /// </summary>
    public static string HowBuilt(GeneratedValueBuiltValueDescription value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)
            return Format(GeneratedValueText.HowBuiltAlsoFrom, JoinNames(value.BuiltFromAttributeNames.Skip(1).ToList()));
        if (value.Via.Count > 0)
            return Format(GeneratedValueText.HowBuiltVia, JoinNames(value.Via));
        return value.MetaverseAttributeName == null
            ? GeneratedValueText.HowBuiltExpression
            : Format(GeneratedValueText.HowBuiltFrom, value.GeneratedAttributeName);
    }

    /// <summary>The "Values built from ..." table's title.</summary>
    public static string PanelTitle(string generatedAttributeName) => Format(GeneratedValueText.PanelTitle, generatedAttributeName);

    /// <summary>The table's explanation, under its title.</summary>
    public static string PanelHelperText(string generatedAttributeName) =>
        Format(GeneratedValueText.PanelHelperText, generatedAttributeName, generatedAttributeName);

    /// <summary>
    /// Whether <paramref name="value"/> is checked before its generated value is issued, and if not, why (Generated Value
    /// Forms, #2023). <paramref name="keepUnique"/> is the switch as it now stands where that may differ from when the
    /// value was described (the flow's own dialog edits it in place); a value on the generated value's own rule that was
    /// described switched off is taken to be checked once switched on.
    /// </summary>
    public static GeneratedValueBuiltValueBeforeIssuing BeforeIssuingOutcome(GeneratedValueBuiltValueDescription value, bool? keepUnique = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)
            return GeneratedValueBuiltValueBeforeIssuing.BuiltFromSeveral;
        if (!(keepUnique ?? value.KeepUnique))
            return GeneratedValueBuiltValueBeforeIssuing.KeepUniqueOff;
        if (value.Kind == GeneratedValueBuiltValueKind.BuiltOnAnotherRule)
            return GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule;

        // On the generated value's own rule and switched on: checked, unless it was described switched on and still not
        // checked, which leaves only its Connected System being excluded.
        return value.CheckedBeforeIssuing || !value.KeepUnique
            ? GeneratedValueBuiltValueBeforeIssuing.Checked
            : GeneratedValueBuiltValueBeforeIssuing.Excluded;
    }

    /// <summary>The "Before issuing" column's state.</summary>
    public static string BeforeIssuingText(GeneratedValueBuiltValueBeforeIssuing outcome) =>
        outcome == GeneratedValueBuiltValueBeforeIssuing.Checked
            ? GeneratedValueText.BeforeIssuingChecked
            : GeneratedValueText.BeforeIssuingNotChecked;

    /// <summary>
    /// The "Before issuing" column's detail: where a checked value is checked, or why a value is not.
    /// </summary>
    public static string BeforeIssuingDetail(GeneratedValueBuiltValueDescription value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BeforeIssuingOutcome(value) switch
        {
            GeneratedValueBuiltValueBeforeIssuing.Checked => value.ProbedIn.Count == 0
                ? GeneratedValueText.BeforeIssuingRecords
                : Format(GeneratedValueText.BeforeIssuingRecordsAndProbes, JoinNames(value.ProbedIn)),
            GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule =>
                Format(GeneratedValueText.BeforeIssuingOtherRule, value.SyncRuleName) + MoveSuggestion(value),
            GeneratedValueBuiltValueBeforeIssuing.KeepUniqueOff => GeneratedValueText.BeforeIssuingKeepUniqueOff,
            GeneratedValueBuiltValueBeforeIssuing.BuiltFromSeveral => GeneratedValueText.BeforeIssuingBuiltFromSeveral,
            _ => Format(GeneratedValueText.BeforeIssuingExcluded, value.GeneratedAttributeName)
        };
    }

    /// <summary>The table's text when nothing is built from the generated value.</summary>
    public static string NoBuiltValuesText(string generatedAttributeName) =>
        Format(GeneratedValueText.NoBuiltValues, generatedAttributeName);

    /// <summary>The Attribute Flow's switch label.</summary>
    public static string SwitchLabel(string generatedAttributeName) => Format(GeneratedValueText.SwitchLabel, generatedAttributeName);

    /// <summary>
    /// What the switch does on <paramref name="value"/>, with the switch as it now stands: whether the value is checked
    /// before the generated value is issued (Generated Value Forms, #2023), and what happens if a target refuses it.
    /// </summary>
    public static string SwitchHelpText(GeneratedValueBuiltValueDescription value, bool keepUnique)
    {
        ArgumentNullException.ThrowIfNull(value);

        var generated = value.GeneratedAttributeName;
        if (!keepUnique)
            return Format(GeneratedValueText.SwitchKeepUniqueOff, generated);

        var corrected = Format(GeneratedValueText.SwitchCorrected, generated);
        return BeforeIssuingOutcome(value, keepUnique) switch
        {
            GeneratedValueBuiltValueBeforeIssuing.Checked =>
                Format(GeneratedValueText.SwitchChecked, generated, corrected),
            GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule =>
                Format(GeneratedValueText.SwitchNotCheckedOtherRule, generated, value.SyncRuleName, MoveSuggestion(value), corrected),
            _ => Format(GeneratedValueText.SwitchNotCheckedExcluded, generated, corrected)
        };
    }

    private static string MoveSuggestion(GeneratedValueBuiltValueDescription value) =>
        value.MoveToSyncRuleName is { } rule ? Format(GeneratedValueText.MoveSuggestion, rule) : string.Empty;

    /// <summary>Why the switch is not in effect while the generated flow's own Collision Remediation is off.</summary>
    public static string NotInEffectText(string generatedAttributeName) =>
        Format(GeneratedValueText.NotInEffect, generatedAttributeName);

    /// <summary>Why a value built from several generated values has no switch.</summary>
    public static string BuiltFromSeveralText(IReadOnlyList<string> generatedAttributeNames) =>
        Format(GeneratedValueText.BuiltFromSeveral, JoinNames(generatedAttributeNames));

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => Format(GeneratedValueText.ListAnd, string.Join(", ", names.Take(names.Count - 1)), names[^1])
    };

    private static string Format(string template, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);
}
