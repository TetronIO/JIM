// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Logic;

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
    /// The snapshot key of a Synchronisation Rule's own on/off setting. Matched against the changed top-level keys, so an
    /// "enabled" nested in an Attribute Flow or the initial password is not mistaken for it.
    /// </summary>
    private const string EnabledKey = "enabled";

    /// <summary>
    /// Whether the change the capture reported must be carried to the objects that already exist.
    /// </summary>
    /// <param name="outcome">What configuration change capture found.</param>
    /// <param name="isInForce">Whether the saved configuration takes part in synchronisation: an enabled rule. It matters
    /// when a change was made to a rule that is switched off after the save, when there is no earlier version to compare
    /// with, and when nothing could be compared.</param>
    internal static bool RequiresSynchronisation(ConfigurationChangeCaptureOutcome outcome, bool isInForce)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Kind switch
        {
            // Identical to the latest version: there is nothing to carry.
            ConfigurationChangeCaptureKind.NoChange => false,

            // Graded. NotClassified is a changed property the classifier has no class for, which is a gap in JIM's
            // tables, not evidence that the change is harmless. A rule that is off after the save, and was off before it,
            // does nothing for the objects of its type whatever was edited in it: walking the type would cost a run
            // (28 minutes at 750,000 objects) and change nothing. Switching it off is the exception, because that does
            // change what the existing objects produce, and enabling it later is a change of its own.
            ConfigurationChangeCaptureKind.Changed => outcome.Class != ConfigurationChangeClass.Cosmetic
                                                      && (isInForce || outcome.ChangedKeys.Contains(EnabledKey)),

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
    /// Whether saving a Synchronisation Rule records its Connected System Object Type as awaiting synchronisation. The one
    /// question the rule saves ask, and the one the acknowledgement before them asks, from the same inputs: the rule's
    /// direction, whether it is enabled after the save, and what capture found. Only an import rule records: an export
    /// rule's change reaches existing objects through export evaluation, which runs on every Metaverse Object the import
    /// types' synchronisation reprocesses (plan decision 5), so a second mechanism would be redundant.
    /// </summary>
    /// <param name="rule">The rule as saved (or, before the save, as it is about to be).</param>
    /// <param name="outcome">What configuration change capture found for the save.</param>
    internal static bool RecordsAwaitingSynchronisation(SyncRule rule, ConfigurationChangeCaptureOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Direction == SyncRuleDirection.Import && RequiresSynchronisation(outcome, isInForce: rule.Enabled);
    }

    /// <summary>
    /// Whether saving a Simple Mode Object Matching Rule, or switching the matching mode, records a Connected System Object
    /// Type as awaiting synchronisation: the change must be one that alters what an import produces, and an enabled import
    /// rule must cover the type. Nothing synchronises the objects of any other type, so there is nothing to process, and
    /// enabling an import rule for it later is a change of its own.
    /// </summary>
    /// <param name="outcome">What configuration change capture found for the save.</param>
    /// <param name="enabledImportRuleCoversType">Whether an enabled import rule is on the Object Type.</param>
    internal static bool RecordsAwaitingSynchronisationForSimpleModeType(ConfigurationChangeCaptureOutcome outcome, bool enabledImportRuleCoversType)
    {
        // The Connected System capture compares the whole system, so a Created outcome says nothing about whether the
        // rule is new; the matching rule save itself is what is being recorded, and it is in force.
        return enabledImportRuleCoversType && RequiresSynchronisation(outcome, isInForce: true);
    }

    /// <summary>
    /// The top-level sections of an export rule's snapshot that <see cref="FlagsExportReview"/> reads, through
    /// <see cref="SyncRuleScopeState"/>: whether it is enabled, whether it provisions, its Scoping Criteria and its
    /// Deprovisioning Action. A change to any other section (an Attribute Flow, Enforce state) flags nothing, and so is left
    /// to a Full Synchronisation; the acknowledgement uses this to say which of the sections it names the review carries.
    /// </summary>
    internal static readonly IReadOnlySet<string> ExportReviewSectionKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "enabled",
        "provisionToConnectedSystem",
        "objectScopingCriteriaGroups",
        "outboundDeprovisionAction"
    };

    /// <summary>
    /// Whether saving an export Synchronisation Rule flags the Metaverse Objects of its type for review against every
    /// export rule, which the next synchronisation of any Connected System carries out (#1925, #1970). It does when the
    /// save can have moved objects into or out of the rule's scope, or changed what happens to those that leave it.
    /// Disabling a rule or switching provisioning off flags nothing: neither destroys what already exists. A save that
    /// leaves scope and the Deprovisioning Action alone (a rename, an Attribute Flow edit) flags nothing either.
    /// </summary>
    /// <param name="rule">The rule as saved (or, before the save, as it is about to be).</param>
    /// <param name="isNewRule">Whether the save creates the rule.</param>
    /// <param name="storedState">The rule's scope settings as stored before the save. A state that cannot be read (the rule
    /// vanished between the read and the save) is treated as a change: a needless review costs time, a missed one leaves
    /// objects wrongly provisioned.</param>
    internal static bool FlagsExportReview(SyncRule rule, bool isNewRule, SyncRuleScopeState? storedState)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Direction != SyncRuleDirection.Export || !rule.Enabled)
            return false;

        return isNewRule || storedState == null || storedState.ExportScopeMovedBy(rule) || storedState.DeprovisioningActionChangedBy(rule);
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
