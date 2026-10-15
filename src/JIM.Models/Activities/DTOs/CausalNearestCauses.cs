// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// An execution item's nearest recorded causes (#2100): the first level of the upward walk for that item, as one row of
/// a Causality Explorer timeline or of a Pending Export's Queued by panel words it in a line.
/// </summary>
/// <remarks>
/// The cohorts are the walk's own (<see cref="CausalChainCohort"/>), grouped exactly as the item page groups them, so
/// the line a row reads can never disagree with the Lineage the row opens. A row has room for a few, so only the first
/// <see cref="MaxCohorts"/> are carried, with the total beside them.
/// </remarks>
public class CausalNearestCauses
{
    /// <summary>
    /// How many of an item's cohorts a row carries.
    /// </summary>
    public const int MaxCohorts = 3;

    public Guid RunProfileExecutionItemId { get; init; }

    /// <summary>
    /// The item's first cohorts, in the walk's order, at most <see cref="MaxCohorts"/>.
    /// </summary>
    public List<CausalChainCohort> Cohorts { get; init; } = [];

    /// <summary>
    /// How many cohorts the item has in all.
    /// </summary>
    public int TotalCohortCount { get; init; }

    /// <summary>
    /// How the item's causes end: <see cref="CausalChainResolution.Resolved"/> where it has any; otherwise
    /// <see cref="CausalChainResolution.NoFurtherCauses"/> where nothing earlier was recorded, or
    /// <see cref="CausalChainResolution.CauseNotRetained"/> where a synchronisation's record has been deleted and the
    /// import that fed it can no longer be found, so the story was cut short rather than complete.
    /// </summary>
    public CausalChainResolution Resolution { get; init; }
}
