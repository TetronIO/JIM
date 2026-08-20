// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// The sort surface a <see cref="VirtualisedDataGrid{TItem}"/> cascades to the column headers rendered inside it,
/// so <see cref="VirtualisedSortHeader"/> can stay non-generic while the grid it talks to is not.
/// </summary>
public interface IVirtualisedSortable
{
    /// <summary>The attribute or column name the list is currently sorted by.</summary>
    string SortBy { get; }

    /// <summary>Whether the current sort is descending.</summary>
    bool SortDescending { get; }

    /// <summary>
    /// Sorts by the named column, flipping direction when it is already the active sort. Re-sorting sends the
    /// reader back to the top: the position they held described an order that no longer exists.
    /// </summary>
    Task ToggleSortAsync(string name);
}
