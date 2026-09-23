// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// The change source for OpenLDAP: the accesslog overlay's cn=accesslog database (slapo-accesslog), where every
/// successful write operation is logged with its reqStart timestamp. The watermark is the latest reqStart seen;
/// a Delta Import reads the write operations at or after it and fetches each affected object's current state.
/// <para>
/// OpenLDAP enforces olcSizeLimit (default 500) as a hard cap for non-rootDN clients, even with paging controls.
/// The bind account used by the connector is typically not the rootDN of the cn=accesslog database, so paging
/// alone cannot bypass the limit. Both the watermark capture and the change read work within it: when the size
/// limit is exceeded, the latest timestamp from the partial results narrows the next query, walking forward
/// through the accesslog until all entries have been scanned.
/// </para>
/// <para>
/// An accesslog that cannot be read is never taken for one that is empty. The readiness check reads the
/// cn=accesslog entry and reports what it found; the watermark is left empty rather than invented when the search
/// is refused or finds nothing to search; and a Delta Import whose search is refused stops with the reason rather
/// than importing nothing and reporting "no changes".
/// </para>
/// </summary>
internal sealed class LdapAccesslogDeltaSource : ILdapDeltaSource
{
    private const string AccesslogDn = "cn=accesslog";
    private const string WriteOperationsFilter = "(&(objectClass=auditWriteObject)(reqResult=0))";

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;

    internal LdapAccesslogDeltaSource(ILdapOperationExecutor executor, ILogger logger)
    {
        _executor = executor;
        _logger = logger;
    }

    #region Watermark

