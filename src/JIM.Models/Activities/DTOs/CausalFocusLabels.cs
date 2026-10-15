// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// What the Causality Explorer's trail (#2100) names its foci by, read for the whole trail at once: each item with its
/// run, and each object live or deleted. A focus absent from both is no longer retained.
/// </summary>
public class CausalFocusLabels
{
    public Dictionary<Guid, CausalTimelineEntry> Items { get; init; } = [];

    public Dictionary<CausalTimelineSubject, CausalObjectHeader> Objects { get; init; } = [];
}
