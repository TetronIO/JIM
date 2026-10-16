// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One level of the downward causal walk (#1811), read in one round trip: the cohorts the frontier's items caused,
/// and how many of them caused nothing further, and why.
/// </summary>
public class CausalConsequenceLevel
{
    /// <summary>
    /// The cohorts found, ordered by parent and then by when their earliest member happened.
    /// </summary>
    public List<CausalConsequenceCohortRow> Cohorts { get; init; } = [];

    /// <summary>
    /// The frontier's items that led nowhere further, counted per parent cohort and reason.
    /// </summary>
    public List<CausalConsequenceLeafRow> Leaves { get; init; } = [];
}
