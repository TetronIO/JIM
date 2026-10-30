// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;

namespace JIM.Web.Causality;

/// <summary>
/// The values of an execution item's change records, as the causality panel needs them (#2498): a summary of every
/// change, and the rows themselves for the changes small enough to carry whole. A change to a large group carries one
/// value change per member, so its rows are read a window at a time instead, and nothing shows a partial list as if it
/// were complete.
/// </summary>
public sealed class CausalityChangeValues
{
    /// <summary>
    /// The most rows a change may have to be carried whole: the same ten that the detail pages show inline before
    /// handing a multi-valued attribute to a scrolling table.
    /// </summary>
    public const int InlineLimit = 10;

    /// <summary>No change values: a hand-built item's change records carry their own.</summary>
    public static readonly CausalityChangeValues None = new();

    /// <summary>A summary of each change record that has values, keyed by change id.</summary>
    public IReadOnlyDictionary<Guid, ChangeValueSummary> Summaries { get; init; } = new Dictionary<Guid, ChangeValueSummary>();

    /// <summary>
    /// Every row of each change with no more than <see cref="InlineLimit"/> of them, keyed by change id, in source order.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<CausalityAttributeRow>> Rows { get; init; } = new Dictionary<Guid, IReadOnlyList<CausalityAttributeRow>>();
}
