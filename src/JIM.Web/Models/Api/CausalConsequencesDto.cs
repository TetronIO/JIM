// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a Run Profile Execution Item went on to cause, walked downward as cohorts, with every branch ending in a named
/// state (#1811).
/// </summary>
public class CausalConsequencesDto
{
    /// <summary>
    /// The execution item the walk started from.
    /// </summary>
    public Guid RunProfileExecutionItemId { get; set; }

    /// <summary>
    /// What the item directly caused, grouped into cohorts; each cohort carries what its members caused in turn.
    /// </summary>
    public List<CausalConsequenceCohortDto> Cohorts { get; set; } = [];

    /// <summary>
    /// Why the item itself led nowhere, where it did.
    /// </summary>
    public List<CausalConsequenceEndingCountDto> Endings { get; set; } = [];

    /// <summary>
    /// True when at least one branch stopped at the depth bound rather than at a real end.
    /// </summary>
    public bool IsTruncatedByDepth { get; set; }

    /// <summary>
    /// True when a branch is still waiting on something that will happen without anyone acting (a Synchronisation, an
    /// export, a retry, a confirming import or a grace period), so reading again later can show more.
    /// </summary>
    public bool HasWaitingBranches { get; set; }

    /// <summary>
    /// When the walk was read, in UTC.
    /// </summary>
    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// Maps the downward walk to its API shape.
    /// </summary>
    public static CausalConsequencesDto FromModel(CausalConsequences consequences)
    {
        ArgumentNullException.ThrowIfNull(consequences);
        return new CausalConsequencesDto
        {
            RunProfileExecutionItemId = consequences.RunProfileExecutionItemId,
            Cohorts = consequences.Cohorts.Select(CausalConsequenceCohortDto.FromModel).ToList(),
            Endings = consequences.Endings.Select(CausalConsequenceEndingCountDto.FromModel).ToList(),
            IsTruncatedByDepth = consequences.IsTruncatedByDepth,
            HasWaitingBranches = consequences.HasWaitingBranches,
            CheckedAt = consequences.CheckedAt
        };
    }
}
