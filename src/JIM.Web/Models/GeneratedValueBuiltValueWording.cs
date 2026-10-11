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
        $"Each of these changes when {generatedAttributeName} does. If a target refuses one as already in use, JIM corrects " +
        $"{generatedAttributeName}, unless the value's Keep unique switch is off. Change a value's switch on its own Attribute Flow.";

    /// <summary>The table's text when nothing is built from the generated value.</summary>
    public static string NoBuiltValuesText(string generatedAttributeName) =>
        $"No Attribute Flow builds a value from {generatedAttributeName}.";

    /// <summary>The Attribute Flow's switch label.</summary>
    public static string SwitchLabel(string generatedAttributeName) => $"Keep unique with {generatedAttributeName}";

    /// <summary>What the switch does, as it stands.</summary>
    public static string SwitchHelpText(bool keepUnique, string generatedAttributeName) => keepUnique
        ? $"If a target refuses this value as already in use, JIM corrects {generatedAttributeName} and works this value out again from the new one."
        : $"If a target refuses this value, JIM reports an export error and leaves {generatedAttributeName} unchanged.";

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
