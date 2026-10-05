// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// Whether anything has happened since a preview started that could change its answer, and when it last did (#134).
/// A preview is a statement about the data and configuration as they stood; an administrator about to act on one
/// needs to know when that statement has been overtaken, and which way, because the remedy reads differently.
/// </summary>
/// <param name="DataChangedAt">
/// The latest run (import, synchronisation or export), Metaverse or Connected System Object edit, or housekeeping
/// recorded since, or null when there has been none.
/// </param>
/// <param name="ConfigurationChangedAt">
/// The latest configuration change since that can change synchronisation outcomes (classified Sync-affecting or
/// Destructive, or a new Synchronisation Rule), or null when there has been none.
/// </param>
public sealed record ConfigurationChangePreviewStaleness(DateTime? DataChangedAt, DateTime? ConfigurationChangedAt)
{
    /// <summary>True when either kind of change has happened since the preview started.</summary>
    public bool IsStale => DataChangedAt.HasValue || ConfigurationChangedAt.HasValue;
}
