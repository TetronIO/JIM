// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The Lineage's reading of what an item went on to cause (#1811), for the footer and the summary band: when it was
/// taken, whether anything is still to happen, and the After this run strip.
/// </summary>
public sealed class CausalityLineageConsequenceSummary
{
    /// <summary>
    /// When the walk ran, in UTC. Consequences can still be happening, so this is a reading, not a final account.
    /// </summary>
    public DateTime CheckedAt { get; init; }

    /// <summary>
    /// Whether any branch is waiting on something that will happen without anyone acting, which is the only time
    /// checking again can show anything new.
    /// </summary>
    public bool HasWaitingBranches { get; init; }

    /// <summary>
    /// Whether any branch goes further than the walk read.
    /// </summary>
    public bool IsFurtherThanShown { get; init; }

    /// <summary>
    /// Whether the item caused anything at all, or has a branch to account for. False for an item whose kind causes
    /// nothing (a confirming import), which then shows no After this run sections and no footer.
    /// </summary>
    public bool HasAny { get; init; }

    /// <summary>
    /// The footer's sentences: what each waiting kind of branch is waiting on, or that every branch has finished.
    /// </summary>
    public IReadOnlyList<string> StateSentences { get; init; } = [];

    /// <summary>
    /// The summary band's After this run strip.
    /// </summary>
    public IReadOnlyList<CausalityPill> Pills { get; init; } = [];
}
