// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Web;

namespace JIM.Web.Models;

/// <summary>
/// The part of the Metaverse Object list's view state that belongs in the URL, so a refresh, a bookmark or a link
/// shared with a colleague lands on the same rows the sender was looking at.
/// </summary>
/// <remarks>
/// All four values travel together deliberately. A scroll position on its own is meaningless: row 1240 of an
/// unfiltered list is a different object from row 1240 of a search, so restoring a position without the filter and
/// sort that produced it would land the reader somewhere arbitrary while looking correct.
///
/// A value equal to the list's own default is written as an absent parameter rather than an explicit one, which
/// keeps an untouched list's URL clean and, more importantly, means returning a value to its default removes the
/// parameter instead of pinning the default forever.
/// </remarks>
public sealed record VirtualisedListUrlState
{
    /// <summary>The free-text search. Distinct from the page's own <c>search</c> attribute-presence deep link.</summary>
    public const string SearchTextParameter = "q";

    /// <summary>The attribute name the list is sorted by.</summary>
    public const string SortByParameter = "sort";

    /// <summary>Present as <c>1</c> (descending) or <c>0</c> (ascending) when the direction differs from the
    /// list's own default; absent when it matches. The explicit <c>0</c> exists because a list may default to
    /// descending (time-ordered lists open newest first), and an absent parameter must mean "the default",
    /// never "ascending".</summary>
    public const string SortDescendingParameter = "desc";

    /// <summary>The zero-based index of the first row in view.</summary>
    public const string FirstVisibleRowParameter = "row";

    /// <summary>
    /// The free-text search currently narrowing the list, or null when the reader has not searched. Whitespace-only
    /// text is read as null: it filters nothing, so carrying it would only make a shared URL look filtered.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>The attribute name the list is sorted by. Never null once read; falls back to the list's default.</summary>
    public string? SortBy { get; init; }

    /// <summary>Whether the sort is descending.</summary>
    public bool SortDescending { get; init; }

    /// <summary>
    /// The zero-based index of the first row in view. This anchors a position rather than an object: a
    /// synchronisation that adds or removes matching objects shifts what sits at this index, exactly as it shifted
    /// what sat on page 7 under paging.
    /// </summary>
    public int FirstVisibleRow { get; init; }

    /// <summary>
    /// Reads the state out of a URL's query string, falling back to the list's defaults for anything absent or
    /// unusable. A hand-edited or truncated link must land somewhere valid rather than failing in front of a reader,
    /// so an unparseable or negative row is read as the top of the list.
    /// </summary>
    /// <param name="query">The query string, with or without its leading '?'.</param>
    /// <param name="defaultSortBy">The attribute the list sorts by when the URL says nothing.</param>
    /// <param name="parameterPrefix">Prefix applied to every parameter this state owns, so two virtualised lists
    /// on one page (each with its own prefix) can share a URL without overwriting each other. Null for a page
    /// with a single list.</param>
    /// <param name="defaultSortDescending">The direction the list sorts in when the URL says nothing.</param>
    public static VirtualisedListUrlState Read(string? query, string defaultSortBy, string? parameterPrefix = null, bool defaultSortDescending = false)
    {
        var parameters = HttpUtility.ParseQueryString(query ?? string.Empty);

        var searchText = parameters[parameterPrefix + SearchTextParameter];
        var sortBy = parameters[parameterPrefix + SortByParameter];
        var row = 0;
        if (int.TryParse(parameters[parameterPrefix + FirstVisibleRowParameter], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRow) && parsedRow > 0)
            row = parsedRow;

        return new VirtualisedListUrlState
        {
            SearchText = string.IsNullOrWhiteSpace(searchText) ? null : searchText,
            SortBy = string.IsNullOrWhiteSpace(sortBy) ? defaultSortBy : sortBy,
            SortDescending = parameters[parameterPrefix + SortDescendingParameter] switch
            {
                "1" => true,
                "0" => false,
                _ => defaultSortDescending
            },
            FirstVisibleRow = row
        };
    }

    /// <summary>
    /// Writes this state into an existing query string, returning the result without a leading '?'. Parameters this
    /// state does not own are preserved untouched; the page's attribute-presence deep link is one of them, and
    /// dropping it would silently widen the reader's filter.
    /// </summary>
    /// <param name="query">The current query string, with or without its leading '?'.</param>
    /// <param name="defaultSortBy">The attribute the list sorts by when the URL says nothing.</param>
    /// <param name="parameterPrefix">Prefix applied to every parameter this state owns; see
    /// <see cref="Read(string?, string, string?, bool)"/>. Parameters carrying a different prefix belong to
    /// another list and are preserved untouched, exactly like any other unrelated parameter.</param>
    /// <param name="defaultSortDescending">The direction the list sorts in when the URL says nothing.</param>
    public string Write(string? query, string defaultSortBy, string? parameterPrefix = null, bool defaultSortDescending = false)
    {
        var parameters = HttpUtility.ParseQueryString(query ?? string.Empty);

        Set(parameters, parameterPrefix + SearchTextParameter, string.IsNullOrWhiteSpace(SearchText) ? null : SearchText);
        Set(parameters, parameterPrefix + SortByParameter, string.Equals(SortBy, defaultSortBy, StringComparison.Ordinal) ? null : SortBy);
        Set(parameters, parameterPrefix + SortDescendingParameter, SortDescending == defaultSortDescending ? null : SortDescending ? "1" : "0");
        Set(parameters, parameterPrefix + FirstVisibleRowParameter, FirstVisibleRow > 0 ? FirstVisibleRow.ToString(CultureInfo.InvariantCulture) : null);

        return parameters.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Sets a parameter, or removes it when the value is null. Removal is what makes a value returned to its default
    /// disappear from the URL rather than linger as an explicit restatement of the default.
    /// </summary>
    private static void Set(System.Collections.Specialized.NameValueCollection parameters, string name, string? value)
    {
        if (value == null)
            parameters.Remove(name);
        else
            parameters[name] = value;
    }
}
