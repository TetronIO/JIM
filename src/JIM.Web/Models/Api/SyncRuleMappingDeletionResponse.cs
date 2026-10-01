// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Returned with 200 OK when an Attribute Flow mapping was deleted: what the deletion affected, so a caller can
/// report it without a second request.
/// </summary>
public class SyncRuleMappingDeletionResponse
{
    /// <summary>
    /// How many Metaverse attribute values the mapping had contributed at decision time. Zero for an export mapping,
    /// which contributes nothing to the Metaverse.
    /// </summary>
    public int AffectedValueCount { get; set; }

    /// <summary>
    /// How many distinct Metaverse Objects held at least one of those values at decision time.
    /// </summary>
    public int AffectedObjectCount { get; set; }

    /// <summary>
    /// True when <c>keepContributedValues</c> was chosen and values were present, so they were kept with no
    /// Synchronisation Rule provenance; false when they are left to be recalled at the next Full Synchronisation of
    /// the contributing system, or there was nothing to keep.
    /// </summary>
    public bool ContributedValuesKept { get; set; }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows the deletion left with a missing input, because an attribute they read,
    /// directly or through other derived attributes, lost its last enabled contributor. The deletion went ahead
    /// regardless; each flow's Missing Input Behaviour now decides what it contributes. Always present; empty when the
    /// Metaverse-Derived Attribute Flows feature is off.
    /// </summary>
    public List<DependentDerivedFlow> DependentDerivedFlows { get; set; } = new();
}
