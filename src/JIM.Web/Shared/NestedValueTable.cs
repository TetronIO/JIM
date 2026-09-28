// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// Settings shared by the virtualised tables nested in an attribute table's value cell (<see cref="CsoMvaTable"/>,
/// <see cref="MvoMvaTable"/>, <see cref="PendingExportMvaTable"/>), so the three read as the same kind of thing.
/// </summary>
public static class NestedValueTable
{
    /// <summary>
    /// The nested table's height ceiling. Its container is a table cell rather than the page, so it states one
    /// rather than measuring where the page footer lands, which would describe the page instead of the cell.
    /// </summary>
    public const string MaxHeight = "400px";
}
