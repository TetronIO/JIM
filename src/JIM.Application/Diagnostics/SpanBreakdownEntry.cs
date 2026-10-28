// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Diagnostics;

/// <summary>
/// The totals for one path of descendant spans in a <see cref="SpanBreakdown"/>.
/// </summary>
public sealed class SpanBreakdownEntry(string path)
{
    /// <summary>
    /// The span names from the owning span's child down to the descendant, joined by <c>/</c>.
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// How many spans completed at this path.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Their combined duration.
    /// </summary>
    public double TotalMs { get; set; }

    /// <summary>
    /// The longest single one.
    /// </summary>
    public double MaxMs { get; set; }
}
