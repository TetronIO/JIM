// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
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
    /// </remarks>
    public Task CaptureWatermarkAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse, TimeSpan searchTimeout)
    {
        // OpenLDAP: query cn=accesslog for the latest reqStart timestamp
        rootDse.LastAccesslogTimestamp = QueryAccesslogForLatestTimestamp(searchTimeout);

        // If the accesslog is empty (e.g., after snapshot restore clears stale data),
        // generate a fallback timestamp so the watermark is never null. This prevents
        // the next delta import from falling back to a full import unnecessarily.
        if (string.IsNullOrEmpty(rootDse.LastAccesslogTimestamp))
        {
            rootDse.LastAccesslogTimestamp = LdapConnectorUtilities.GenerateAccesslogFallbackTimestamp();
            _logger.Information("GetRootDseInformation: Accesslog is empty; using fallback timestamp {Timestamp} as watermark",
                rootDse.LastAccesslogTimestamp);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Queries OpenLDAP's cn=accesslog for the most recent reqStart timestamp, to be used as the
    /// delta import watermark. Uses a multi-strategy approach to handle server-side size limits:
    /// 1. Server-side sort (reverse by reqStart) with SizeLimit=1 to get only the latest entry.
    ///    This requires the sssvlv overlay to be enabled on the server.
    /// 2. If sorting is not supported, falls back to handling the SizeLimitExceeded exception
    ///    by extracting partial results from the exception response.
    /// </summary>
    private string? QueryAccesslogForLatestTimestamp(TimeSpan searchTimeout)
    {
        try
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
        catch (Exception ex)
        {
            _logger.Warning(ex, "QueryAccesslogForLatestTimestamp: Failed to query accesslog. " +
                "The directory may not have the accesslog overlay enabled.");
            return null;
        }
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
    public Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken)
    {
        // Nothing is checked in this layer; a later one probes cn=accesslog for readability.
        return Task.FromResult<IReadOnlyList<LdapDeltaSourceFinding>>([]);
    }

    /// <inheritdoc />
    public bool HasBaseline(LdapConnectorRootDse previous) => !string.IsNullOrEmpty(previous.LastAccesslogTimestamp);

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

            try
            {
                response = (SearchResponse)_executor.SendRequest(request, context.SearchTimeout);

                if (response == null || response.ResultCode != ResultCode.Success)
                {
                    _logger.Warning("GetDeltaResultsUsingAccesslog: Failed to query accesslog. ResultCode: {ResultCode}",
                        response?.ResultCode);
                    return;
                }
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
            catch (Exception ex)
            {
                _logger.Error(ex, "GetDeltaResultsUsingAccesslog: Error querying accesslog");
                return;
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
    /// Builds a delete import object from an accesslog auditDelete entry.
    /// Extracts the objectClass and entryUUID from reqOld attributes to construct
    /// a proper import object with ObjectType and external ID, matching what the
    /// USN-based delete detection produces.
    /// </summary>
    private ConnectedSystemImportObject? BuildDeleteImportObjectFromAccesslog(SearchResultEntry accesslogEntry, IReadOnlyList<ConnectedSystemObjectType> objectTypes)
    {
        // Extract entryUUID: try reqEntryUUID first (direct attribute), then reqOld
        var entryUuid = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqEntryUUID");

        // Extract objectClass from reqOld values (format: "attributeName: value")
        var reqOldValues = LdapConnectorUtilities.GetEntryAttributeStringValues(accesslogEntry, "reqOld") ?? [];

        const string objectClassPrefix = "objectClass: ";
        var objectClasses = reqOldValues
            .Where(oldValue => oldValue.StartsWith(objectClassPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(oldValue => oldValue[objectClassPrefix.Length..].Trim());

        // The same precedence as a live entry gets, so a deletion is recorded against the Object Type the object
        // was imported as.
        var objectType = LdapObjectTypeMatcher.Match(objectClasses, objectTypes);

        // Also try to get entryUUID from reqOld if not found via reqEntryUUID
        const string entryUuidPrefix = "entryUUID: ";
        entryUuid ??= reqOldValues
            .FirstOrDefault(oldValue => oldValue.StartsWith(entryUuidPrefix, StringComparison.OrdinalIgnoreCase))?
            [entryUuidPrefix.Length..].Trim();

        if (objectType == null)
        {
            var reqDn = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqDN");
            _logger.Warning("BuildDeleteImportObjectFromAccesslog: Could not determine object type for deleted object. " +
                "DN: {Dn}. The accesslog entry may not contain reqOld attributes.", LogSanitiser.Sanitise(reqDn));
            return null;
        }

        if (string.IsNullOrEmpty(entryUuid))
        {
            var reqDn = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqDN");
            _logger.Warning("BuildDeleteImportObjectFromAccesslog: Could not determine entryUUID for deleted object. " +
                "DN: {Dn}. The accesslog entry may not contain reqEntryUUID or reqOld entryUUID.", LogSanitiser.Sanitise(reqDn));
            return null;
        }

        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = objectType.Name,
            ChangeType = ObjectChangeType.Deleted,
        };

        // Add entryUUID as an attribute so the import processor can match to the existing CSO
        var externalIdAttribute = objectType.Attributes.FirstOrDefault(a => a.IsExternalId);

        if (externalIdAttribute != null)
        {
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
            {
                Name = externalIdAttribute.Name,
                StringValues = [entryUuid]
            });
        }

        // Add the DN as the secondary external ID (distinguishedName)
        var reqDnValue = LdapConnectorUtilities.GetEntryAttributeStringValue(accesslogEntry, "reqDN");
        if (!string.IsNullOrEmpty(reqDnValue))
        {
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
            {
                Name = "distinguishedName",
                StringValues = [reqDnValue]
            });
        }

        _logger.Debug("BuildDeleteImportObjectFromAccesslog: Built delete import for {ObjectType} with entryUUID {Uuid}, DN: {Dn}",
            objectType.Name, LogSanitiser.Sanitise(entryUuid), LogSanitiser.Sanitise(reqDnValue));
        return importObject;
    }

    #endregion
}
