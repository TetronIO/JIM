// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One window of a virtualised list, as a page returns it to the grid.
/// </summary>
/// <param name="Items">The rows of the requested window, already sorted.</param>
/// <param name="TotalItems">The total number of rows matching the current filters, or null when the request did
/// not ask for the count (see <see cref="VirtualisedWindowRequest.IncludeTotalCount"/>). Null is deliberately
/// distinct from zero: it means "not counted", never "no matches".</param>
public sealed record VirtualisedWindow<TItem>(ICollection<TItem> Items, int? TotalItems);
