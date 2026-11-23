// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// What the portal says about where a generated value is checked for availability (Unique Value Generation, #242,
/// release 3), in one place: the "Checked for availability in" panel on the generated Attribute Flow form, and the Sync
/// Preview note about probing. Plain strings, rendered through Blazor's encoder, because Connected System names and
/// generated values are administrator data. Written in the viewer's language (#3030): the words come from
/// <c>GeneratedValueText</c>.
/// </summary>
public static class GeneratedValueAvailabilityWording
{
    /// <summary>The panel's explanation, under its title.</summary>
    public static string PanelHelperText => GeneratedValueText.AvailabilityPanelHelper;

    /// <summary>The panel's text when no export Synchronisation Rule sends the value anywhere.</summary>
    public static string NoParticipantsText => GeneratedValueText.AvailabilityNoParticipants;

    /// <summary>The Checked column's chip.</summary>
    public static string CheckLabel(GeneratedValueParticipantCheck check) => check switch
    {
        GeneratedValueParticipantCheck.JimRecordsAndProbe => GeneratedValueText.AvailabilityCheckRecordsAndProbe,
        GeneratedValueParticipantCheck.JimRecordsOnly => GeneratedValueText.AvailabilityCheckRecordsOnly,
        _ => GeneratedValueText.AvailabilityNotChecked
    };

    /// <summary>The line beneath the Checked column's chip; null when there is nothing to explain.</summary>
    public static string? ReasonText(GeneratedValueParticipantReason reason) => reason switch
    {
        GeneratedValueParticipantReason.ConnectorCannotProbe => GeneratedValueText.AvailabilityReasonCannotProbe,
        GeneratedValueParticipantReason.AttributeNotProbed => GeneratedValueText.AvailabilityReasonAttributeNotProbed,
        GeneratedValueParticipantReason.NotTextValue => GeneratedValueText.AvailabilityReasonNotText,
        GeneratedValueParticipantReason.ExportedThroughExpression => GeneratedValueText.AvailabilityReasonExpression,
        GeneratedValueParticipantReason.CombinedWithOtherSources => GeneratedValueText.AvailabilityReasonCombined,
        GeneratedValueParticipantReason.Excluded => GeneratedValueText.AvailabilityReasonExcluded,
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
            1 => GeneratedValueText.AvailabilityPronounIt,
            2 => GeneratedValueText.AvailabilityPronounEither,
            _ => GeneratedValueText.AvailabilityPronounAny
        };
        var joinedNames = DerivedFlowWording.JoinNames(names);

        // A value built from the generated value (Generated Value Forms, #2023): a collision on it makes JIM draw another
        // value for the attribute it is built from, so the note names both.
        if (probe.BuiltFromAttributeName != null)
        {
            return (
                string.Format(CultureInfo.CurrentCulture, GeneratedValueText.SyncProbeBuiltBefore,
                    joinedNames, probe.AttributeName, probe.BuiltFromAttributeName, pronoun),
                probe.Value,
                string.Format(CultureInfo.CurrentCulture, GeneratedValueText.SyncProbeBuiltAfter, probe.BuiltFromAttributeName));
        }

        return (
            string.Format(CultureInfo.CurrentCulture, GeneratedValueText.SyncProbeBefore, joinedNames, pronoun),
            probe.Value,
            GeneratedValueText.SyncProbeAfter);
    }
}