    /// <inheritdoc />
    /// <remarks>
    /// Runs during both Full and Delta Imports: a Full Import establishes the baseline for the first Delta Import,
    /// and a Delta Import captures the current position so the next one starts from here. The rootDSE entry itself
    /// carries nothing this source needs.
    /// <para>
    /// The watermark is left empty when cn=accesslog cannot be read (missing, refused, or unreachable), so that the
    /// next Delta Import finds no baseline and performs a Full Import rather than reading nothing from a log it
    /// cannot see and reporting "no changes". Only a readable, empty accesslog gets a generated timestamp: after a
    /// snapshot restore clears it there is genuinely nothing before now, and a baseline saves the next Delta Import
    /// an unnecessary Full Import.
    /// </para>
    /// </remarks>
    public Task CaptureWatermarkAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse, TimeSpan searchTimeout)
    {
        try
        {
            rootDse.LastAccesslogTimestamp = QueryAccesslogForLatestTimestamp(searchTimeout);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            LeaveWatermarkEmpty(rootDse, "it was not found");
            return Task.CompletedTask;
        }
        catch (DirectoryOperationException ex)
        {
            LeaveWatermarkEmpty(rootDse, $"the directory refused the search: {DirectorysWords(ex)}");
            return Task.CompletedTask;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
        {
            LeaveWatermarkEmpty(rootDse, "it was not found");
            return Task.CompletedTask;
        }
        catch (LdapException ex)
        {
            LeaveWatermarkEmpty(rootDse, $"the directory could not be reached: {DirectorysWords(ex)}");
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(rootDse.LastAccesslogTimestamp))
        {
            rootDse.LastAccesslogTimestamp = LdapConnectorUtilities.GenerateAccesslogFallbackTimestamp();
            _logger.Information("LdapAccesslogDeltaSource: The accesslog at {AccesslogDn} is readable but empty; using the generated timestamp {Timestamp} as the watermark so the next Delta Import has a baseline",
                AccesslogDn, rootDse.LastAccesslogTimestamp);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records that no watermark could be taken, and why. A generated timestamp would give the next Delta Import a
    /// baseline it has no right to; an empty one makes it perform a Full Import and say so.
    /// </summary>
    private void LeaveWatermarkEmpty(LdapConnectorRootDse rootDse, string reason)
    {
        rootDse.LastAccesslogTimestamp = null;
        _logger.Warning("LdapAccesslogDeltaSource: No watermark was taken from {AccesslogDn}: {Reason}. The next Delta Import will have no baseline and will perform a Full Import.",
            AccesslogDn, reason);
    }

    /// <summary>
    /// Queries OpenLDAP's cn=accesslog for the most recent reqStart timestamp, to be used as the
    /// delta import watermark. Uses a multi-strategy approach to handle server-side size limits:
    /// 1. Server-side sort (reverse by reqStart) with SizeLimit=1 to get only the latest entry.
    ///    This requires the sssvlv overlay to be enabled on the server.
    /// 2. If sorting is not supported, falls back to handling the SizeLimitExceeded exception
    ///    by extracting partial results from the exception response.
    /// Null when the accesslog is readable and holds no write operations. A missing, refused or unreachable
    /// accesslog is left to the caller as the exception the directory raised.
    /// </summary>
    private string? QueryAccesslogForLatestTimestamp(TimeSpan searchTimeout)
    {
        // Strategy 1: Server-side sort (reverse) with SizeLimit=1
        // This gets only the single latest entry, avoiding size limit issues entirely.
        var result = QueryAccesslogWithServerSideSort(searchTimeout);
        if (result != null)
            return result;

        // Strategy 2: Simple query with size limit exceeded handling.
        // If the accesslog has fewer entries than olcSizeLimit, this returns all entries normally.
        // If it exceeds the limit, we catch the DirectoryOperationException and extract
        // the latest timestamp from the partial results in the exception's response.
        return QueryAccesslogWithSizeLimitHandling(searchTimeout);
    }

    /// <summary>
    /// Attempts to query the accesslog using server-side sorting (reverse by reqStart) with
    /// SizeLimit=1 to retrieve only the latest entry. This requires the sssvlv overlay.
    /// Returns null if server-side sorting is not supported.
    /// </summary>
    private string? QueryAccesslogWithServerSideSort(TimeSpan searchTimeout)
    {
        try
        {
            var request = new SearchRequest(AccesslogDn,
                WriteOperationsFilter,
                SearchScope.OneLevel,
                "reqStart");

            // Request reverse sort by reqStart so the latest entry comes first
            var sortControl = new SortRequestControl(new SortKey("reqStart", "caseIgnoreOrderingMatch", true));
            sortControl.IsCritical = true;
            request.Controls.Add(sortControl);
            request.SizeLimit = 1;

            var response = (SearchResponse)_executor.SendRequest(request, searchTimeout);
            if (response?.Entries.Count > 0)
            {
                var timestamp = LdapConnectorUtilities.GetEntryAttributeStringValue(response.Entries[0], "reqStart");
                if (timestamp != null)
                {
                    _logger.Debug("QueryAccesslogWithServerSideSort: Latest accesslog timestamp: {Timestamp} (via server-side sort)", timestamp);
                    return timestamp;
                }
            }

            _logger.Debug("QueryAccesslogWithServerSideSort: No accesslog entries found via server-side sort");
            return null;
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse { ResultCode: ResultCode.UnavailableCriticalExtension or ResultCode.UnwillingToPerform })
        {
            _logger.Debug("QueryAccesslogWithServerSideSort: Server-side sorting not supported (sssvlv overlay not enabled). Falling back to size-limit-aware query.");
            return null;
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse { ResultCode: ResultCode.InappropriateMatching })
        {
            // Matching rule not supported for this attribute; fall back
            _logger.Debug("QueryAccesslogWithServerSideSort: Sort matching rule not supported for reqStart. Falling back to size-limit-aware query.");
            return null;
        }
    }

    /// <summary>
    /// Queries the accesslog using an iterative approach that works within the server's size limit.
    /// When the size limit is exceeded, extracts the latest timestamp from partial results and
    /// re-queries with a narrower filter (reqStart >= latest_seen) to walk forward through the
    /// accesslog until all entries have been scanned. This effectively implements manual paging
    /// without requiring paging controls to bypass the size limit.
    /// </summary>
    private string? QueryAccesslogWithSizeLimitHandling(TimeSpan searchTimeout)
    {
        string? latestTimestamp = null;
        var totalEntries = 0;
        var iterations = 0;
        const int maxIterations = 100; // Safety limit to prevent infinite loops

        // Start with an unfiltered query to get the first batch
        var currentFilter = WriteOperationsFilter;

        while (iterations < maxIterations)
        {
            iterations++;
            string? batchLatest;
            int batchCount;
            var hitSizeLimit = false;

            try
            {
                var request = new SearchRequest(AccesslogDn, currentFilter,
                    SearchScope.OneLevel, "reqStart");

                var response = (SearchResponse)_executor.SendRequest(request, searchTimeout);
                (batchLatest, batchCount) = ExtractLatestTimestamp(response);
            }
            catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partialResponse
                && partialResponse.ResultCode == ResultCode.SizeLimitExceeded)
            {
                // The server hit its size limit but returned partial results.
                (batchLatest, batchCount) = ExtractLatestTimestamp(partialResponse);
                hitSizeLimit = true;
            }

            totalEntries += batchCount;

            if (batchCount == 0 || batchLatest == null)
                break;

            // Update the overall latest timestamp
            if (latestTimestamp == null || string.Compare(batchLatest, latestTimestamp, StringComparison.Ordinal) > 0)
                latestTimestamp = batchLatest;

            if (!hitSizeLimit)
                break; // Got all results without hitting the limit: done

            // Size limit was hit. The partial results contain the earliest entries (OpenLDAP
            // returns in insertion order). Re-query starting after the latest timestamp we've
            // seen to walk forward through the remaining entries.
            _logger.Debug("QueryAccesslogWithSizeLimitHandling: Size limit exceeded on iteration {Iteration}. " +
                "Latest timestamp so far: {Timestamp}. Re-querying from that point.",
                iterations, latestTimestamp);

            currentFilter = $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={latestTimestamp}))";
        }

        if (totalEntries == 0)
        {
            _logger.Debug("QueryAccesslogWithSizeLimitHandling: No accesslog entries found");
            return null;
        }

        _logger.Debug("QueryAccesslogWithSizeLimitHandling: Latest accesslog timestamp: {Timestamp} " +
            "(scanned {Count} entries in {Iterations} iterations)",
            latestTimestamp, totalEntries, iterations);
        return latestTimestamp;
    }

    /// <summary>
    /// Extracts the latest reqStart timestamp from a search response containing accesslog entries.
    /// </summary>
    private static (string? latestTimestamp, int entryCount) ExtractLatestTimestamp(SearchResponse response)
    {
        string? latestTimestamp = null;
        var count = 0;

        foreach (SearchResultEntry entry in response.Entries)
        {
            var reqStart = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqStart");
            if (reqStart != null && (latestTimestamp == null || string.Compare(reqStart, latestTimestamp, StringComparison.Ordinal) > 0))
                latestTimestamp = reqStart;
            count++;
        }

        return (latestTimestamp, count);
    }

    #endregion

    #region Continuity, readiness and baseline

    /// <inheritdoc />
    /// <remarks>A timestamp watermark carries no server identity to verify, so nothing can invalidate it here.</remarks>
    public void VerifyContinuity(LdapConnectorRootDse previous, LdapConnectorRootDse current)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the cn=accesslog entry itself, base scope and no attributes, which is the least a directory will answer
    /// about it. OpenLDAP shares one accesslog across every database it serves, so there is a single subject here
    /// whatever the partitions. An entry answered is an availability. No entry, noSuchObject (in either shape the
    /// client library gives it) and a refusal are all unavailabilities, because each was read in full: a directory
    /// that hides what the account may not see answers success with nothing, and some answer noSuchObject for a
    /// base the account may not read, so a missing entry is never claimed as absence outright. Only a fault on the
    /// way to the directory is an unknown.
    /// </remarks>
    public Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var request = new SearchRequest(AccesslogDn, "(objectClass=*)", SearchScope.Base, "1.1");

        LdapDeltaSourceFinding finding;
        try
        {
            var response = (SearchResponse)_executor.SendRequest(request);
            finding = response.Entries.Count > 0
                ? Available()
                : NotFound("the directory answered the read with no entry");
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            finding = NotFound("the directory answered noSuchObject");
        }
        catch (DirectoryOperationException ex)
        {
            finding = Refused(DirectorysWords(ex));
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
        {
            finding = NotFound("the directory answered noSuchObject");
        }
        catch (LdapException ex)
        {
            finding = Undetermined($"the directory could not be reached ({DirectorysWords(ex)})");
        }

        return Task.FromResult<IReadOnlyList<LdapDeltaSourceFinding>>([finding]);
    }

    /// <inheritdoc />
    public bool HasBaseline(LdapConnectorRootDse previous) => !string.IsNullOrEmpty(previous.LastAccesslogTimestamp);

    #endregion

    #region Findings and their texts

    private LdapDeltaSourceFinding Available()
    {
        _logger.Debug("LdapAccesslogDeltaSource: The account JIM connects as can read {AccesslogDn}.", AccesslogDn);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.Available,
            Detail = "the accesslog entry is readable"
        };
    }

    private LdapDeltaSourceFinding NotFound(string detail)
    {
        _logger.Warning("LdapAccesslogDeltaSource: No accesslog was found at {AccesslogDn}, or none the account JIM connects as may read: {Detail}. A Delta Import from this directory would detect no changes.",
            AccesslogDn, detail);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.Unavailable,
            Detail = detail,
            DeltaImportText = DescribeNotFoundForDeltaImport(),
            SchemaDiscoveryText = DescribeNotFoundForSchemaDiscovery()
        };
    }

    private LdapDeltaSourceFinding Refused(string reason)
    {
        _logger.Warning("LdapAccesslogDeltaSource: The directory refused to read {AccesslogDn}: {Reason}. A Delta Import from this directory would detect no changes.",
            AccesslogDn, reason);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.Unavailable,
            Detail = $"the directory refused the read: {reason}",
            DeltaImportText = DescribeRefusedForDeltaImport(reason),
            SchemaDiscoveryText = DescribeRefusedForSchemaDiscovery(reason)
        };
    }

    private LdapDeltaSourceFinding Undetermined(string detail)
    {
        _logger.Warning("LdapAccesslogDeltaSource: Could not establish whether the account JIM connects as can read {AccesslogDn}: {Detail}", AccesslogDn, detail);
        var text = DescribeUndetermined(detail);
        return new LdapDeltaSourceFinding
        {
            Subject = AccesslogDn,
            Outcome = LdapDeltaSourceOutcome.CouldNotDetermine,
            Detail = detail,
            DeltaImportText = text,
            SchemaDiscoveryText = text
        };
    }

    /// <summary>The directory's own words for a refusal or a fault, made safe to log and to quote in a finding.</summary>
    private static string DirectorysWords(Exception ex) => LogSanitiser.Sanitise(ex.Message) ?? string.Empty;

    /// <summary>What every Delta Import text ends with: the two ways out, and where the access-control rule is.</summary>
    private const string DeltaImportRemedy =
        "Run a Full Import, which also detects deletions by absence, or enable the accesslog overlay and grant the account read access to " + AccesslogDn + "; " +
        "the LDAP Connector documentation, under Service Account Permissions, gives the access-control rule.";

    /// <summary>The failure that stops a Delta Import when there is no accesslog to read, or none the account may see.</summary>
    private static string DescribeNotFoundForDeltaImport() =>
        $"Changes cannot be detected: the directory provides no accesslog at {AccesslogDn}, or none the account JIM connects as may read, " +
        "so additions, updates and deletions since the last import would go unnoticed. " + DeltaImportRemedy;

    /// <summary>The failure that stops a Delta Import when the directory refused the accesslog search, in its own words.</summary>
    private static string DescribeRefusedForDeltaImport(string reason) =>
        $"Changes cannot be detected: the directory refused to read {AccesslogDn} ({reason}), " +
        "so additions, updates and deletions since the last import would go unnoticed. " + DeltaImportRemedy;

    /// <summary>What Schema Discovery warns when there is no accesslog, so the administrator learns of it while setting the Connected System up.</summary>
    private static string DescribeNotFoundForSchemaDiscovery() =>
        $"This directory publishes no accesslog at {AccesslogDn} that the account JIM connects as may read, so Delta Import is not available; " +
        "Full Import works as normal and also detects deletions by absence. " +
        $"To use Delta Import, enable the accesslog overlay and grant the account read access to {AccesslogDn}; see the LDAP Connector documentation, Service Account Permissions.";

    /// <summary>What Schema Discovery warns when the directory refused to read the accesslog.</summary>
    private static string DescribeRefusedForSchemaDiscovery(string reason) =>
        $"The directory refused to read {AccesslogDn} ({reason}), so Delta Import is not available; " +
        "Full Import works as normal and also detects deletions by absence. " +
        $"To use Delta Import, grant the account JIM connects as read access to {AccesslogDn}; see the LDAP Connector documentation, Service Account Permissions.";

    /// <summary>The one text for an unknown, in both places: what could not be confirmed, why, and what it would mean.</summary>
    private static string DescribeUndetermined(string detail) =>
        $"JIM could not confirm that the account it connects as can read {AccesslogDn}: {detail}. " +
        "If it cannot, Delta Imports from this directory detect no changes.";

    #endregion

    #region Reading changes

    /// <inheritdoc />
    /// <remarks>
    /// Queries cn=accesslog for write operations that occurred after the previous watermark timestamp. For each
    /// change, fetches the current state of the affected object through the host; a deletion is built from the
    /// accesslog entry itself, because the object is no longer there to fetch.
    /// Handles the server-side size limit (olcSizeLimit, default 500) by iterating through batches: when the size
    /// limit is exceeded, the latest timestamp from partial results is used to narrow the next query, effectively
    /// walking forward through the accesslog until all changes are found. Everything is read in one call; nothing
    /// is paged across calls.
    /// </remarks>
    /// <exception cref="CannotPerformDeltaImportException">The directory refused the accesslog search, or has no accesslog to search.</exception>
    public async Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken)
    {
        // The import establishes HasBaseline before calling, so the watermark is present.
        var previousTimestamp = context.PreviousRootDse.LastAccesslogTimestamp!;

        await context.Host.EnterPhaseAsync(LdapConnectorPhases.QueryChanges, $"Querying changes since {previousTimestamp}...");

        _logger.Debug("GetDeltaResultsUsingAccesslog: Querying for changes since {PreviousTimestamp}", previousTimestamp);

        var currentTimestamp = previousTimestamp;
        var totalEntries = 0;
        var skippedOutOfScope = 0;
        var iterations = 0;
        const int maxIterations = 100; // Safety limit
        // Track processed DNs+timestamps to avoid duplicates when iterating with >= filters
        var processedEntries = new HashSet<string>(StringComparer.Ordinal);
        // Track processed DNs to avoid importing the same object multiple times when the
        // accesslog has multiple changes for the same DN (e.g., 3 member modifications to
        // the same group). Since we fetch current state rather than replaying individual
        // changes, only the first occurrence needs to be processed.
        var processedDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Build partition suffix list for filtering: OpenLDAP shares cn=accesslog across
        // all databases, so we must exclude entries from other suffixes.
        var partitionSuffixes = context.TargetPartitions
            .Select(p => p.ExternalId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();
        // Partition filtering alone is not the same selection a full import makes: within a partition, only the
        // selected Containers are imported from, and each carries its own scope. Applying that here keeps a delta
        // import to the objects a full import would have returned.
        var targetContainers = context.ScopeDecidingContainers;

        while (iterations < maxIterations)
        {
            iterations++;

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetDeltaResultsUsingAccesslog: Cancellation requested. Stopping");
                return;
            }

            // LDAP only supports >= (not >), so we use >= and skip already-processed entries in code.
            var ldapFilter = $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={currentTimestamp}))";
            // Request reqOld for delete entries; it contains the old objectClass and entryUUID
            // which are needed to construct proper delete import objects.
            var request = new SearchRequest(AccesslogDn, ldapFilter, SearchScope.OneLevel,
                "reqStart", "reqType", "reqDN", "reqOld", "reqEntryUUID");

            SearchResponse response;
            var hitSizeLimit = false;
            var objectsReadBeforeThisBatch = result.ImportObjects.Count;

            // A search the directory will not answer stops the run rather than ending the read: returning here
            // would import nothing, which the run would report as "no changes". A fault on the way to the directory
            // (any other LdapException) propagates as the run's failure, in the directory's own words.
            try
            {
                response = (SearchResponse)_executor.SendRequest(request, context.SearchTimeout);

                if (response == null || response.ResultCode != ResultCode.Success)
                    throw RefusedSearch($"result code {response?.ResultCode.ToString() ?? "none"}");
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                throw MissingAccesslog();
            }
            catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partialResponse
                && partialResponse.ResultCode == ResultCode.SizeLimitExceeded)
            {
                // Size limit exceeded; process the partial results we got
                response = partialResponse;
                hitSizeLimit = true;
                _logger.Debug("GetDeltaResultsUsingAccesslog: Size limit exceeded on iteration {Iteration}. " +
                    "Processing {Count} partial results.", iterations, response.Entries.Count);
            }
            catch (DirectoryOperationException ex)
            {
                throw RefusedSearch(DirectorysWords(ex));
            }
            catch (LdapException ex) when (ex.ErrorCode == 32) // noSuchObject, the legacy shape
            {
                throw MissingAccesslog();
            }

            if (response.Entries.Count == 0)
                break;

            _logger.Debug("GetDeltaResultsUsingAccesslog: Processing {Count} accesslog entries (iteration {Iteration})",
                response.Entries.Count, iterations);

            string? batchLatestTimestamp = null;

            foreach (SearchResultEntry entry in response.Entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("GetDeltaResultsUsingAccesslog: Cancellation requested. Stopping");
                    return;
                }

                var reqStart = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqStart");
                var reqType = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqType");
                var reqDn = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqDN");

                if (string.IsNullOrEmpty(reqDn) || string.IsNullOrEmpty(reqStart))
                    continue;

                // Filter by partition scope: only process entries whose DN falls within
                // the Connected System's selected partitions. OpenLDAP's shared cn=accesslog
                // records changes from ALL databases (suffixes), so we must exclude entries
                // from other suffixes to avoid importing objects from the wrong partition.
                if (partitionSuffixes.Count > 0 &&
                    !partitionSuffixes.Any(suffix => reqDn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    skippedOutOfScope++;
                    continue;
                }

                // Filter by Container scope: the entry is within a selected partition, but the administrator
                // selects Containers within it, and a OneLevel Container excludes everything below its own level.
                if (!LdapConnectorUtilities.IsDnInScope(reqDn, targetContainers))
                {
                    skippedOutOfScope++;
                    continue;
                }

                // Track the latest timestamp in this batch for the next iteration
                if (batchLatestTimestamp == null || string.Compare(reqStart, batchLatestTimestamp, StringComparison.Ordinal) > 0)
                    batchLatestTimestamp = reqStart;

                // Skip the exact timestamp match from the previous watermark
                if (reqStart == previousTimestamp)
                    continue;

                // Skip entries we've already processed (from overlapping >= queries)
                var entryKey = $"{reqStart}|{reqDn}";
                if (!processedEntries.Add(entryKey))
                    continue;

                totalEntries++;

                // Map accesslog reqType to ObjectChangeType
                var objectChangeType = reqType?.ToLowerInvariant() switch
                {
                    "add" => ObjectChangeType.Added,
                    "modify" => ObjectChangeType.Updated,
                    "delete" => ObjectChangeType.Deleted,
                    "modrdn" => ObjectChangeType.Updated,
                    _ => ObjectChangeType.NotSet
                };

                // Deduplicate by DN for non-delete operations. Multiple add/modify entries for
                // the same DN would produce identical import objects (since we fetch current state),
                // triggering duplicate detection errors. However, delete entries must ALWAYS be
                // processed even if the DN was already seen: a group that was added then deleted
                // in the same delta window must be processed as a delete (the final state).
                if (objectChangeType != ObjectChangeType.Deleted && !processedDns.Add(reqDn))
                    continue;

                if (objectChangeType == ObjectChangeType.Deleted)
                {
                    // For deletes, extract object type and external ID from the accesslog entry.
                    // The reqOld attribute contains the old attribute values (key: value format),
                    // and reqEntryUUID contains the entryUUID of the deleted object.
                    var deleteObject = BuildDeleteImportObjectFromAccesslog(entry, context.ObjectTypes);
                    if (deleteObject != null)
                        result.ImportObjects.Add(deleteObject);
                }
                else
                {
                    // For add/modify/modrdn, fetch the current state of the object
                    var currentObject = context.Host.GetObjectByDn(reqDn, objectChangeType);
                    if (currentObject != null)
                    {
                        result.ImportObjects.Add(currentObject);
                    }
                    else
                    {
                        _logger.Warning("GetDeltaResultsUsingAccesslog: GetObjectByDn returned null for DN '{ReqDn}' " +
                            "(change type: {ChangeType}). The object may have been deleted or moved since the accesslog " +
                            "entry was recorded. Entry skipped.", LogSanitiser.Sanitise(reqDn), objectChangeType);
                    }
                }
            }

            // A delta that follows an outage can run to a very large number of changes, each one
            // costing a round trip to fetch the object's current state, so report at every batch
            // boundary rather than leaving the Activity's counters still until the whole walk ends.
            await context.Host.ReportObjectsReadAsync(result.ImportObjects.Count - objectsReadBeforeThisBatch);

            if (!hitSizeLimit)
                break; // Got all results without hitting the limit: done

            // Size limit was hit. Narrow the query to start from the latest timestamp we've seen
            // to walk forward through the remaining entries.
            if (batchLatestTimestamp == null || batchLatestTimestamp == currentTimestamp)
            {
                // No progress made: all entries have the same timestamp. Cannot narrow further.
                _logger.Warning("GetDeltaResultsUsingAccesslog: Cannot narrow accesslog query further. " +
                    "All {Count} entries in this batch have timestamp {Timestamp}. Some changes may be missed.",
                    response.Entries.Count, currentTimestamp);
                break;
            }

            currentTimestamp = batchLatestTimestamp;
        }

        _logger.Debug("GetDeltaResultsUsingAccesslog: Processed {TotalEntries} change entries in {Iterations} iterations. " +
            "Skipped {SkippedOutOfScope} entries outside partition scope.",
            totalEntries, iterations, skippedOutOfScope);
    }

    /// <summary>
    /// The failure a Delta Import stops with when the accesslog search found nothing at cn=accesslog to search.
    /// </summary>
    private CannotPerformDeltaImportException MissingAccesslog()
    {
        _logger.Warning("GetDeltaResultsUsingAccesslog: No accesslog was found at {AccesslogDn}, or none the account JIM connects as may read. Refusing the Delta Import rather than reporting no changes.", AccesslogDn);
        return new CannotPerformDeltaImportException(DescribeNotFoundForDeltaImport());
    }

    /// <summary>
    /// The failure a Delta Import stops with when the directory refused the accesslog search, in its own words.
    /// </summary>
    private CannotPerformDeltaImportException RefusedSearch(string reason)
    {
        _logger.Warning("GetDeltaResultsUsingAccesslog: The directory refused the search of {AccesslogDn}: {Reason}. Refusing the Delta Import rather than reporting no changes.", AccesslogDn, reason);
        return new CannotPerformDeltaImportException(DescribeRefusedForDeltaImport(reason));
    }

    /// <summary>
    /// Builds a delete import object from an accesslog auditDelete entry: reqOld holds the deleted entry's
    /// attributes as "name: value" lines, and reqEntryUUID states its entryUUID directly, outranking any reqOld
    /// carries. The import object matches what the USN-based delete detection produces (Object Type, external id,
    /// DN); null, with a warning, when reqOld names no selected Object Type or no entryUUID can be found.
    /// </summary>
    private ConnectedSystemImportObject? BuildDeleteImportObjectFromAccesslog(SearchResultEntry accesslogEntry, IReadOnlyList<ConnectedSystemObjectType> objectTypes)
    {
        var reqDn = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqDN");
        var reqEntryUuid = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqEntryUUID");
        var reqOldValues = LdapConnectorUtilities.GetEntryAttributeStringValues(accesslogEntry, "reqOld") ?? [];

        return LdapDeletedEntryIdentity.Identify(reqOldValues, reqDn, reqEntryUuid, objectTypes, _logger);
    }

    #endregion
}
