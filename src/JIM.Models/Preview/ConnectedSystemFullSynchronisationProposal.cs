// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// A proposal to run a Full Synchronisation of a Connected System, as the Full Synchronisation preview adapter
/// receives it (#1530). The system travels as the preview's target, and the configuration previewed is the one
/// already saved, so the proposal carries only how much of the population to evaluate.
/// </summary>
/// <param name="MaxObjects">
/// The most Connected System Objects to evaluate, or null (the default) for every one. A cap is for scripts that
/// want a quick sample of a large system; the preview then says its counts describe the objects it evaluated, not
/// the whole system, because a partial count read as a whole one is how a change gets approved as safe.
/// </param>
public sealed record ConnectedSystemFullSynchronisationProposal(int? MaxObjects = null);
