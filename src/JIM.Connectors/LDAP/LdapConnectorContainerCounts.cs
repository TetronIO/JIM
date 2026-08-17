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

    /// <summary>
    /// How long a single page may take before the directory is treated as unresponsive.
    /// </summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the whole count may take before it gives up and reports what it has.
    /// </summary>
    /// <remarks>
    /// Counting is folded into Retrieve Hierarchy, so it spends an administrator's wait on something they did not
    /// ask for by name. The hierarchy is what they wanted, and on a directory large enough for the count to run
    /// long it is also the thing they need soonest. A budget is the right control here rather than a confirmation
    /// prompt: a prompt would interrupt every hierarchy refresh to ask about a cost that is usually trivial.
    /// </remarks>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(60);

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly bool _supportsPaging;
    private readonly TimeSpan _budget;

    internal LdapConnectorContainerCounts(ILdapOperationExecutor executor, ILogger logger, bool supportsPaging, TimeSpan? budget = null)
    {
        _executor = executor;
        _logger = logger;
        _supportsPaging = supportsPaging;
        _budget = budget ?? DefaultBudget;
    }

    /// <summary>
    /// Whether the count has spent its whole budget and should stop with what it has.
    /// </summary>
    /// <param name="elapsed">How long the count has been running.</param>
    /// <param name="budget">The budget; zero or less means there is none, and only cancellation stops the count.</param>
    internal static bool ShouldStopForBudget(TimeSpan elapsed, TimeSpan budget) =>
        budget > TimeSpan.Zero && elapsed >= budget;

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

        // Deliberately not handing the token to Task.Run. Doing so makes an already-cancelled token throw before the
        // body runs, which contradicts what this method promises: a count reports being cut short through
        // ConnectorContainerObjectCountResult, and the caller treats a throw as a failed count rather than a
        // truncated one. Cancellation is observed inside the loop instead, so early and mid-flight cancellation
        // both come back the same way.
        return await Task.Run(() => Count(connectorPartition, objectTypeNames, result, cancellationToken), CancellationToken.None);
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

            if (ShouldStopForBudget(stopwatch.Elapsed, _budget))
            {
                _logger.Warning("Count: '{Partition}' gave up after {Elapsed} having counted {Counted} objects",
                    LogSanitiser.Sanitise(connectorPartition.Name), stopwatch.Elapsed, counted);

                return Incomplete(result,
                    $"Counting stopped after {_budget.TotalSeconds:N0} seconds so that the hierarchy was not held up. Narrowing the selected Object Types is the usual fix.");
            }

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
                response = (SearchResponse)_executor.SendRequest(searchRequest, PageTimeout);
            }
            catch (DirectoryOperationException ex) when (IsLimitExceeded(ex))
            {
                // The server stopped early. Whatever has been counted so far is real but short of the truth, and
                // presenting it plainly would understate what deselecting a Container costs.
                _logger.Warning("Count: '{Partition}' hit a server limit after {Counted} objects: {Message}",
                    LogSanitiser.Sanitise(connectorPartition.Name), counted, LogSanitiser.Sanitise(ex.Message));

                return Incomplete(result,
                    "The directory stopped the search at its own size or time limit. Raising that limit, or narrowing the selected Object Types, is the usual fix.");
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

            // The parent of each returned Distinguished Name is the Container the object sits in, which is what
            // makes this one search per partition rather than one per Container. An entry with no parent is at the
            // top of the namespace and belongs to no Container here.
            var containerIdentifiers = response.Entries.Cast<SearchResultEntry>()
                .Select(entry => LdapConnectorUtilities.ParseDistinguishedName(entry.DistinguishedName).ParentDn)
                .Where(parentDn => !string.IsNullOrEmpty(parentDn));

            foreach (var containerIdentifier in containerIdentifiers)
            {
                result.DirectCountsByContainerIdentifier[containerIdentifier!] =
                    result.DirectCountsByContainerIdentifier.GetValueOrDefault(containerIdentifier!) + 1;
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
