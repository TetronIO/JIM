// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Web.Localisation;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// The lines of the attribute editor's impact report for a change of contribution mode (#3059): one per kind of effect
/// the change has on the values Metaverse Objects hold, or one saying nothing changes. Kept apart from the dialog's
/// markup because which lines appear is logic.
/// </summary>
public static class ContributionModeImpactText
{
    /// <summary>The report's lines, in the reader's language.</summary>
    public static IReadOnlyList<string> Lines(ContributionModeImpact impact)
    {
        ArgumentNullException.ThrowIfNull(impact);

        var lines = new List<string>();
        if (impact.ObjectsLosingValues > 0)
        {
            lines.Add(impact.ValuesNoLongerSupplied > 0
                ? string.Format(CultureInfo.CurrentCulture,
                    PluralRules.Choose(impact.ObjectsLosingValues, MetaverseAttributeText.ImpactLosingWithValuesOne, MetaverseAttributeText.ImpactLosingWithValuesOther),
                    impact.ObjectsLosingValues.ToString("N0", CultureInfo.CurrentCulture), impact.ValuesNoLongerSupplied.ToString("N0", CultureInfo.CurrentCulture))
                : string.Format(CultureInfo.CurrentCulture,
                    PluralRules.Choose(impact.ObjectsLosingValues, MetaverseAttributeText.ImpactLosingOne, MetaverseAttributeText.ImpactLosingOther),
                    impact.ObjectsLosingValues.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (impact.ObjectsKeepingOneContributor > 0)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture,
                PluralRules.Choose(impact.ObjectsKeepingOneContributor, MetaverseAttributeText.ImpactKeepingOneOne, MetaverseAttributeText.ImpactKeepingOneOther),
                impact.ObjectsKeepingOneContributor.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (impact.ObjectsGainingValues > 0)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture,
                PluralRules.Choose(impact.ObjectsGainingValues, MetaverseAttributeText.ImpactGainingOne, MetaverseAttributeText.ImpactGainingOther),
                impact.ObjectsGainingValues.ToString("N0", CultureInfo.CurrentCulture)));
        }

        if (lines.Count == 0)
            lines.Add(MetaverseAttributeText.ImpactNone);
        return lines;
    }
}
