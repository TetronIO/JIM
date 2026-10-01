// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads everything a search matches, one page at a time where the directory pages (RFC 2696), for the connector's
/// own metadata searches: the schema's classes and attributes, a partition's containers, the forest's domain
/// controllers.
/// </summary>
/// <remarks>
/// <para>
/// Active Directory refuses an unpaged search that would return more than its MaxPageSize (1,000 by default) with
/// sizeLimitExceeded, and a stock forest publishes well over 1,000 <c>attributeSchema</c> entries, so an unpaged
/// schema read fails on the first connection to a real domain controller. Samba AD applies no such cap, which is why
/// the Samba integration lab never showed it (#1853). The paged-results control is what lifts the cap: each page is
/// itself capped at MaxPageSize, and the cookie carries the search across pages.
/// </para>
/// <para>
/// The control is sent non-critical, so a directory that does not page answers the whole search in one response
/// rather than refusing it; a directory the connector knows not to page (Samba AD, which returns duplicate entries
/// across pages) is sent no control at all, exactly as the object import does. A directory that hands back a cookie
/// and then refuses it on the next page is treated the way the import treats it: the first page was the whole result.
/// A directory that stops the search at its own limit even so is not hidden: the refusal is let through, because a
/// partial schema or container hierarchy silently accepted is worse than a discovery that fails and says why.
/// </para>
/// </remarks>
internal static class LdapPagedSearch
{
    /// <summary>
    /// Sends <paramref name="request"/> and returns every entry it matches, following the paged-results cookie
    /// until the directory answers without one.
    /// </summary>
    /// <param name="executor">Where the requests go.</param>
    /// <param name="request">The search. Its control collection is used for the paging control and is modified.</param>
    /// <param name="supportsPaging">Whether the directory honours the paged-results control; false sends none.</param>
    /// <param name="pageSize">How many entries to ask for per page.</param>
    /// <param name="logger">Where a refused cookie is reported.</param>
    /// <param name="purpose">What the search is for, named in that report.</param>
    internal static List<SearchResultEntry> ReadAll(ILdapOperationExecutor executor, SearchRequest request, bool supportsPaging, int pageSize, ILogger logger, string purpose)
    {
        var entries = new List<SearchResultEntry>();

        if (!supportsPaging)
        {
            var response = (SearchResponse)executor.SendRequest(request);
            entries.AddRange(response.Entries.Cast<SearchResultEntry>());
            return entries;
        }

        byte[]? cookie = null;
        var page = 0;
        while (true)
        {
            page++;

            // One paging control per request: the previous page's is replaced, never stacked.
            foreach (var previous in request.Controls.OfType<PageResultRequestControl>().ToList())
                request.Controls.Remove(previous);

            var paging = new PageResultRequestControl(pageSize) { IsCritical = false };
            if (cookie is { Length: > 0 })
                paging.Cookie = cookie;
            request.Controls.Add(paging);

            SearchResponse response;
            try
            {
                response = (SearchResponse)executor.SendRequest(request);
            }
            catch (DirectoryOperationException ex) when (page > 1 &&
                ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
            {
                logger.Warning("LdapPagedSearch: The directory returned a paging cookie for the {Purpose} search and then refused it on page {Page}; treating the {Count} entries already read as the whole result. Error: {Message}",
                    purpose, page, entries.Count, LogSanitiser.Sanitise(ex.Message));
                return entries;
            }

            entries.AddRange(response.Entries.Cast<SearchResultEntry>());

            cookie = response.Controls?.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is not { Length: > 0 })
                return entries;
        }
    }
}
