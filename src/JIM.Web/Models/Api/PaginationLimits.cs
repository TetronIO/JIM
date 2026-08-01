// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Web.Models.Api;

/// <summary>
/// The single source of truth for how deep into a result set a caller may page (issue #487).
/// <para>
/// PostgreSQL answers an <c>OFFSET n</c> query by walking and discarding n rows, so the cost of a paged read
/// is set by the offset, not by the page number. The ceiling here is therefore expressed as a maximum offset:
/// a page number alone is a poor proxy, being four times stricter at a page size of 25 than at 100 for exactly
/// the same database work.
/// </para>
/// <para>
/// Requests beyond the ceiling are rejected with a 400 rather than silently clamped, so a caller learns they
/// have over-paged instead of quietly receiving the wrong rows.
/// </para>
/// </summary>
public static class PaginationLimits
{
    /// <summary>
    /// The largest page size any paginated endpoint will serve. Endpoints clamp to this rather than rejecting,
    /// so the depth rule evaluates against the page size the query will actually use.
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// The deepest offset (rows skipped) any paginated endpoint will serve.
    /// <para>
    /// Sized above the largest validated deployment scale (500,000 objects per Connected System; see
    /// <c>docs/administration/deployment.md</c>) so that enumerating an entire corpus stays possible, while
    /// still bounding the absurd case. A caller asking for page 99,999,999 is not paging, it is a bug or an
    /// attack, and either way PostgreSQL should never be asked to walk 10,000,000,000 rows to answer it.
    /// </para>
    /// <para>
    /// Note that offset paging costs O(n^2) to walk a whole corpus regardless of this ceiling. The ceiling
    /// makes deep paging bounded, not cheap; keyset pagination is the answer for routine full enumeration.
    /// </para>
    /// </summary>
    public const int MaxSkip = 1_000_000;

    /// <summary>
    /// Whether a page / page size combination is within the retrieval depth ceiling.
    /// </summary>
    /// <remarks>
    /// Evaluated in 64-bit arithmetic: <c>(page - 1) * pageSize</c> overflows a 32-bit integer long before
    /// <see cref="int.MaxValue"/> pages, and a wrapped negative offset would sail past the check.
    /// Sub-1 pages and oversized page sizes are normalised the same way the endpoints normalise them, so the
    /// rule never rejects a request the action would have served happily.
    /// </remarks>
    public static bool IsWithinDepth(int page, int pageSize)
    {
        return SkipFor(page, pageSize) <= MaxSkip;
    }

    /// <summary>
    /// The deepest page number that may be requested at a given page size.
    /// </summary>
    public static int MaxPageFor(int pageSize)
    {
        var effectivePageSize = NormalisePageSize(pageSize);
        return (MaxSkip / effectivePageSize) + 1;
    }

    /// <summary>
    /// The error message returned when a request exceeds the retrieval depth ceiling. Names the limit and the
    /// deepest page the caller may ask for at their page size, so the fix is obvious from the response alone.
    /// </summary>
    public static string DepthExceededMessage(int page, int pageSize)
    {
        var effectivePageSize = NormalisePageSize(pageSize);
        return string.Format(
            CultureInfo.InvariantCulture,
            "Page {0} is beyond the maximum retrieval depth of {1} rows (the deepest page at a page size of {2} is {3}). " +
            "Requests beyond this depth are rejected to protect database performance at scale; narrow the result set with " +
            "a filter or search rather than paging so deep.",
            page.ToString("N0", CultureInfo.InvariantCulture),
            MaxSkip.ToString("N0", CultureInfo.InvariantCulture),
            effectivePageSize,
            MaxPageFor(effectivePageSize).ToString("N0", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The number of rows a page / page size combination would skip, normalised and widened so it cannot overflow.
    /// </summary>
    private static long SkipFor(int page, int pageSize)
    {
        var effectivePage = page < 1 ? 1 : page;
        return (long)(effectivePage - 1) * NormalisePageSize(pageSize);
    }

    /// <summary>
    /// Normalises a page size to the range the endpoints actually serve.
    /// </summary>
    private static int NormalisePageSize(int pageSize)
    {
        if (pageSize < 1)
            return 1;

        return pageSize > MaxPageSize ? MaxPageSize : pageSize;
    }
}
