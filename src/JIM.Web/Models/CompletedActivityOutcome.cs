// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One outcome of a Connected System operation on the Operations page's Completed list: what it counts, how many, and
/// whether the count is of things removed (drawn in the error colour) or merely recorded.
/// </summary>
public sealed record CompletedActivityOutcome(string Label, int Count, bool IsRemoval);
