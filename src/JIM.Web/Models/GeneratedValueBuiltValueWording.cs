// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Web.Models;

/// <summary>
/// What the portal says about values built from a generated value (Generated Value Forms, #2014), in one place: the
/// "Built from generated value" block on an Attribute Flow and the "Values built from ..." table on a generated one. Plain
/// strings, rendered through Blazor's encoder, because attribute and Synchronisation Rule names are administrator data.
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
        GeneratedValueBuiltValueRefusal.GeneratedValueCorrected => $"{generatedAttributeName} is corrected",
        GeneratedValueBuiltValueRefusal.KeepUniqueOff => "Export error (Keep unique is off)",
        GeneratedValueBuiltValueRefusal.CollisionRemediationOff => "Export error (Collision Remediation is off)",
        _ => "Export error (built from more than one generated value)"
    };

    /// <summary>
    /// How the value is built, after its Synchronisation Rule's name: from the generated value itself, through other
    /// built values, by an export expression, or from other generated values as well.
    /// </summary>
    public static string HowBuilt(GeneratedValueBuiltValueDescription value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)
            return $"also built from {JoinNames(value.BuiltFromAttributeNames.Skip(1).ToList())}";
        if (value.Via.Count > 0)
            return $"via {JoinNames(value.Via)}";
        return value.MetaverseAttributeName == null ? "Expression" : $"from {value.GeneratedAttributeName}";
    }

    /// <summary>The "Values built from ..." table's title.</summary>
    public static string PanelTitle(string generatedAttributeName) => $"Values built from {generatedAttributeName}";

    /// <summary>The table's explanation, under its title.</summary>
    public static string PanelHelperText(string generatedAttributeName) =>
        $"JIM issues {generatedAttributeName} only when every value checked before issuing is free too, so they stay consistent. " +
        $"If a target refuses one anyway, JIM corrects {generatedAttributeName}, unless the value's Keep unique switch is off. " +
        "Change a value's switch on its own Attribute Flow.";

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
        outcome == GeneratedValueBuiltValueBeforeIssuing.Checked ? "Checked" : "Not checked";

    /// <summary>
    /// The "Before issuing" column's detail: where a checked value is checked, or why a value is not.
    /// </summary>
    public static string BeforeIssuingDetail(GeneratedValueBuiltValueDescription value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BeforeIssuingOutcome(value) switch
        {
            GeneratedValueBuiltValueBeforeIssuing.Checked => value.ProbedIn.Count == 0
                ? "JIM's records"
                : $"JIM's records and {JoinNames(value.ProbedIn)}",
            GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule =>
                $"Built on {value.SyncRuleName}, which runs only once the person is in that system.{MoveSuggestion(value)}",
            GeneratedValueBuiltValueBeforeIssuing.KeepUniqueOff => "Keep unique is off",
            GeneratedValueBuiltValueBeforeIssuing.BuiltFromSeveral => "Built from more than one generated value",
            _ => $"Its Connected System is excluded from {value.GeneratedAttributeName}'s checks"
        };
    }

    /// <summary>The table's text when nothing is built from the generated value.</summary>
    public static string NoBuiltValuesText(string generatedAttributeName) =>
        $"No Attribute Flow builds a value from {generatedAttributeName}.";

    /// <summary>The Attribute Flow's switch label.</summary>
    public static string SwitchLabel(string generatedAttributeName) => $"Keep unique with {generatedAttributeName}";

    /// <summary>
    /// What the switch does on <paramref name="value"/>, with the switch as it now stands: whether the value is checked
    /// before the generated value is issued (Generated Value Forms, #2023), and what happens if a target refuses it.
    /// </summary>
    public static string SwitchHelpText(GeneratedValueBuiltValueDescription value, bool keepUnique)
    {
        ArgumentNullException.ThrowIfNull(value);

        var generated = value.GeneratedAttributeName;
        if (!keepUnique)
            return $"If a target refuses this value, JIM reports an export error and leaves {generated} unchanged.";

        var corrected = $"JIM corrects {generated} and works this value out again from the new one.";
        return BeforeIssuingOutcome(value, keepUnique) switch
        {
            GeneratedValueBuiltValueBeforeIssuing.Checked =>
                $"Checked before {generated} is issued: JIM moves on to the next {generated} if this value is already in use. If a target refuses it anyway, {corrected}",
            GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule =>
                $"Not checked before {generated} is issued, because it is built on {value.SyncRuleName}, a different Synchronisation Rule.{MoveSuggestion(value)} If a target refuses it, {corrected}",
            _ => $"Not checked before {generated} is issued, because its Connected System is excluded from {generated}'s checks. If a target refuses it, {corrected}"
        };
    }

    private static string MoveSuggestion(GeneratedValueBuiltValueDescription value) =>
        value.MoveToSyncRuleName is { } rule ? $" Move it to {rule} to have it checked first." : string.Empty;

    /// <summary>Why the switch is not in effect while the generated flow's own Collision Remediation is off.</summary>
    public static string NotInEffectText(string generatedAttributeName) =>
        $"Collision Remediation is off on the {generatedAttributeName} Attribute Flow, so nothing built from {generatedAttributeName} " +
        "is corrected and a refusal of this value is an export error. You can still change the switch; it takes effect when " +
        "Collision Remediation is turned back on.";

    /// <summary>Why a value built from several generated values has no switch.</summary>
    public static string BuiltFromSeveralText(IReadOnlyList<string> generatedAttributeNames) =>
        $"This value is built from {JoinNames(generatedAttributeNames)}, so JIM cannot tell which to correct. A refusal is reported as an export error.";

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };
}
