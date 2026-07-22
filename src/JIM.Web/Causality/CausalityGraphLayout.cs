// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The complete computed Graph view layout: positioned nodes (events plus the synthetic source
/// root), bezier edges and the overall SVG canvas dimensions.
/// </summary>
/// <param name="Nodes">Positioned nodes, events in tree order with the source root last.</param>
/// <param name="Edges">Parent-to-child edges in tree order.</param>
/// <param name="Width">Overall canvas width.</param>
/// <param name="Height">Overall canvas height.</param>
public sealed record CausalityGraphLayout(
    IReadOnlyList<CausalityGraphNode> Nodes,
    IReadOnlyList<CausalityGraphEdge> Edges,
    double Width,
    double Height);
