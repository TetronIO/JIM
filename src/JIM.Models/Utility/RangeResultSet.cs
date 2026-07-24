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
    /// </summary>
    public int TotalResults { get; set; }
}
