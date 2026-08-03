// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The Flow view canvas measurements returned by <c>jimCausality.measure</c>: the rectangle of
/// every element carrying a <c>data-flow-id</c>, relative to the canvas so the connector overlay's
/// SVG coordinate space (CSS pixels, no viewBox) matches directly.
/// </summary>
public sealed class CausalityFlowMeasurements
{
    /// <summary>
    /// The measured card rectangles.
    /// </summary>
    public List<CausalityFlowCardRect> Cards { get; set; } = [];
}
