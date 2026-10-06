// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Transactional;

namespace JIM.Web.Models;

/// <summary>
/// What the portal says about where a generated value is checked for availability (Unique Value Generation, #242,
/// release 3), in one place: the "Checked for availability in" panel on the generated Attribute Flow form, and the Sync
/// Preview note about probing. Plain strings, rendered through Blazor's encoder, because Connected System names and
/// generated values are administrator data.
/// </summary>
public static class GeneratedValueAvailabilityWording
{
    /// <summary>The panel's explanation, under its title.</summary>
    public const string PanelHelperText =
        "Every Connected System this value is exported to. Where it is exported unchanged, JIM checks its own records of that system " +
        "before choosing a value and, where the Connector supports it, also probes the target: it asks the target directly whether " +
        "the value is already in use, which catches accounts JIM doesn't import. This list updates itself as export Synchronisation " +
        "Rules change.";

    /// <summary>The panel's text when no export Synchronisation Rule sends the value anywhere.</summary>
    public const string NoParticipantsText =
        "No export Synchronisation Rule sends this value to a Connected System yet, so JIM checks only the values it has already generated.";

    /// <summary>The Checked column's chip.</summary>
    public static string CheckLabel(GeneratedValueParticipantCheck check) => check switch
    {
        GeneratedValueParticipantCheck.JimRecordsAndProbe => "JIM's records + probe",
        GeneratedValueParticipantCheck.JimRecordsOnly => "JIM's records only",
        _ => "Not checked"
    };

    /// <summary>The line beneath the Checked column's chip; null when there is nothing to explain.</summary>
    public static string? ReasonText(GeneratedValueParticipantReason reason) => reason switch
    {
        GeneratedValueParticipantReason.ConnectorCannotProbe => "This Connector can't probe its target.",
        GeneratedValueParticipantReason.AttributeNotProbed => "JIM doesn't probe this attribute: its values only need to be unique within their container.",
        GeneratedValueParticipantReason.NotTextValue => "Only text values are probed.",
        GeneratedValueParticipantReason.ExportedThroughExpression =>
            "Exported through an expression, so the value here differs from the generated one. JIM doesn't check it; if it's already in use here, the export fails.",
        GeneratedValueParticipantReason.CombinedWithOtherSources =>
            "Exported combined with other values, so the value here differs from the generated one. JIM doesn't check it; if it's already in use here, the export fails.",
        GeneratedValueParticipantReason.Excluded => "Excluded: values already in use here don't stop JIM choosing them.",
        _ => null
    };

    /// <summary>
    /// The Sync Preview note for one generated value the synchronisation would probe for, split around the value so
    /// the component can set the value as code.
    /// </summary>
    public static (string BeforeValue, string Value, string AfterValue) ProbeNote(SyncPreviewGeneratedValueProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var names = probe.ConnectedSystemNames;
        var pronoun = names.Count switch
        {
            1 => "it",
            2 => "either",
            _ => "any of them"
        };

        return (
            $"This preview checks JIM's own records only. When the synchronisation runs, JIM also probes {DerivedFlowWording.JoinNames(names)}. " +
            $"If {pronoun} already has an account using ",
            probe.Value,
            " (one JIM doesn't import), the synchronisation will generate a different value instead.");
    }
}
