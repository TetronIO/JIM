// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// What caused a Run Profile Execution Item, walked upward through the recorded causes as cohorts (#1223, #1811).
/// </summary>
public class CausalChainDto
{
    /// <summary>
    /// The execution item the walk started from.
    /// </summary>
    public Guid RunProfileExecutionItemId { get; set; }

    /// <summary>
    /// The item's direct causes, grouped into cohorts; each member carries its own causes in turn. Empty when nothing
    /// caused it, which is the ordinary case.
    /// </summary>
    public List<CausalChainCohortDto> Cohorts { get; set; } = [];

    /// <summary>
    /// True when at least one branch stopped at the depth bound rather than at a real end.
    /// </summary>
    public bool IsTruncatedByDepth { get; set; }

    /// <summary>
    /// Maps the upward walk to its API shape.
    /// </summary>
    public static CausalChainDto FromModel(CausalChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return new CausalChainDto
        {
            RunProfileExecutionItemId = chain.RunProfileExecutionItemId,
            Cohorts = chain.Cohorts.Select(CausalChainCohortDto.FromModel).ToList(),
            IsTruncatedByDepth = chain.IsTruncatedByDepth
        };
    }
}
