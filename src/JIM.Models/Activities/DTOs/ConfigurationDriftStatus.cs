// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// Whether a Connected System's configuration has changed in a way that affects synchronisation outcomes since its
/// last Full Synchronisation, and therefore whether the administrator should be told that a re-run is needed for the
/// configuration to take effect.
///
/// Only <see cref="ConfigurationChangeClass.SyncAffecting"/> and <see cref="ConfigurationChangeClass.Destructive"/>
/// changes count: a rename changes nothing about what synchronisation does, so it must not raise the indicator and
/// train administrators to ignore it.
/// </summary>
public class ConfigurationDriftStatus
{
    /// <summary>
    /// The Connected System this status describes.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// True when at least one Sync-affecting or Destructive configuration change affecting this system was recorded
    /// after the reference point (the start of the last completed Full Synchronisation). False when the configuration
    /// is settled, and also false whenever the answer is not knowable: check <see cref="IsDeterminable"/> before
    /// presenting this as "no changes pending".
    /// </summary>
    public bool HasPendingChanges { get; init; }

    /// <summary>
    /// True when this system has never completed a Full Synchronisation, so no configuration has ever been applied in
    /// full. Distinct from <see cref="HasPendingChanges"/>: there is no reference point to compare against, so the
    /// surfaces word this case differently rather than claiming changes are pending.
    /// </summary>
    public bool NeverFullySynchronised { get; init; }

    /// <summary>
    /// True when configuration change tracking is switched off, so JIM has no record of what changed and drift cannot
    /// be determined. Reported honestly rather than as "no changes pending": a silent false negative here would tell
    /// an administrator their configuration is live when it is not.
    /// </summary>
    public bool TrackingDisabled { get; init; }

    /// <summary>
    /// True when the drift question has a meaningful answer, i.e. tracking is on and there is a Full Synchronisation
    /// to compare against.
    /// </summary>
    public bool IsDeterminable => !TrackingDisabled && !NeverFullySynchronised;

    /// <summary>
    /// When the last completed Full Synchronisation for this system started. This is deliberately the run's start and
    /// not its completion: a change made while a long run was in flight may not have been picked up by it, so counting
    /// it as pending errs towards prompting an unnecessary re-run rather than hiding a real one.
    /// Null when <see cref="NeverFullySynchronised"/>.
    /// </summary>
    public DateTime? LastFullSynchronisation { get; init; }

    /// <summary>
    /// When the most recent qualifying change was recorded. Null when there are none.
    /// </summary>
    public DateTime? MostRecentChange { get; init; }

    /// <summary>
    /// How many qualifying changes were recorded since the reference point.
    /// </summary>
    public int ChangeCount { get; init; }

    /// <summary>
    /// The highest class among the qualifying changes, so a surface can distinguish "configuration needs applying"
    /// from "a destructive change is waiting to be applied". <see cref="ConfigurationChangeClass.NotClassified"/>
    /// when there are none.
    /// </summary>
    public ConfigurationChangeClass HighestChangeClass { get; init; }
}
