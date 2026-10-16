// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One cause within a cohort, and what caused it in turn.
/// </summary>
public class CausalChainMemberDto
{
    /// <summary>
    /// The execution item that recorded the cause, where one did.
    /// </summary>
    public Guid? RunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The Metaverse Object that was the cause, where the cause is identified by object.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System Object that was the cause, where the cause is identified by object.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Pending Export whose execution was the cause.
    /// </summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>
    /// How the cause was named when it was recorded.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// When the cause happened, in UTC.
    /// </summary>
    public DateTime Occurred { get; set; }

    /// <summary>
    /// Why the walk stopped here, or Resolved where the cause's own causes follow.
    /// </summary>
    public CausalChainResolution Resolution { get; set; }

    /// <summary>
    /// What caused this cause, grouped into cohorts.
    /// </summary>
    public List<CausalChainCohortDto> Causes { get; set; } = [];

    /// <summary>
    /// Maps one cause of the upward walk.
    /// </summary>
    public static CausalChainMemberDto FromModel(CausalChainMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return new CausalChainMemberDto
        {
            RunProfileExecutionItemId = member.RunProfileExecutionItemId,
            MetaverseObjectId = member.MetaverseObjectId,
            ConnectedSystemObjectId = member.ConnectedSystemObjectId,
            PendingExportId = member.PendingExportId,
            DisplayName = member.DisplayName,
            Occurred = member.Occurred,
            Resolution = member.Resolution,
            Causes = member.Causes.Select(CausalChainCohortDto.FromModel).ToList()
        };
    }
}
