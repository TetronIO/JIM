// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Application.Services;

/// <summary>
/// Decides whether a saved configuration change must be recorded as awaiting synchronisation, and how the record reads
/// (#2615). A pure function of what configuration change capture reported: the part of the feature with a judgement in
/// it, kept apart from the saves that ask so each rule is testable on its own.
/// <para>
/// The decision is graded by the existing classification (<see cref="ConfigurationChangeClassifier"/>), not a second
/// scheme: a Sync-affecting or Destructive change is recorded, a Cosmetic one is not. Where JIM cannot grade the change
/// it records it. The two costs are not alike. Processing an Object Type's objects that did not need it costs time;
/// missing a change that did leaves objects wrong until someone happens to run a Full Synchronisation.
/// </para>
/// </summary>
internal static class ConfigurationChangeSynchronisationPolicy
{
    /// <summary>
    /// Whether the change the capture reported must be carried to the objects that already exist.
    /// </summary>
    /// <param name="outcome">What configuration change capture found.</param>
    /// <param name="isInForce">Whether the saved configuration takes part in synchronisation: an enabled rule. It only
    /// matters when there is no earlier version to compare with, and when nothing could be compared.</param>
    internal static bool RequiresSynchronisation(ConfigurationChangeCaptureOutcome outcome, bool isInForce)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Kind switch
        {
            // Identical to the latest version: there is nothing to carry.
            ConfigurationChangeCaptureKind.NoChange => false,

            // Graded. NotClassified is a changed property the classifier has no class for, which is a gap in JIM's
            // tables, not evidence that the change is harmless.
            ConfigurationChangeCaptureKind.Changed => outcome.Class != ConfigurationChangeClass.Cosmetic,

            // A first version: a rule just created reaches objects that already exist, but only if it is switched on. A
            // rule created disabled applies to nothing, and enabling it later is a Sync-affecting change of its own.
            ConfigurationChangeCaptureKind.Created => isInForce,

            // Nothing was compared, so a disabled rule may just have been enabled. Not knowing is a reason to record,
            // whether or not the rule looks to be in force.
            ConfigurationChangeCaptureKind.NotTracked or ConfigurationChangeCaptureKind.Failed => true,

            // A kind added later and not yet considered here: do the safe thing.
            _ => true
        };
    }

    /// <summary>
    /// The text recorded with the change so it still reads once the rule is gone: what was saved, and (where capture
    /// could tell) what changed in it and how consequential that is.
    /// </summary>
    /// <param name="subject">What was saved, as the administrator knows it (a Synchronisation Rule's name).</param>
    /// <param name="outcome">What configuration change capture found.</param>
    /// <param name="isNew">Whether the subject was created by this save, so that "no earlier version to compare with"
    /// can be told apart from "created".</param>
    internal static string Describe(string subject, ConfigurationChangeCaptureOutcome outcome, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Kind switch
        {
            ConfigurationChangeCaptureKind.Changed => $"{subject}: {DescribeSections(outcome)} changed{DescribeClass(outcome.Class)}",
            ConfigurationChangeCaptureKind.Created => isNew
                ? $"{subject}: created"
                : $"{subject}: saved; it had no earlier recorded version to compare with",
            ConfigurationChangeCaptureKind.NotTracked =>
                $"{subject}: saved; configuration change tracking is off, so what changed is unknown",
            ConfigurationChangeCaptureKind.Failed =>
                $"{subject}: saved; what changed is unknown because the change could not be compared with its history",
            _ => $"{subject}: saved"
        };
    }

    private static string DescribeSections(ConfigurationChangeCaptureOutcome outcome) =>
        outcome.ChangedSections.Count == 0 ? "configuration" : string.Join(", ", outcome.ChangedSections);

    private static string DescribeClass(ConfigurationChangeClass changeClass) => changeClass switch
    {
        ConfigurationChangeClass.Cosmetic => " (Cosmetic)",
        ConfigurationChangeClass.SyncAffecting => " (Sync-affecting)",
        ConfigurationChangeClass.Destructive => " (Destructive)",
        _ => string.Empty
    };
}
