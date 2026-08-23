// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The upward causal walk for one Run Profile Execution Item: what caused the changes it describes, and what
/// caused those, as far back as retention and the depth bound allow (#1223).
/// </summary>
/// <remarks>
/// The shape is a walk over <b>cohorts</b>, not a linear chain. At each level the causes are grouped by their
/// attribution tuple, so ten objects deleted for the same reason on the same Connected System read as one
/// statement carrying a count rather than ten near-identical hops. A cohort of one is the degenerate case and
/// renders as a plain hop, which is why the common single-cause item still looks like a simple chain. A level
/// producing two or more cohorts is a genuine fork, and is kept as one: two root causes converging on one
/// effect is the signal an administrator most needs, and flattening it would hide exactly that.
/// </remarks>
public class CausalChain
{
    /// <summary>
    /// The Run Profile Execution Item the walk started from.
    /// </summary>
    public Guid RunProfileExecutionItemId { get; init; }

    /// <summary>
    /// The direct causes of this item, grouped into cohorts. Empty when nothing caused it, which is the
    /// ordinary case: most items describe a change with a local explanation.
    /// </summary>
    public List<CausalChainCohort> Cohorts { get; init; } = [];

    /// <summary>
    /// True when at least one branch stopped at the depth bound rather than at a real end. Distinguishes "this
    /// is the whole story" from "there is more, bounded for your protection", which the UI must not conflate.
    /// </summary>
    public bool IsTruncatedByDepth { get; init; }
}
