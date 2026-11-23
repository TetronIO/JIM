// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Web.Localisation;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// The words of the "When this takes effect" part of the save acknowledgement (#2615), composed from what the save will do.
/// Kept apart from the dialog's markup because the sentences carry the logic that can be wrong in a way markup cannot:
/// how long is "under a minute", what rounds to "1 minute", when the duration clause is left out because the Connected
/// System has no Full Synchronisation to take a speed from.
/// </summary>
public static class ConfigurationChangeEffectText
{
    /// <summary>
    /// What a Delta or Full Synchronisation does for one Object Type the save makes it process in full: whose
    /// synchronisation, which type, how many objects, and (where there is a last Full Synchronisation to take a speed from)
    /// about how long it takes.
    /// </summary>
    public static string ProcessedInFull(AwaitingSynchronisationEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        return string.Format(CultureInfo.CurrentCulture, ActivityText.ActivityEffectProcessedInFull,
            estimate.ConnectedSystemName, estimate.ObjectTypeName, Objects(estimate.ObjectCount), DurationClause(estimate.EstimatedDuration));
    }

    /// <summary>
    /// What the save leaves in place until that run: where the administrator finds the change, and the way to run it.
    /// </summary>
    public static string UntilThen => ActivityText.ActivityEffectUntilThen;

    /// <summary>
    /// What the save flags an export rule's Metaverse Objects for.
    /// </summary>
    public static string ExportReview(string metaverseObjectTypeName) =>
        string.Format(CultureInfo.CurrentCulture, ActivityText.ActivityEffectExportReview, metaverseObjectTypeName);

    /// <summary>
    /// What is left for a Full Synchronisation, naming what and where.
    /// </summary>
    public static string FullSynchronisation(ConfigurationChangeEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        var what = effect.FullSynchronisationSections.Count == 0
            ? ActivityText.ActivityEffectThisChange
            : string.Format(CultureInfo.CurrentCulture, ActivityText.ActivityEffectChangeTo,
                string.Join(", ", effect.FullSynchronisationSections));
        return string.Format(CultureInfo.CurrentCulture, ActivityText.ActivityEffectFullSyncSentence,
            what, effect.FullSynchronisationConnectedSystemName);
    }

    /// <summary>
    /// The clause stating about how long processing takes, led by its comma: "about 28 minutes at the speed of its last
    /// Full Synchronisation", or "under a minute" for less than one. Empty when there is no estimate, so a Connected System
    /// that has never completed a Full Synchronisation is told how many objects and nothing invented about how long.
    /// </summary>
    public static string DurationClause(TimeSpan? estimatedDuration)
    {
        if (estimatedDuration is not { } duration)
            return string.Empty;

        return string.Format(CultureInfo.CurrentCulture, ActivityText.ActivityEffectDurationClause, Duration(duration));
    }

    /// <summary>
    /// "under a minute" for less than a minute; otherwise "about N minutes" to the nearest whole minute ("about 1 minute"
    /// from a minute to a minute and a half).
    /// </summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
            return ActivityText.ActivityEffectUnderAMinute;

        var minutes = (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        return string.Format(CultureInfo.CurrentCulture,
            PluralRules.Choose(minutes, ActivityText.ActivityEffectAboutMinutesOne, ActivityText.ActivityEffectAboutMinutesOther),
            minutes.ToString("N0", CultureInfo.CurrentCulture));
    }

    private static string Objects(int count) => string.Format(CultureInfo.CurrentCulture,
        PluralRules.Choose(count, ActivityText.ActivityEffectObjectsOne, ActivityText.ActivityEffectObjectsOther),
        count.ToString("N0", CultureInfo.CurrentCulture));
}
