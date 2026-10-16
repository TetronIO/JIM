// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One cohort found at one level of the downward causal walk (#1811), counted and grouped in the database: its
/// shared values, its size and span, its first page of members, and the members that carry the walk on.
/// </summary>
public class CausalConsequenceCohortRow
{
    /// <summary>
    /// The cohort's number within its level, which its continuing members carry as their parent.
    /// </summary>
    public int Ordinal { get; init; }

    /// <summary>
    /// The ordinal of the cohort, one level up, whose members caused these.
    /// </summary>
    public int ParentCohortOrdinal { get; init; }

    /// <summary>
    /// What the members have in common.
    /// </summary>
    public required CausalConsequenceCohortTuple Tuple { get; init; }

    /// <summary>
    /// The Connected System's name, as the edge recorded it or as it is named now for a derived hop.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// The Synchronisation Rule's name, as the edge recorded it.
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// The plural of <see cref="CausalConsequenceCohortTuple.ObjectTypeName"/>, where the type curates one.
    /// </summary>
    public string? ObjectTypePluralName { get; init; }

    /// <summary>
    /// The exact number of members, counted in the database however many there are.
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// When the earliest member happened, in UTC.
    /// </summary>
    public DateTime FirstOccurred { get; init; }

    /// <summary>
    /// When the latest member happened, in UTC.
    /// </summary>
    public DateTime LastOccurred { get; init; }

    /// <summary>
    /// The soonest due time among the members' outstanding work, in UTC.
    /// </summary>
    public DateTime? DueAt { get; init; }

    /// <summary>
    /// The first page of members, earliest first and then by name.
    /// </summary>
    public List<CausalConsequenceMember> Members { get; init; } = [];

    /// <summary>
    /// The members whose own consequences the next level reads, earliest first, up to the walk's per-level bound.
    /// Empty for a hop that ends its branch (a confirmation, or outstanding work).
    /// </summary>
    public List<Guid> ContinuingItemIds { get; init; } = [];

    /// <summary>
    /// How many members continue, counted however many there are. More than <see cref="ContinuingItemIds"/> holds
    /// means the bound was reached.
    /// </summary>
    public int ContinuingMemberCount { get; init; }
}
