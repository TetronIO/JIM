// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One rendered Flow view connector: a cubic bezier elbow path and the terminal dot at its target
/// end. Coordinates are pre-formatted with the invariant culture, ready for SVG attributes.
/// </summary>
/// <param name="PathData">The SVG path data ("M x1 y1 C mx y1, mx y2, x2 y2").</param>
/// <param name="DotX">Terminal dot centre x, invariant-formatted.</param>
/// <param name="DotY">Terminal dot centre y, invariant-formatted.</param>
public sealed record CausalityFlowConnector(string PathData, string DotX, string DotY);
