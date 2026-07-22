// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The Flow view canvas measurements returned by <c>jimCausality.measure</c>: the canvas size and
/// the rectangle of every element carrying a <c>data-flow-id</c>, all relative to the canvas so the
/// connector overlay's SVG coordinate space matches directly.
/// </summary>
public sealed class CausalityFlowMeasurements
{
    /// <summary>
    /// Canvas width in pixels.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Canvas height in pixels.
    /// </summary>
    public double Height { get; set; }

    /// <summary>
    /// The measured card rectangles.
    /// </summary>
    public List<CausalityFlowCardRect> Cards { get; set; } = [];
}
