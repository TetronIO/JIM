// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Windows an in-memory list for <see cref="VirtualisedWindow{TItem}"/> consumers. Small configuration lists
/// (Connectors, API Keys, Predefined Searches and friends) are loaded whole and sliced here rather than each
/// growing a database range read; the page owns filtering and sorting (they differ per page), then hands the
/// result to this one place so the window contract is honoured identically everywhere, in particular that the
/// total is only counted when asked for and a null total means "not counted", never zero.
/// </summary>
public static class VirtualisedWindowExtensions
{
    /// <summary>
    /// Slices an already-filtered, already-sorted list into the requested window, counting the whole list only
    /// when <see cref="VirtualisedWindowRequest.IncludeTotalCount"/> asks for it.
    /// </summary>
    public static VirtualisedWindow<T> ToWindow<T>(this IEnumerable<T> filteredAndSorted, VirtualisedWindowRequest request)
    {
        // Materialise once: the source may be a deferred LINQ pipeline, and counting plus slicing it twice
        // would re-run the page's filter for no benefit.
        var items = filteredAndSorted as IReadOnlyCollection<T> ?? filteredAndSorted.ToList();

        return new VirtualisedWindow<T>(
            items.Skip(request.StartIndex).Take(request.Count).ToList(),
            request.IncludeTotalCount ? items.Count : null);
    }
}
