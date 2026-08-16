// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// A window of results retrieved by absolute offset and count, alongside the total (unwindowed) match count.
/// Unlike <see cref="PagedResultSet{T}"/> this carries no page-number semantics; it exists for virtualised
/// (infinite-scroll) list views whose data source is addressed by <c>offset</c>/<c>count</c> rather than by page.
/// </summary>
public class RangeResultSet<T>
{
    /// <summary>
    /// The items in the requested window, in query order.
    /// </summary>
    public List<T> Results { get; set; } = new();

    /// <summary>
    /// The total number of items matching the query across all windows, used to size the virtualised scroll area.
    /// Null when the caller asked for the window without the total (it already knows it), which is deliberately
    /// distinct from zero: counting the whole match set is the expensive half of a window read, and a scroll that
    /// reads it once per filter change rather than once per window must not confuse "not counted" with "no matches".
    /// </summary>
    public int? TotalResults { get; set; }
}
