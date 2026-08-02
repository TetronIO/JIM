// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// What JIM knows about a configuration change *before* it is saved: which properties are actually changing, how
/// consequential the change is overall, and what the destructive parts of it will do. This is what the save-time
/// acknowledgement is composed from, so that a rename saves silently whilst the dangerous toggle sitting on the same
/// page demands consent.
///
/// The baseline is the object's latest captured configuration snapshot, which is the same baseline the post-save
/// capture diffs against. That is deliberate: the acknowledgement an administrator consented to and the class recorded
/// in the change history are then computed from the same comparison, and cannot disagree.
/// </summary>
public class ConfigurationChangePreflight
{
    /// <summary>
    /// The highest class among the properties that actually changed, which is how the change as a whole is judged.
    /// <see cref="ConfigurationChangeClass.NotClassified"/> when nothing changed, or when no judgement could be made.
    /// </summary>
    public ConfigurationChangeClass HighestClass { get; init; }

    /// <summary>
    /// The properties that changed, most consequential first. Cosmetic properties are included so the administrator
    /// sees the whole picture of what they are saving, but they never drive the acknowledgement on their own.
    /// </summary>
    public IReadOnlyList<ConfigurationChangePreflightItem> Items { get; init; } = [];

    /// <summary>
    /// True when JIM had no captured baseline to compare against, so it cannot say what changed. Callers must not
    /// read this as "nothing consequential changed": treat it as "unknown" and stay silent rather than guess, exactly
    /// as the changed-since indicator does when tracking is switched off. Happens when configuration change tracking
    /// is disabled, or on the first save of an object that predates change capture.
    /// </summary>
    public bool BaselineUnavailable { get; init; }

    /// <summary>
    /// True when the administrator should be asked to acknowledge this change before it is saved, i.e. it alters
    /// synchronisation outcomes. False for cosmetic-only saves, no-op saves, and creates.
    /// </summary>
    public bool RequiresAcknowledgement =>
        HighestClass is ConfigurationChangeClass.SyncAffecting or ConfigurationChangeClass.Destructive;

    /// <summary>
    /// True when at least one changing property can cascade deletions or mass deprovisioning, which is what
    /// escalates the acknowledgement from advisory to a consent gate.
    /// </summary>
    public bool IsDestructive => HighestClass == ConfigurationChangeClass.Destructive;

    /// <summary>
    /// The subset of <see cref="Items"/> that carry a stated consequence, i.e. the destructive ones. This is what the
    /// acknowledgement leads with.
    /// </summary>
    public IEnumerable<ConfigurationChangePreflightItem> DestructiveItems =>
        Items.Where(i => i.Class == ConfigurationChangeClass.Destructive);

    /// <summary>
    /// The result for a save that needs no acknowledgement: nothing changed, nothing is known to have changed, or the
    /// change is a create (which has no prior state, so nothing existing is at risk).
    /// </summary>
    public static ConfigurationChangePreflight None { get; } = new();

    /// <summary>
    /// The result for a save JIM cannot judge, because no captured baseline exists to compare against.
    /// </summary>
    public static ConfigurationChangePreflight Unknown { get; } = new() { BaselineUnavailable = true };
}
