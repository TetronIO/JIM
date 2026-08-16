// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Something in Advanced Mode Container Scope text that stops it being applied, tied to the line that caused it.
/// </summary>
/// <param name="LineNumber">The 1-based line the problem is on.</param>
/// <param name="Message">
/// What is wrong and what to do about it. These are read by an administrator mid-edit, so they name the path or
/// directive at fault rather than describing the rule in the abstract.
/// </param>
public sealed record ContainerScopeTextError(int LineNumber, string Message);
