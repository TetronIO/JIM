// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The runs that staged a Pending Export (#2100), newest first, each with its nearest recorded causes: what the Pending
/// Export page's Queued by panel lists.
/// </summary>
public class CausalQueuedBy
{
    public Guid PendingExportId { get; init; }

    /// <summary>
    /// The staging runs whose items are still retained, newest first. Empty where the Pending Export names no run, which
    /// is what an export staged before runs were recorded on it carries.
    /// </summary>
    public List<CausalTimelineRow> Rows { get; init; } = [];

    /// <summary>
    /// How many staging runs the Pending Export names whose items history retention has since removed.
    /// </summary>
    public int NotRetainedCount { get; init; }

    /// <summary>
    /// Whether the Pending Export names as many runs as it keeps, so earlier ones may have been dropped to make room.
    /// </summary>
    public bool EarlierRunsMayBeOmitted { get; init; }
}
