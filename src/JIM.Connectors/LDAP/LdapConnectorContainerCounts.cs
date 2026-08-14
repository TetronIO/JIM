// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Counts the objects each Container in a partition holds, for the Partitions and Containers tab (#1276).
/// </summary>
/// <remarks>
/// LDAP has no COUNT operation, so counting means retrieving the matching entries. It does not mean running an
/// import: RFC 4511 lets a search ask for the attribute list <c>1.1</c>, meaning "return no attributes at all", so
/// each match comes back as a Distinguished Name and nothing else.
///
/// That turns every Container's count into <b>one search per partition</b> rather than one per Container, because
/// the parent of each returned Distinguished Name is the Container the object sits in. A directory holding half a
/// million objects answers in one paged search returning names only, rather than in one search per Container
/// multiplied by one per Object Type.
/// </remarks>
internal class LdapConnectorContainerCounts
{
    /// <summary>
    /// The attribute selector that asks an LDAP server for no attributes at all (RFC 4511 §4.5.1.8). Every entry
    /// still arrives with its Distinguished Name, which is the only thing being counted here.
    /// </summary>
    private const string NoAttributes = "1.1";

    /// <summary>
    /// How many entries to ask for per page. Not the Run Profile's page size: no Run Profile is involved in a
    /// hierarchy retrieval, and this search reads no attributes, so a page carries far less than an import's does.
    /// </summary>
    private const int PageSize = 1000;

    private readonly LdapConnection _connection;
    private readonly ILogger _logger;
    private readonly bool _supportsPaging;

    internal LdapConnectorContainerCounts(LdapConnection ldapConnection, ILogger logger, bool supportsPaging)
    {
        _connection = ldapConnection;
        _logger = logger;
        _supportsPaging = supportsPaging;
    }

    internal async Task<ConnectorContainerObjectCountResult> CountAsync(
        ConnectorPartition connectorPartition,
        IReadOnlyList<string> objectTypeNames,
        CancellationToken cancellationToken)
    {
        var result = new ConnectorContainerObjectCountResult();

        // Counting every entry regardless of type would report a number no import will ever match, which is worse
        // than reporting nothing: it is wrong in the direction that makes a deselection look more expensive.
        if (objectTypeNames.Count == 0)
        {
            _logger.Debug("CountAsync: no Object Types selected for partition '{Partition}', so there is nothing to count",
                LogSanitiser.Sanitise(connectorPartition.Name));
            return result;
        }

        return await Task.Run(() => Count(connectorPartition, objectTypeNames, result, cancellationToken), cancellationToken);
    }

    private ConnectorContainerObjectCountResult Count(
        ConnectorPartition connectorPartition,
        IReadOnlyList<string> objectTypeNames,
        ConnectorContainerObjectCountResult result,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var filter = BuildObjectClassFilter(objectTypeNames);
        var counted = 0;
        byte[]? cookie = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return Incomplete(result, "The count was cancelled before every object had been counted.");

            var searchRequest = new SearchRequest(connectorPartition.Name, filter, SearchScope.Subtree, NoAttributes);
            if (_supportsPaging)
            {
                var pageControl = new PageResultRequestControl(PageSize) { IsCritical = false };
                if (cookie is { Length: > 0 })
                    pageControl.Cookie = cookie;

                searchRequest.Controls.Add(pageControl);
            }

            SearchResponse response;
            try
            {
                response = (SearchResponse)_connection.SendRequest(searchRequest);
            }
            catch (DirectoryOperationException ex) when (IsLimitExceeded(ex))
            {
                // The server stopped early. Whatever has been counted so far is real but short of the truth, and
                // presenting it plainly would understate what deselecting a Container costs.
                _logger.Warning("Count: '{Partition}' hit a server limit after {Counted} objects: {Message}",
                    LogSanitiser.Sanitise(connectorPartition.Name), counted, LogSanitiser.Sanitise(ex.Message));

                return Incomplete(result,
                    "The directory stopped the search at its own size or time limit, so these counts are lower than the true figures.");
            }
            catch (DirectoryOperationException ex) when (cookie is { Length: > 0 } &&
                ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
            {
                // A server that returned a cookie on the first page but rejects it afterwards (Samba AD does this)
                // has already given everything it intends to; the same allowance the import path makes.
                _logger.Warning("Count: '{Partition}' rejected the paging cookie, so the first page is taken as the whole result",
                    LogSanitiser.Sanitise(connectorPartition.Name));
                break;
            }

            foreach (SearchResultEntry entry in response.Entries)
            {
                var parentDn = LdapConnectorUtilities.ParseDistinguishedName(entry.DistinguishedName).ParentDn;
                if (string.IsNullOrEmpty(parentDn))
                    continue;

                result.DirectCountsByContainerIdentifier[parentDn] =
                    result.DirectCountsByContainerIdentifier.GetValueOrDefault(parentDn) + 1;
                counted++;
            }

            cookie = ReadPagingCookie(response);
            if (!_supportsPaging || cookie is not { Length: > 0 })
                break;
        }

        stopwatch.Stop();
        _logger.Debug("Count: '{Partition}' counted {Counted} objects across {Containers} Containers in {Elapsed}",
            LogSanitiser.Sanitise(connectorPartition.Name), counted,
            result.DirectCountsByContainerIdentifier.Count, stopwatch.Elapsed);

        return result;
    }

    private static ConnectorContainerObjectCountResult Incomplete(ConnectorContainerObjectCountResult result, string reason)
    {
        result.Complete = false;
        result.IncompleteReason = reason;
        return result;
    }

    private static byte[]? ReadPagingCookie(SearchResponse response) =>
        response.Controls?.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;

    private static bool IsLimitExceeded(DirectoryOperationException exception) =>
        exception.Response?.ResultCode is ResultCode.SizeLimitExceeded or ResultCode.TimeLimitExceeded
            or ResultCode.AdminLimitExceeded;

    /// <summary>
    /// The filter matching any of the selected Object Types, which is the union of what a Full Import searches for
    /// per type. One search over the union costs one pass; one search per type costs a pass each.
    /// </summary>
    internal static string BuildObjectClassFilter(IReadOnlyList<string> objectTypeNames)
    {
        var clauses = objectTypeNames.Select(name => $"(objectClass={EscapeFilterValue(name)})").ToList();

        return clauses.Count == 1 ? clauses[0] : $"(|{string.Join(string.Empty, clauses)})";
    }

    /// <summary>
    /// Escapes the characters RFC 4515 reserves in a filter's assertion value. Object Type names come from the
    /// directory's own schema rather than from a person, but a filter built by string concatenation is a filter
    /// injection waiting for the one schema that carries a parenthesis.
    /// </summary>
    private static string EscapeFilterValue(string value) => value
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);
}
