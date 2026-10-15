// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// What a Generated Value's base expression would give generation if one of its inputs had no value, the others as
/// given (#2055).
/// </summary>
/// <param name="Accessor">The input made absent, such as <c>cs["sn"]</c>.</param>
/// <param name="GeneratedBase">The base expression's outcome with that input absent.</param>
public sealed record GeneratedBaseAbsentInputOutcome(string Accessor, GeneratedBaseEvaluation GeneratedBase);
