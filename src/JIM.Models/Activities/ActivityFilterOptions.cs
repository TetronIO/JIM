// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// Contains the available filter options for worker task activity queries.
/// Populated from distinct values in the activity history.
/// </summary>
public class ActivityFilterOptions
{
    /// <summary>
    /// Distinct Connected System names (from TargetContext).
    /// </summary>
    public List<string> ConnectedSystems { get; set; } = [];

    /// <summary>
    /// Distinct Run Profile names (from TargetName).
    /// </summary>
    public List<string> RunProfiles { get; set; } = [];
}
