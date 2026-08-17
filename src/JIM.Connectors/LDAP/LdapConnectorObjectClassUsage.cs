// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads entries' <c>objectClass</c> values so JIM can tell an administrator which auxiliary classes their directory
/// actually uses.
/// </summary>
/// <remarks>
/// An RFC 4512 schema cannot answer this: an auxiliary class's definition says what it contributes, never where it
/// is used. The entries are the only source of truth, so this reads them, asking for nothing but
/// <c>objectClass</c>. Reading whole entries instead would turn a sample an administrator will run into one they
/// will cancel.
/// </remarks>
internal class LdapConnectorObjectClassUsage
{
    private readonly LdapConnection _connection;
    private readonly ILogger _logger;
    private readonly LdapConnectorRootDse _rootDse;

    internal LdapConnectorObjectClassUsage(LdapConnection connection, ILogger logger, LdapConnectorRootDse rootDse)
    {
        _connection = connection;
        _logger = logger;
        _rootDse = rootDse;
    }

    internal async Task<ObjectClassUsageResult> ReadAsync(
        ConnectedSystem connectedSystem,
        ObjectClassUsageRequest request,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        var result = new ObjectClassUsageResult();

        var containers = (connectedSystem.Partitions ?? [])
            .SelectMany(ConnectedSystemUtilities.GetTopLevelSelectedContainers)
            .ToList();

        if (containers.Count == 0)
        {
            // Sampling the whole directory when an administrator has scoped JIM to part of it would report classes
            // from entries JIM will never manage.
            _logger.Warning("ReadObjectClassUsage: No containers are selected for '{ConnectedSystem}', so there is nothing in scope to sample.", connectedSystem.Name);
            return result;
        }

        var filter = $"(objectClass={LdapConnectorUtilities.EscapeLdapFilterValue(request.ObjectTypeName)})";

        foreach (var container in containers)
        {
            if (Finished(request, result, cancellationToken))
                break;

            await progress.ReportAsync($"Sampling {request.ObjectTypeName} objects in {container.Name}...");
            await ReadContainerAsync(container, filter, request, result, cancellationToken, progress);
        }

        return result;
    }

    private async Task ReadContainerAsync(
        ConnectedSystemContainer container,
        string filter,
        ObjectClassUsageRequest request,
        ObjectClassUsageResult result,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        byte[]? cookie = null;
        var supportsPaging = _rootDse.SupportsPaging;

        do
        {
            if (Finished(request, result, cancellationToken))
                return;

            var searchRequest = new SearchRequest(container.ExternalId, filter,
                LdapConnectorUtilities.GetSearchScope(container), "objectClass");

            if (supportsPaging)
            {
                // Non-critical, so a directory that cannot page ignores the control and answers in one response
                // rather than refusing the search.
                var pageControl = new PageResultRequestControl(request.PageSize) { IsCritical = false };
                if (cookie is { Length: > 0 })
                    pageControl.Cookie = cookie;
                searchRequest.Controls.Add(pageControl);
            }

            var response = (SearchResponse)_connection.SendRequest(searchRequest);
            if (response.ResultCode != ResultCode.Success)
            {
                // One unreadable container must not cost the sample every other container's findings, but an
                // administrator reading a suggestion needs to know it was assembled from an incomplete read.
                _logger.Warning("ReadObjectClassUsage: Search of '{Container}' returned {ResultCode}; that container contributes nothing to this sample.",
                    container.ExternalId, response.ResultCode);
                result.Partial = true;
                return;
            }

            CountEntries(response, request, result);
            await progress.ReportObjectsReadAsync(result.EntriesRead);

            cookie = supportsPaging ? PagingCookie(response) : null;
        }
        while (cookie is { Length: > 0 });
    }

    private static void CountEntries(SearchResponse response, ObjectClassUsageRequest request, ObjectClassUsageResult result)
    {
        foreach (SearchResultEntry entry in response.Entries)
        {
            if (request.MaximumEntries.HasValue && result.EntriesRead >= request.MaximumEntries.Value)
            {
                result.Partial = true;
                return;
            }

            result.EntriesRead++;

            var objectClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(entry, "objectClass");
            if (objectClasses == null)
                continue;

            foreach (var objectClass in objectClasses)
                result.ObjectClassCounts[objectClass] = result.ObjectClassCounts.GetValueOrDefault(objectClass) + 1;
        }
    }

    /// <summary>
    /// Whether to stop, either because the sample limit is reached or because an administrator cancelled. Both mark
    /// the result partial, so what it reports is never read as a statement about the whole population.
    /// </summary>
    private static bool Finished(ObjectClassUsageRequest request, ObjectClassUsageResult result, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            result.Partial = true;
            return true;
        }

        if (!request.MaximumEntries.HasValue || result.EntriesRead < request.MaximumEntries.Value)
            return false;

        result.Partial = true;
        return true;
    }

    private static byte[]? PagingCookie(SearchResponse response)
    {
        return response.Controls?
            .OfType<PageResultResponseControl>()
            .FirstOrDefault()?
            .Cookie;
    }
}
