// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One consequence within a cohort: an execution item, an outstanding Pending Export, or a pending Metaverse Object
/// deletion.
/// </summary>
public class CausalConsequenceMemberDto
{
    /// <summary>
    /// The execution item that recorded the consequence, where it is one.
    /// </summary>
    public Guid? RunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The outstanding Pending Export, for an OutstandingPendingExport cohort.
    /// </summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>
    /// The Metaverse Object whose deletion is pending, for a ScheduledDeletion cohort.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// How the consequence's object was named when it was recorded.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// When the consequence happened, or for outstanding work when it was queued or scheduled, in UTC.
    /// </summary>
    public DateTime Occurred { get; set; }

    /// <summary>
    /// When outstanding work is next due, in UTC: a retry time, or when a scheduled deletion becomes eligible.
    /// </summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// Maps one consequence.
    /// </summary>
    public static CausalConsequenceMemberDto FromModel(CausalConsequenceMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new CausalConsequenceMemberDto
        {
            RunProfileExecutionItemId = member.RunProfileExecutionItemId,
            PendingExportId = member.PendingExportId,
            MetaverseObjectId = member.MetaverseObjectId,
            DisplayName = member.DisplayName,
            Occurred = member.Occurred,
            DueAt = member.DueAt
        };
    }
}
