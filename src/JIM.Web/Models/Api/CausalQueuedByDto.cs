// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// The runs that queued a Pending Export, newest first, each as the execution item that staged it with its nearest
/// recorded cause.
/// </summary>
public class CausalQueuedByDto
{
    /// <summary>
    /// The Pending Export.
    /// </summary>
    public Guid PendingExportId { get; set; }

    /// <summary>
    /// The staging runs still retained, newest first. Empty where the Pending Export names no run, as one queued before
    /// staging runs were recorded does not.
    /// </summary>
    public List<CausalTimelineRowDto> Runs { get; set; } = [];

    /// <summary>
    /// How many staging runs the Pending Export names that history retention has since removed.
    /// </summary>
    public int NotRetainedCount { get; set; }

    /// <summary>
    /// True where the Pending Export names as many runs as it keeps, so earlier ones may have been dropped to make room.
    /// </summary>
    public bool EarlierRunsMayBeOmitted { get; set; }

    /// <summary>
    /// Maps the Queued by read to its API shape.
    /// </summary>
    public static CausalQueuedByDto FromModel(CausalQueuedBy queuedBy)
    {
        ArgumentNullException.ThrowIfNull(queuedBy);
        return new CausalQueuedByDto
        {
            PendingExportId = queuedBy.PendingExportId,
            Runs = queuedBy.Rows.Select(CausalTimelineRowDto.FromModel).ToList(),
            NotRetainedCount = queuedBy.NotRetainedCount,
            EarlierRunsMayBeOmitted = queuedBy.EarlierRunsMayBeOmitted
        };
    }
}
