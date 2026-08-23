// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The object lineage for one Run Profile Execution Item (#1495): the item's causal story projected
/// onto the objects it involves. Columns are the objects (each record, the Identity), ordered
/// source to Identity to target(s); every causally relevant event is a card on the column of the
/// object it happened to, and adjacent columns are joined by labelled relationships.
/// </summary>
public sealed class CausalityLineageModel
{
    /// <summary>
    /// The object columns in display order: source-side records, then the Identity, then
    /// target-side records, then the trailing column for unplaceable hops where one exists.
    /// </summary>
    public required IReadOnlyList<CausalityLineageColumn> Columns { get; init; }

    /// <summary>
    /// The relationships between adjacent columns: element i joins <see cref="Columns"/>[i] to
    /// Columns[i + 1], so there is always exactly one fewer join than columns (and none for a
    /// single-column story).
    /// </summary>
    public required IReadOnlyList<CausalityLineageJoin> Joins { get; init; }

    /// <summary>
    /// True when at least one chain branch stopped at the depth bound rather than at a real end,
    /// carried through from the walk so the canvas can say "there is more" distinctly from "this is
    /// the whole story".
    /// </summary>
    public bool IsTruncatedByDepth { get; init; }
}
