// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// Whether anything has happened since a preview started that could change its answer, and what last did (#134).
/// A preview is a statement about the data and configuration as they stood; an administrator about to act on one
/// needs to know when that statement has been overtaken, and which way, because the remedy reads differently.
/// </summary>
/// <param name="DataChange">
/// The latest run (import, synchronisation or export), housekeeping, or data-moving Connected System or Synchronisation
/// Rule operation recorded since, or null when there has been none.
/// </param>
/// <param name="ConfigurationChange">
/// The latest configuration change since that can change synchronisation outcomes (classified Sync-affecting or
/// Destructive, or a new Synchronisation Rule), or null when there has been none.
/// </param>
public sealed record ConfigurationChangePreviewStaleness(PreviewOvertakingActivity? DataChange, PreviewOvertakingActivity? ConfigurationChange)
{
    /// <summary>When data last moved since the preview started, or null when it has not.</summary>
    public DateTime? DataChangedAt => DataChange?.Created;

    /// <summary>When configuration last changed since the preview started, or null when it has not.</summary>
    public DateTime? ConfigurationChangedAt => ConfigurationChange?.Created;

    /// <summary>True when either kind of change has happened since the preview started.</summary>
    public bool IsStale => DataChange != null || ConfigurationChange != null;

    /// <summary>When the preview was last overtaken: the later of the two kinds of change, or null when it is current.</summary>
    public DateTime? OvertakenAt => (ConfigurationChangedAt is null || DataChangedAt > ConfigurationChangedAt) ? DataChangedAt : ConfigurationChangedAt;

    /// <summary>
    /// What last overtook the preview, one clause per kind ("Run Profile 'Delta Import' ran on Connected System 'HR
    /// Import', and Synchronisation Rule 'HR Users' was updated"), or null when it is current (#2022).
    /// </summary>
    public string? Describe() => (DataChange, ConfigurationChange) switch
    {
        ({ } data, { } configuration) => $"{data.Describe()}, and {configuration.Describe()}",
        ({ } data, null) => data.Describe(),
        (null, { } configuration) => configuration.Describe(),
        _ => null
    };
}
