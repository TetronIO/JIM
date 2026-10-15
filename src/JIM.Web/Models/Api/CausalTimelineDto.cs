// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// One page of an object's causal timeline: every Run Profile Execution Item about the object, newest first, each with
/// its nearest recorded cause.
/// </summary>
public class CausalTimelineDto : PaginatedResponse<CausalTimelineRowDto>
{
    /// <summary>
    /// The object the timeline is about, live or deleted.
    /// </summary>
    public CausalObjectHeaderDto Object { get; set; } = null!;

    /// <summary>
    /// For a Metaverse Object: true where this deployment records no Metaverse Object changes, so the timeline holds
    /// only what the object's joined Connected System Objects recorded.
    /// </summary>
    public bool MetaverseObjectChangesNotRecorded { get; set; }

    /// <summary>
    /// Maps a timeline page to its API shape. The header must be present: an object with none does not exist.
    /// </summary>
    public static CausalTimelineDto FromModel(CausalTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Header == null)
            throw new ArgumentException("A causal timeline without an object header has no object to describe.", nameof(timeline));

        return new CausalTimelineDto
        {
            Object = CausalObjectHeaderDto.FromModel(timeline.Header),
            Items = timeline.Rows.Select(CausalTimelineRowDto.FromModel).ToList(),
            TotalCount = timeline.TotalCount,
            Page = timeline.Page,
            PageSize = timeline.PageSize,
            MetaverseObjectChangesNotRecorded = timeline.MetaverseObjectChangesNotRecorded
        };
    }
}
