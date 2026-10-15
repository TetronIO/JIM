// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One row of a Causality Explorer list (#2100): an execution item with its run named, and its nearest recorded causes.
/// </summary>
public class CausalTimelineRow
{
    public CausalTimelineEntry Entry { get; init; } = null!;

    public CausalNearestCauses NearestCauses { get; init; } = null!;
}
