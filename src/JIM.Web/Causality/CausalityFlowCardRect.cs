// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The measured rectangle of one Flow view card (or Connected System group), relative to the flow
/// canvas. Populated from JavaScript via <c>jimCausality.measure</c>; property names match the
/// camelCase JSON the interop layer produces.
/// </summary>
public sealed class CausalityFlowCardRect
{
    /// <summary>
    /// The card's <c>data-flow-id</c> value (e.g. "src", "evt-0", "sys-1").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Left edge in pixels, relative to the canvas.
    /// </summary>
    public double Left { get; set; }

    /// <summary>
    /// Right edge in pixels, relative to the canvas.
    /// </summary>
    public double Right { get; set; }

    /// <summary>
    /// Top edge in pixels, relative to the canvas.
    /// </summary>
    public double Top { get; set; }

    /// <summary>
    /// Height in pixels.
    /// </summary>
    public double Height { get; set; }

    /// <summary>
    /// Top edge of the card's header row (the element marked <c>data-flow-head</c>) in pixels,
    /// relative to the canvas. Zero alongside a zero <see cref="HeaderHeight"/> when no header was
    /// found.
    /// </summary>
    public double HeaderTop { get; set; }

    /// <summary>
    /// Height of the card's header row in pixels, or zero when no header was found. Connectors
    /// anchor on this row's middle, because it is the row that names the thing being connected and
    /// its height differs by card kind.
    /// </summary>
    public double HeaderHeight { get; set; }
}
