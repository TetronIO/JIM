// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The downward causal walk for one Run Profile Execution Item (#1811): what the changes it describes went on to
/// cause, and what those caused, as far as the read bounds allow. The counterpart of Phase 1's
/// <see cref="CausalChain"/>.
/// </summary>
/// <remarks>
/// Every branch ends with a named state: in a cohort's <see cref="CausalConsequenceCohort.Ending"/>, in the counts of
/// members that led nowhere further, or as <see cref="CausalConsequenceCohort.IsFurtherThanShown"/>. Consequences can
/// still be happening, so the walk is a reading taken at <see cref="CheckedAt"/>, and says whether anything is still
/// waiting.
/// </remarks>
public class CausalConsequences
{
    /// <summary>
    /// The Run Profile Execution Item the walk started from.
    /// </summary>
    public Guid RunProfileExecutionItemId { get; init; }

    /// <summary>
    /// What the item directly caused, grouped into cohorts.
    /// </summary>
    public List<CausalConsequenceCohort> Cohorts { get; init; } = [];

    /// <summary>
    /// Why the item itself led nowhere, where it did: an import waiting for a Synchronisation, one that needed nothing
    /// doing, or an item with no further consequences.
    /// </summary>
    public List<CausalConsequenceEndingCount> Endings { get; init; } = [];

    /// <summary>
    /// True when at least one branch stopped at the depth bound rather than at a real end.
    /// </summary>
    public bool IsTruncatedByDepth { get; init; }

    /// <summary>
    /// True when any branch is still waiting on something that will happen without anyone acting: a Synchronisation,
    /// an export, a retry, a confirming import or a grace period. Reading again later can show more.
    /// </summary>
    public bool HasWaitingBranches { get; init; }

    /// <summary>
    /// When the walk was read, in UTC.
    /// </summary>
    public DateTime CheckedAt { get; init; }
}
