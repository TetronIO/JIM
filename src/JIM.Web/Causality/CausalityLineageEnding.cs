// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Causality;

/// <summary>
/// A chain ending rendered as a quiet footer under the column it closes (#1495). The three terminal
/// states are kept distinct because they mean entirely different things: the whole story, history
/// aged out, and the depth bound.
/// </summary>
/// <param name="Resolution">Why the walk stopped here.</param>
/// <param name="Text">The phrase for it, from <see cref="CausalityCauseWording.Ending"/>.</param>
public sealed record CausalityLineageEnding(CausalChainResolution Resolution, string Text)
{
    /// <summary>
    /// Where the walk stopped at its bound on a single item, the way to carry on from it (#2100), which shows the next
    /// levels from there. Empty for every other ending, and where the walk stopped on a cohort of several, whose members
    /// each carry their own link.
    /// </summary>
    public IReadOnlyList<CausalityLineageContinuation> Continuations { get; init; } = [];
}
