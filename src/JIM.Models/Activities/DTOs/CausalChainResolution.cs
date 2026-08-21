// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// Why the upward walk stopped at a given cause (#1223). Every terminal state is named, because the three
/// reasons a chain ends mean entirely different things to the person reading it and an absence cannot
/// distinguish them.
/// </summary>
/// <remarks>
/// Not a persisted enum: computed per read, so it carries no ordinal obligation.
/// </remarks>
public enum CausalChainResolution
{
    /// <summary>
    /// The causing record was found and the walk continued through it. Any further causes are in
    /// <c>Causes</c>.
    /// </summary>
    Resolved = 0,

    /// <summary>
    /// The causing record exists and nothing caused it: a genuine root. The chain is complete here, and the UI
    /// should say so rather than implying more is hidden.
    /// </summary>
    NoFurtherCauses = 1,

    /// <summary>
    /// The causing record has aged out of history. Expected rather than exceptional: causes are always older
    /// than their effects, so once a deployment has been live longer than one retention window this is the
    /// normal end of a long chain, and should be styled as calm and expected rather than as an error. The
    /// cause is still named from the edge's own snapshot, so the chain says what was lost.
    /// </summary>
    CauseNotRetained = 2,

    /// <summary>
    /// The walk hit its depth bound with causes still to follow. The distinction from
    /// <see cref="NoFurtherCauses"/> matters: one means "this is the whole story", the other means "there is
    /// more".
    /// </summary>
    DepthLimitReached = 3
}
