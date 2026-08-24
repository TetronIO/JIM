// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One column of the lineage canvas (#1495): a side of the story rather than a single object. A
/// story has at most four, whatever it involves: the source side's records, the Identity, the
/// target side's records, and the trailing column for hops the builder cannot place.
/// </summary>
/// <remarks>
/// Objects on the same side share a column because the horizontal axis means "one hop further along
/// the causal chain", and sibling records all sit at the same hop: the builder returns no
/// relationship between two of them, so a column each spent a track and a gutter on a relationship
/// it had already ruled out, and the canvas widened without bound as a deployment gained Connected
/// Systems. Each object encloses its own events so a shared column still reads as separate stories.
/// </remarks>
public sealed class CausalityLineageColumn
{
    /// <summary>
    /// What kind of object this column holds. Uniform within a column: the sides are records and the
    /// middle is the Identity.
    /// </summary>
    public CausalityLineageColumnKind Kind { get; init; }

    /// <summary>
    /// The objects stacked in this column, in display order: the page's own record leads its side,
    /// and the rest follow in the order the story reached them.
    /// </summary>
    public required IReadOnlyList<CausalityLineageObject> Objects { get; init; }

    /// <summary>
    /// Whether any of this run's own events landed on any object in this column.
    /// </summary>
    public bool IsLit => Objects.Any(o => o.IsLit);
}
