// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One positioned node in the Graph view's layered node-link layout: a causality event (or the
/// synthetic source-record root) with its column depth, final canvas coordinates and pre-truncated
/// display strings.
/// </summary>
/// <param name="Id">Stable node id: "src" for the synthetic root, "evt-n" in tree order otherwise.</param>
/// <param name="Depth">Tree depth; the root sits at 0 and each depth maps to one x column.</param>
/// <param name="X">The node group's x translation on the SVG canvas.</param>
/// <param name="Y">The node group's y translation on the SVG canvas.</param>
/// <param name="Title">Truncated node title (plain or technical label per the tech-names flag).</param>
/// <param name="Sub">Truncated sub line: attribute count, first entity chip label or system name.</param>
/// <param name="Tone">Visual tone for the accent bar.</param>
/// <param name="HasAttributeRows">Whether the underlying event carries attribute detail rows.</param>
/// <param name="Event">The underlying causality event; null for the synthetic source root.</param>
public sealed record CausalityGraphNode(
    string Id,
    int Depth,
    double X,
    double Y,
    string Title,
    string Sub,
    CausalityTone Tone,
    bool HasAttributeRows,
    CausalityEvent? Event);
