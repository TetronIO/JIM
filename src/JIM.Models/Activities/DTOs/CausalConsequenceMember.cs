// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One consequence within a cohort (#1811): an item, an outstanding Pending Export, or a pending Metaverse Object
/// deletion.
/// </summary>
public class CausalConsequenceMember
{
    /// <summary>
    /// The item that recorded the consequence, where it is one. What the UI links to.
    /// </summary>
    public Guid? RunProfileExecutionItemId { get; init; }

    /// <summary>
    /// The outstanding Pending Export, for an <see cref="CausalConsequenceHopKind.OutstandingPendingExport"/> member.
    /// </summary>
    public Guid? PendingExportId { get; init; }

    /// <summary>
    /// The Metaverse Object whose deletion is pending, for a <see cref="CausalConsequenceHopKind.ScheduledDeletion"/>
    /// member.
    /// </summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>
    /// How the consequence's object was named when it was recorded.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// When the consequence happened (or, for outstanding work, when it was queued or scheduled), in UTC.
    /// </summary>
    public DateTime Occurred { get; init; }

    /// <summary>
    /// When outstanding work is next due, in UTC: a retry time, or when a scheduled deletion becomes eligible.
    /// </summary>
    public DateTime? DueAt { get; init; }
}
