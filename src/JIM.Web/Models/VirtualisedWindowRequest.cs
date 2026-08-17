// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One window of a virtualised list, as the grid asks its page for it: which rows, under which search and sort,
/// and whether the page should also count the whole match set.
/// </summary>
/// <param name="StartIndex">The zero-based index of the first row wanted.</param>
/// <param name="Count">How many rows are wanted. Always at least one; the grid's zero-width measuring probes are
/// clamped before they reach the page.</param>
/// <param name="SearchText">The free-text search narrowing the list, or null when the reader has not searched.</param>
/// <param name="SortBy">The attribute or column name the list is sorted by.</param>
/// <param name="SortDescending">Whether the sort is descending.</param>
/// <param name="IncludeTotalCount">Whether to count the whole match set alongside the window. Counting is the
/// expensive half of a window read, so the grid only asks when the filters have changed since the last count;
/// a loader must honour false by skipping its count query and returning a null total.</param>
public sealed record VirtualisedWindowRequest(
    int StartIndex,
    int Count,
    string? SearchText,
    string SortBy,
    bool SortDescending,
    bool IncludeTotalCount);
