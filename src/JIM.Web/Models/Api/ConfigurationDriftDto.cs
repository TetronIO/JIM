// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of whether a Connected System's configuration has changed in a way that needs a Full
/// Synchronisation to take effect. Only Sync-affecting and Destructive changes are counted, so a rename never
/// registers here.
/// </summary>
public class ConfigurationDriftDto
{
    /// <summary>
    /// True when Sync-affecting or Destructive configuration changes have been recorded since the last completed Full
    /// Synchronisation. Always false when <see cref="IsDeterminable"/> is false, so never read this on its own as
    /// "the configuration is settled".
    /// </summary>
    public bool HasPendingChanges { get; set; }

    /// <summary>
    /// True when the drift question has a meaningful answer: change tracking is on, and there is a completed Full
    /// Synchronisation to compare against.
    /// </summary>
    public bool IsDeterminable { get; set; }

    /// <summary>
    /// True when this Connected System has never completed a Full Synchronisation, so no configuration has ever been
    /// applied in full and there is no reference point to compare against.
    /// </summary>
    public bool NeverFullySynchronised { get; set; }

    /// <summary>
    /// True when configuration change tracking is switched off, so JIM holds no record of what changed and drift
    /// cannot be determined.
    /// </summary>
    public bool TrackingDisabled { get; set; }

    /// <summary>
    /// When the last completed Full Synchronisation started, or null if there has never been one. This is the run's
    /// start rather than its completion, so a change made while a long run was in flight still counts as pending.
    /// </summary>
    public DateTime? LastFullSynchronisation { get; set; }

    /// <summary>
    /// When the most recent qualifying change was recorded, or null if there are none.
    /// </summary>
    public DateTime? MostRecentChange { get; set; }

    /// <summary>
    /// How many qualifying changes have been recorded since the last Full Synchronisation.
    /// </summary>
    public int ChangeCount { get; set; }

    /// <summary>
    /// The highest class among those changes, so a caller can tell a change that alters synchronisation outcomes from
    /// one that can cascade deletions the moment it is applied.
    /// </summary>
    public ConfigurationChangeClass HighestChangeClass { get; set; }

    /// <summary>
    /// Creates a DTO from the application-layer status.
    /// </summary>
    public static ConfigurationDriftDto FromStatus(ConfigurationDriftStatus status)
    {
        return new ConfigurationDriftDto
        {
            HasPendingChanges = status.HasPendingChanges,
            IsDeterminable = status.IsDeterminable,
            NeverFullySynchronised = status.NeverFullySynchronised,
            TrackingDisabled = status.TrackingDisabled,
            LastFullSynchronisation = status.LastFullSynchronisation,
            MostRecentChange = status.MostRecentChange,
            ChangeCount = status.ChangeCount,
            HighestChangeClass = status.HighestChangeClass
        };
    }
}
