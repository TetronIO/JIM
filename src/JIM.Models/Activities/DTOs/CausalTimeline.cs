// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One page of an object's causal timeline (#2100): the execution items about it, newest first, each with its nearest
/// recorded causes, and the object they are about.
/// </summary>
public class CausalTimeline
{
    public CausalTimelineSubject Subject { get; init; } = null!;

    /// <summary>
    /// The object, live or deleted; null where neither it nor a deletion record for it exists.
    /// </summary>
    public CausalObjectHeader? Header { get; init; }

    public List<CausalTimelineRow> Rows { get; init; } = [];

    /// <summary>
    /// How many items the timeline holds in all.
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// The page returned, from 1.
    /// </summary>
    public int Page { get; init; }

    public int PageSize { get; init; }

    /// <summary>
    /// For a Metaverse Object, whether this deployment records no Metaverse Object changes, so the timeline holds only
    /// what its joined Connected System Objects recorded and says so rather than implying it is complete.
    /// </summary>
    public bool MetaverseObjectChangesNotRecorded { get; init; }
}
