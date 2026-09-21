// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// How one page of the tombstone search ended.
/// </summary>
internal enum DeletedObjectsSearchOutcome
{
    /// <summary>The directory answered the page; its entries, if any, are to be imported.</summary>
    Read,

    /// <summary>The directory refused the search, so no deletions were detected in this container.</summary>
    Refused,

    /// <summary>The partition has no Deleted Objects container to search.</summary>
    ContainerMissing,

    /// <summary>
    /// The directory stopped at its own size limit before answering everything. Nothing is imported from such an
    /// answer: with no cookie the rest can never be fetched, and importing the part that arrived would move the
    /// watermark past the part that did not.
    /// </summary>
    LimitExceeded
}

/// <summary>
/// One page of the tombstone search: what was read, where the next page starts, and how it ended.
/// </summary>
internal sealed class DeletedObjectsPage
{
    /// <summary>The tombstones in this page; empty for every outcome but a successful read.</summary>
    internal required IReadOnlyList<SearchResultEntry> Entries { get; init; }

    /// <summary>The cookie the next page resumes from, or null when this page was the last.</summary>
    internal byte[]? NextCookie { get; init; }

    internal required DeletedObjectsSearchOutcome Outcome { get; init; }

    /// <summary>The directory's own words for a page that was not read, already sanitised for logging and notes.</summary>
    internal string? Detail { get; init; }
}

/// <summary>
/// Searches a partition's Deleted Objects container for the tombstones a USN Delta Import must turn into deletions,
/// one page at a time, exactly as <c>GetDeltaResultsUsingUsn</c> pages the changes in an ordinary container.
/// <para>
/// This lives apart from <c>LdapConnectorImport</c> because that class holds the raw <see cref="LdapConnection"/>,
/// which is sealed and cannot be driven by a fake directory; the <see cref="ILdapOperationExecutor"/> seam is the
/// repository's answer to that, and <see cref="LdapConnectorDeletedObjectsAccess"/> already reaches the same
/// container through it. What the import does with each page stays in the import.
/// </para>
/// <para>
/// It exists to fix two defects (#1724). The search used to carry no paging control, so Active Directory capped
/// it at its MaxPageSize (1000 by default) and, for a bulk clean-up larger than that since the last import,
/// answered SizeLimitExceeded; the import read that as the directory not supporting the Show Deleted Objects
/// control, imported no deletions at all, and moved the watermark past them for good. And the search was not
/// gated on a pagination token, so a multi-page Delta Import re-read and re-yielded the same tombstones once per
/// page. Paging with a cookie of its own fixes both.
/// </para>
/// </summary>
internal class LdapConnectorDeletedObjectsSearch
{
    /// <summary>
    /// The little a tombstone keeps that the import needs: objectGUID to match it to its Connected System Object,
    /// objectClass to find its Object Type, and the rest for diagnostics.
    /// </summary>
    private static readonly string[] TombstoneAttributes = ["objectGUID", "objectClass", "isDeleted", "lastKnownParent", "distinguishedName"];

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapConnectorDeletedObjectsSearch(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Reads one page of tombstones changed since the watermark.
    /// </summary>
    /// <param name="containerDn">The partition's Deleted Objects container.</param>
    /// <param name="previousUsn">The highest USN the last import committed; only tombstones changed after it are read.</param>
    /// <param name="pageSize">The Run Profile's page size, when the directory pages.</param>
    /// <param name="supportsPaging">Whether the directory honours the paged-results control; Samba AD does not, and is sent none.</param>
    /// <param name="cookie">The cookie the previous page returned, or null for the first page.</param>
    /// <param name="timeout">How long to wait for the directory to answer.</param>
    internal DeletedObjectsPage Search(string containerDn, long previousUsn, int pageSize, bool supportsPaging, byte[]? cookie, TimeSpan timeout)
    {
        // isDeleted narrows the container to tombstones proper; uSNChanged is what a deletion bumps, so the same
        // watermark that finds the changes finds the deletions.
        var filter = $"(&(isDeleted=TRUE)(uSNChanged>={previousUsn + 1}))";
        var request = new SearchRequest(containerDn, filter, SearchScope.Subtree, TombstoneAttributes);

        // Without this the container and everything in it are invisible. Critical, so a directory that does not
        // honour it refuses rather than quietly answering nothing.
        request.Controls.Add(new DirectoryControl(LdapConnectorConstants.LDAP_SERVER_SHOW_DELETED_OID, null, true, true));

        if (supportsPaging)
        {
            var paging = new PageResultRequestControl(pageSize)
            {
                // Non-critical, so a directory that cannot page answers the search rather than refusing it.
                IsCritical = false
            };
            if (cookie is { Length: > 0 })
                paging.Cookie = cookie;

            request.Controls.Add(paging);
        }

        SearchResponse response;
        try
        {
            response = (SearchResponse)_executor.SendRequest(request, timeout);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            // The directory stopped short. The exception carries the entries it did answer, but with no cookie
            // there is no resuming, and importing a partial set would move the watermark past the rest. Better
            // no deletions and a plain warning than half of them and silence about the other half.
            var detail = LogSanitiser.Sanitise(ex.Message);
            _logger.Warning("LdapConnectorDeletedObjectsSearch: The directory returned more deleted objects in {Container} than it answers in one search; none were imported. Error: {Message}",
                LogSanitiser.Sanitise(containerDn), detail);
            return new DeletedObjectsPage { Entries = [], Outcome = DeletedObjectsSearchOutcome.LimitExceeded, Detail = detail };
        }
        catch (DirectoryOperationException ex) when (cookie is { Length: > 0 } &&
            ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
        {
            // The directory handed back a cookie on the first page and now refuses it (Samba AD does this). It
            // answered everything on that first page, as GetDeltaResultsUsingUsn already assumes for changes.
            _logger.Warning("LdapConnectorDeletedObjectsSearch: The directory rejected the paging cookie for {Container}; assuming all deleted objects were returned on the first page. Error: {Message}",
                LogSanitiser.Sanitise(containerDn), LogSanitiser.Sanitise(ex.Message));
            return new DeletedObjectsPage { Entries = [], Outcome = DeletedObjectsSearchOutcome.Read };
        }
        catch (DirectoryOperationException ex)
        {
            // Anything else the directory refused for: no rights, an unsupported control on the first page, and so
            // on. No deletions were detected here, and the import says so in the directory's own words.
            var detail = LogSanitiser.Sanitise(ex.Message);
            _logger.Warning("LdapConnectorDeletedObjectsSearch: The directory refused the search of {Container}. Error: {Message}",
                LogSanitiser.Sanitise(containerDn), detail);
            return new DeletedObjectsPage { Entries = [], Outcome = DeletedObjectsSearchOutcome.Refused, Detail = detail };
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject
        {
            // Some configurations have no Deleted Objects container to search.
            _logger.Debug("LdapConnectorDeletedObjectsSearch: Deleted Objects container not found at {Container}.", LogSanitiser.Sanitise(containerDn));
            return new DeletedObjectsPage { Entries = [], Outcome = DeletedObjectsSearchOutcome.ContainerMissing };
        }

        byte[]? nextCookie = null;
        if (supportsPaging &&
            response.Controls?.OfType<PageResultResponseControl>().FirstOrDefault() is { Cookie.Length: > 0 } pageResponse)
        {
            nextCookie = pageResponse.Cookie;
        }

        return new DeletedObjectsPage
        {
            Entries = response.Entries.Cast<SearchResultEntry>().ToList(),
            NextCookie = nextCookie,
            Outcome = DeletedObjectsSearchOutcome.Read
        };
    }
}
