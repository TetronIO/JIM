// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// One step of the evaluation order a <see cref="DerivedFlowAnalysis"/> shows (#1750): the Metaverse attributes, of
/// those the analysed flow depends on, that are settled at this step.
/// </summary>
/// <param name="Step">The step number, from 1. Step 1 is the ordinary pass; step N + 1 evaluates the derived flows
/// whose inputs are all settled by step N.</param>
/// <param name="AttributeNames">The attributes settled at this step, ordered by name.</param>
public sealed record DerivedFlowAnalysisStep(int Step, IReadOnlyList<string> AttributeNames);
