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
    /// <summary>
    /// The name an administrator-supplied statement is given when it stands in for a table, because a
    /// derived table has to be named. Chosen so no real object collides with it.
    /// </summary>
    internal const string SourceAlias = "JIM_SOURCE";

    internal string? SchemaName { get; init; }

    /// <summary>
    /// The primary table or view being read. Null when <see cref="SelectStatement"/> is supplied
    /// instead; exactly one of the two is always set.
    /// </summary>
    internal string? ObjectName { get; init; }

    /// <summary>
    /// An administrator-supplied SELECT statement standing in for a table or view, wrapped as a derived
    /// table so the page is ordered, seeked and limited around it exactly as it would be around a table.
    /// </summary>
    internal string? SelectStatement { get; init; }

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
    /// The column a Delta Import restricts the read to, so that only rows beyond the persisted watermark
    /// are returned: a change log's sequence, or a source's last-modified column. Null for a Full Import,
    /// and null on a Delta Import that has no watermark yet and therefore reads from the beginning.
    /// </summary>
    internal string? ChangeColumn { get; init; }

    /// <summary>
    /// The parameter carrying the watermark <see cref="ChangeColumn"/> is compared against. Set exactly
    /// when <see cref="ChangeColumn"/> is.
    /// </summary>
    internal string? ChangeParameterName { get; init; }

    /// <summary>
    /// The related tables whose own watermarks also select a row, so that a change confined to one of
    /// them (a group membership added or revoked) is detected as a change to the object it belongs to.
    /// Empty for a Full Import, for Change-Log Table mode, and for an object type with no related tables.
    /// </summary>
    internal IReadOnlyList<SqlRelatedChangeSource> RelatedChangeSources { get; init; } = [];

    /// <summary>
    /// True when no previous anchor was supplied, so the page starts at the beginning of the ordered set.
    /// </summary>
    internal bool IsFirstPage => LastAnchorParameterNames.Count == 0;

    /// <summary>
    /// True when the read is restricted to rows beyond a watermark.
    /// </summary>
    internal bool HasChangeFilter => ChangeColumn != null;

    /// <summary>
    /// The predicate this page's changed rows are selected by, or null where the page reads everything.
    /// </summary>
    internal SqlChangeFilter? ChangeFilter => HasChangeFilter
        ? new SqlChangeFilter
        {
            ChangeColumn = ChangeColumn!,
            ChangeParameterName = ChangeParameterName!,
            AnchorColumns = AnchorColumns,
            RelatedSources = RelatedChangeSources
        }
        : null;
}
