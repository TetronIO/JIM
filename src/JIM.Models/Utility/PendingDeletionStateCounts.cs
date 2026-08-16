// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// How the Metaverse Objects awaiting deletion divide between the three states the Pending Deletions page
/// reports. Every object pending deletion falls into exactly one of the three, so they sum to
/// <see cref="Total"/>; the counts are taken across the whole match set rather than a window of it, because a
/// summary computed from the rows on screen under-reports the moment the list is longer than the window.
/// </summary>
public class PendingDeletionStateCounts
{
    /// <summary>Every Metaverse Object awaiting deletion, after any Metaverse Object Type filter.</summary>
    public int Total { get; init; }

    /// <summary>
    /// Objects still connected to at least one Connected System. Their accounts are being deprovisioned; the
    /// object cannot be deleted until the last Connected System Object has gone.
    /// </summary>
    public int Deprovisioning { get; init; }

    /// <summary>
    /// Disconnected objects whose Metaverse Object Type sets a deletion grace period that has not yet elapsed.
    /// </summary>
    public int AwaitingGracePeriod { get; init; }

    /// <summary>
    /// Disconnected objects whose grace period has elapsed, or whose type sets none: housekeeping deletes these
    /// on its next pass.
    /// </summary>
    public int ReadyForDeletion { get; init; }
}
