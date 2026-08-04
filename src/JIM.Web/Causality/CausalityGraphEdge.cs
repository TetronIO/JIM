// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One parent-to-child edge in the Graph view: a cubic bezier from the right centre of the parent
/// node to the left centre of the child node, with the path data pre-formatted in invariant culture.
/// </summary>
/// <param name="FromId">Node id of the parent end.</param>
/// <param name="ToId">Node id of the child end.</param>
/// <param name="PathData">The SVG path "d" attribute value.</param>
public sealed record CausalityGraphEdge(string FromId, string ToId, string PathData);
