// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// Everything a provider needs to generate one page of a keyset-paginated read.
/// <para>
/// Keyset pagination, never OFFSET: OFFSET makes the server re-scan and discard every row already
/// returned, so the cost of page N grows with N and a 500,000-row import degrades quadratically.
/// Filtering on the anchor instead lets the server seek straight to the page boundary on the index
/// that already orders it.
/// </para>
/// </summary>
internal sealed record SqlKeysetPageRequest
{
    internal string? SchemaName { get; init; }

    /// <summary>
    /// The primary table or view being read.
    /// </summary>
    internal required string ObjectName { get; init; }

    internal required IReadOnlyList<string> SelectColumns { get; init; }

    /// <summary>
    /// The anchor column or columns, in order. A single column is the documented default; a composite
    /// anchor is compared lexicographically, matching the ORDER BY exactly.
    /// </summary>
    internal required IReadOnlyList<string> AnchorColumns { get; init; }

    /// <summary>
    /// The parameter carrying the Run Profile's page size.
    /// </summary>
    internal required string PageSizeParameterName { get; init; }

    /// <summary>
    /// The parameters carrying the previous page's last anchor value, one per anchor column. Empty on
    /// the first page of a run, where there is no boundary to filter beyond.
    /// </summary>
    internal IReadOnlyList<string> LastAnchorParameterNames { get; init; } = [];

    /// <summary>
    /// True when no previous anchor was supplied, so the page starts at the beginning of the ordered set.
    /// </summary>
    internal bool IsFirstPage => LastAnchorParameterNames.Count == 0;
}
