// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// What an Attribute Flow would do if one of its inputs had no value (#2055): the tester runs the flow again with that
/// input absent and the others as given, so the Missing Input Behaviour's effect is visible before an object without
/// the value arrives.
/// </summary>
/// <param name="Accessor">The input made absent, as the Expression writes it, for example <c>cs["sn"]</c>.</param>
/// <param name="Evaluation">The outcome with that input absent.</param>
public sealed record AttributeFlowAbsentInputOutcome(string Accessor, AttributeFlowEvaluation Evaluation);
