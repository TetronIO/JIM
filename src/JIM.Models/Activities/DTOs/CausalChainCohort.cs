// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A group of causes that share an attribution tuple, and therefore say the same thing about why an effect
/// happened: "10 objects were removed because their authoritative source disconnected" (#1223).
/// </summary>
/// <remarks>
/// Grouping is per <b>effect outcome</b>, not merely per item. An item carrying several outcomes has several
/// independent stories, and merging their causes into one cohort would attribute a removal on one outcome to a
/// cause belonging to another. This is the resolution of the PRD's edge-granularity question, and it is why
/// <see cref="EffectSyncOutcomeId"/> is part of the key rather than incidental detail.
///
/// The reason is a <see cref="CausalReasonCode"/> rather than the sentence shown on screen. Grouping on prose
/// would be redundant with the Connected System name the tuple already carries, and would change silently
/// whenever the wording changed; the sentence is derived at render time from the code plus these snapshots.
/// </remarks>
public class CausalChainCohort
{
    /// <summary>
    /// The outcome on the effect that this cohort explains, where the causes named one. Null where the causes
    /// attach to the item as a whole.
    /// </summary>
    public Guid? EffectSyncOutcomeId { get; init; }

    /// <summary>
    /// Which cascade seam these causes sit on.
    /// </summary>
    public CausalEdgeType EdgeType { get; init; }

    /// <summary>
    /// Why the causes produced the effect, as a code. The displayed sentence is derived from it.
    /// </summary>
    public CausalReasonCode ReasonCode { get; init; }

    /// <summary>
    /// The Connected System the causes occurred on, where one applies.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// The Connected System's name as it was at the time, which is what the chain must display: the system may
    /// since have been renamed or deleted.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// The Synchronisation Rule responsible, where one applies.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// The Synchronisation Rule's name as it was at the time.
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// The individual causes in this cohort. Always populated, so an expanded cohort can name its members
    /// rather than only counting them.
    /// </summary>
    public List<CausalChainMember> Members { get; init; } = [];

    /// <summary>
    /// How many causes this cohort speaks for. The number an administrator reads first ("4 Groups"), so it is
    /// exposed rather than left for the caller to compute and risk computing differently.
    /// </summary>
    public int MemberCount => Members.Count;
}
