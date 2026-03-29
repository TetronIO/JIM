using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Text.Json;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorImport
{
    private const int DefaultSearchTimeoutSeconds = 300; // 5 minutes
    private const string SearchTimeoutSettingName = "Search Timeout";

    private readonly CancellationToken _cancellationToken;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly ILogger _logger;
    private readonly LdapConnection _connection;
    private readonly List<ConnectedSystemPaginationToken> _paginationTokens;
    private readonly string? _persistedConnectorData;
    private readonly TimeSpan _searchTimeout;
    private readonly string _placeholderMemberDn;
    private LdapConnectorRootDse? _previousRootDse;
    private LdapConnectorRootDse? _currentRootDse;

    internal LdapConnectorImport(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        LdapConnection connection,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _connectedSystem = connectedSystem;
        _connectedSystemRunProfile = runProfile;
        _connection = connection;
        _paginationTokens = paginationTokens;
        _persistedConnectorData = persistedConnectorData;
        _logger = logger;
        _cancellationToken = cancellationToken;

        // Get search timeout from settings, defaulting to 5 minutes
        var searchTimeoutSetting = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == SearchTimeoutSettingName);
        var searchTimeoutSeconds = searchTimeoutSetting?.IntValue ?? DefaultSearchTimeoutSeconds;
        _searchTimeout = TimeSpan.FromSeconds(searchTimeoutSeconds);

        // Get placeholder member DN for filtering during import
        var placeholderSetting = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == LdapConnectorConstants.SETTING_GROUP_PLACEHOLDER_MEMBER_DN);
        _placeholderMemberDn = placeholderSetting?.StringValue ?? LdapConnectorConstants.DEFAULT_GROUP_PLACEHOLDER_MEMBER_DN;

        // If we have persisted connector data from a previous page, deserialise it to get capabilities
        // This allows subsequent pages to know the directory capabilities without re-querying
        if (!string.IsNullOrEmpty(persistedConnectorData) && paginationTokens.Count > 0)
        {
            try
            {
                _currentRootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(persistedConnectorData);
            }
            catch (JsonException ex)
            {
                _logger.Warning(ex, "LdapConnectorImport: Failed to deserialise persisted connector data for capability detection. Will re-query directory.");
            }
        }
    }

    internal ConnectedSystemImportResult GetFullImportObjects()
    {
        _logger.Verbose("GetFullImportObjects: Started");

        if (_connectedSystem.Partitions == null)
            throw new ArgumentException("_connectedSystem.Partitions is null. Cannot continue.");
        if (_connectedSystem.ObjectTypes == null)
            throw new ArgumentException("_connectedSystem.ObjectTypes is null. Cannot continue.");

        var result = new ConnectedSystemImportResult();

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetFullImportObjects: O1 Cancellation requested. Stopping");
            return result;
        }

        if (_paginationTokens.Count == 0)
        {
            // initial-page call. we have no paging tokens to use (yet) to resume a query

            // get information about the directory we're connected to
            _currentRootDse = GetRootDseInformation();

            // Serialise the rootDSE info to JSON for persistence
            // This captures the current USN/changelog position for use in future delta imports
            result.PersistedConnectorData = JsonSerializer.Serialize(_currentRootDse);
        }

        // enumerate target partitions (scoped to run profile partition if set, otherwise all selected)
        foreach (var selectedPartition in GetTargetPartitions())
        {
            // enumerate top-level selected containers in this partition
            // Use GetTopLevelSelectedContainers to avoid duplicates when both parent and child containers are selected
            // (subtree search on parent already includes children)
            foreach (var selectedContainer in ConnectedSystemUtilities.GetTopLevelSelectedContainers(selectedPartition))
            {
                // we need to perform a query per object type, so that we can have distinct attribute lists per LDAP request
                foreach (var selectedObjectType in _connectedSystem.ObjectTypes.Where(ot => ot.Selected))
                {
                    // if this is the subsequent page for this container, use this when getting the results for the next page
                    var paginationTokenName = LdapConnectorUtilities.GetPaginationTokenName(selectedContainer, selectedObjectType);
                    var paginationToken = _paginationTokens.SingleOrDefault(pt => pt.Name == paginationTokenName);
                    var lastRunsCookie = paginationToken?.ByteValue;

                    // On subsequent pages, skip container+objectType combos that have no pagination token.
                    // All results for that combo were already returned on a previous page.
                    // Sending unrelated search requests between paged result calls invalidates the
                    // server-side paging cursor on OpenLDAP (RFC 2696 cookies are connection-scoped).
                    if (_paginationTokens.Count > 0 && paginationToken == null)
                    {
                        _logger.Debug("GetFullImportObjects: Skipping {ObjectType} in {Container} — no pagination token (all results returned on previous page)",
                            selectedObjectType.Name, selectedContainer.ExternalId);
                        continue;
                    }

                    if (_cancellationToken.IsCancellationRequested)
                    {
                        _logger.Debug("GetFullImportObjects: O2 Cancellation requested. Stopping");
                        return result;
                    }

                    GetFisoResults(result, selectedContainer, selectedObjectType, lastRunsCookie);
                }
            }
        }

        // closing notes:
        // this implementation ends up performing paging per selected container, which might confuse the user if they have a lot of selected containers and end up
        // getting more results back per pass than they expect. Consider refactoring the interface between JIM.Service and the connector, so it is executed once per selected container
        // so the connector always returns a page of results to the JIM.Service (a page of results per container).

        return result;
    }

    internal ConnectedSystemImportResult GetDeltaImportObjects()
    {
        _logger.Verbose("GetDeltaImportObjects: Started");

        if (_connectedSystem.Partitions == null)
            throw new ArgumentException("_connectedSystem.Partitions is null. Cannot continue.");
        if (_connectedSystem.ObjectTypes == null)
            throw new ArgumentException("_connectedSystem.ObjectTypes is null. Cannot continue.");

        var result = new ConnectedSystemImportResult();

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeltaImportObjects: Cancellation requested. Stopping");
            return result;
        }

        // Try to deserialise the previous run's RootDSE info to get the watermark
        if (string.IsNullOrEmpty(_persistedConnectorData))
        {
            _logger.Warning("GetDeltaImportObjects: No persisted connector data available. A full import must be run first before delta imports.");
            throw new CannotPerformDeltaImportException("No persisted connector data available. Run a full import first to establish a baseline.");
        }

        _previousRootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(_persistedConnectorData);
        if (_previousRootDse == null)
        {
            _logger.Warning("GetDeltaImportObjects: Could not deserialise persisted connector data.");
            throw new CannotPerformDeltaImportException("Could not deserialise persisted connector data. Run a full import to re-establish baseline.");
        }

        if (_paginationTokens.Count == 0)
        {
            // Initial page - get the current RootDSE info
            _currentRootDse = GetRootDseInformation();
            result.PersistedConnectorData = JsonSerializer.Serialize(_currentRootDse);
        }

        // Determine which delta strategy to use
        if (_previousRootDse.UseUsnDeltaImport)
        {
            if (!_previousRootDse.HighestCommittedUsn.HasValue)
            {
                throw new CannotPerformDeltaImportException("Previous USN watermark not available. Run a full import first.");
            }

            _logger.Debug("GetDeltaImportObjects: Using AD USN-based delta import. Previous USN: {PreviousUsn}",
                _previousRootDse.HighestCommittedUsn);

            // For AD, query objects where uSNChanged > previous HighestCommittedUSN
            foreach (var selectedPartition in GetTargetPartitions())
            {
                // Use GetTopLevelSelectedContainers to avoid duplicates when both parent and child containers are selected
                foreach (var selectedContainer in ConnectedSystemUtilities.GetTopLevelSelectedContainers(selectedPartition))
                {
                    foreach (var selectedObjectType in _connectedSystem.ObjectTypes.Where(ot => ot.Selected))
                    {
                        if (_cancellationToken.IsCancellationRequested)
                        {
                            _logger.Debug("GetDeltaImportObjects: Cancellation requested. Stopping");
                            return result;
                        }

                        var paginationTokenName = LdapConnectorUtilities.GetPaginationTokenName(selectedContainer, selectedObjectType);
                        var paginationToken = _paginationTokens.SingleOrDefault(pt => pt.Name == paginationTokenName);
                        var lastRunsCookie = paginationToken?.ByteValue;

                        // On subsequent pages, skip combos with no pagination token (see full import comment)
                        if (_paginationTokens.Count > 0 && paginationToken == null)
                            continue;

                        GetDeltaResultsUsingUsn(result, selectedContainer, selectedObjectType, _previousRootDse.HighestCommittedUsn.Value, lastRunsCookie);
                    }
                }

                // Query deleted objects (tombstones) for this partition
                // AD moves deleted objects to CN=Deleted Objects,<partition DN>
                // We query this container separately with the Show Deleted Objects control
                if (_cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("GetDeltaImportObjects: Cancellation requested before querying deleted objects. Stopping");
                    return result;
                }

                GetDeletedObjectsUsingUsn(result, selectedPartition, _previousRootDse.HighestCommittedUsn.Value);
            }
        }
        else if (_previousRootDse.UseAccesslogDeltaImport)
        {
            // For OpenLDAP with accesslog overlay
            if (string.IsNullOrEmpty(_previousRootDse.LastAccesslogTimestamp))
            {
                // The accesslog watermark is not available. This can happen when:
                // - The accesslog has more entries than the server's olcSizeLimit (default 500)
                //   and the bind account cannot bypass the limit (not the accesslog DB rootDN)
                // - The accesslog overlay is not enabled or not accessible
                // - The previous full import failed to capture the watermark
                //
                // Rather than failing, fall back to a full import which will correctly import
                // all objects AND establish the watermark for future delta imports.
                _logger.Warning("GetDeltaImportObjects: Accesslog watermark not available. " +
                    "Falling back to full import to establish baseline. " +
                    "Future delta imports should work normally after this full import completes.");

                result = GetFullImportObjects();
                result.WarningMessage = "Delta import was requested but the accesslog watermark was not available " +
                    "(the cn=accesslog database may have exceeded the server's size limit for the bind account). " +
                    "A full import was performed instead. The watermark has been established and future " +
                    "delta imports should succeed normally.";
                result.WarningErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;
                return result;
            }

            _logger.Debug("GetDeltaImportObjects: Using accesslog-based delta import. Previous timestamp: {PreviousTimestamp}",
                _previousRootDse.LastAccesslogTimestamp);

            GetDeltaResultsUsingAccesslog(result, _previousRootDse.LastAccesslogTimestamp);
        }
        else
        {
            // For generic changelog-based directories (Oracle, 389DS, etc.)
            if (!_previousRootDse.LastChangeNumber.HasValue)
            {
                throw new CannotPerformDeltaImportException("Previous changelog number not available. Run a full import first.");
            }

            _logger.Debug("GetDeltaImportObjects: Using changelog-based delta import. Previous ChangeNumber: {PreviousChange}",
                _previousRootDse.LastChangeNumber);

            GetDeltaResultsUsingChangelog(result, _previousRootDse.LastChangeNumber.Value);
        }

        return result;
    }

    /// <summary>
    /// Returns the partitions to import from. If the run profile specifies a partition, only that
    /// partition is returned. Otherwise, all selected partitions on the connected system are returned.
    /// </summary>
    private IEnumerable<ConnectedSystemPartition> GetTargetPartitions()
    {
        if (_connectedSystemRunProfile.Partition != null)
        {
            _logger.Debug("GetTargetPartitions: Run profile targets specific partition: {PartitionName}",
                LogSanitiser.Sanitise(_connectedSystemRunProfile.Partition.Name));
            return [_connectedSystemRunProfile.Partition];
        }

        _logger.Debug("GetTargetPartitions: No partition specified on run profile, importing from all selected partitions");
        return _connectedSystem.Partitions!.Where(p => p.Selected);
    }

    #region private methods
    /// <summary>
    /// For directories that support changelog.
    /// </summary>
    private int? QueryDirectoryForLastChangeNumber(int lastChangeNumber)
    {
        // TODO: this needs optimising. If we pass in zero, do we really want to have to enumerate all changes to get the last change number?
        // TODO: make sure this works with a range of directory implementations.

        var ldapFilter = $"(&(!(cn=changelog))(changeNumber>={lastChangeNumber}))";
        var ldapRequest = new SearchRequest("cn=changelog", ldapFilter, SearchScope.Subtree);

        try
        {
            var ldapResponse = (SearchResponse)_connection.SendRequest(ldapRequest);
            if (ldapResponse == null)
            {
                _logger.Warning("QueryDirectoryForLastChangeNumber: ldapResponse is null");
                return 0;
            }

            if (ldapResponse.ResultCode != ResultCode.Success || ldapResponse.Entries.Count == 0)
            {
                _logger.Warning($"QueryDirectoryForLastChangeNumber: Didn't get an expected result. Result code: {ldapResponse.ResultCode}, entries: {ldapResponse.Entries.Count}");
                return 0;
            }

            // this is a valid result
            var index = 0;
            if (ldapResponse.Entries.Count > 1)
                index = ldapResponse.Entries.Count - 1;

            var lastChangeEntry = ldapResponse.Entries[index];
            return LdapConnectorUtilities.GetEntryAttributeIntValue(lastChangeEntry, "changenumber");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "QueryDirectoryForLastChangeNumber: Unhandled exception");
            return 0;
        }
    }

    /// <summary>
    /// Queries the OpenLDAP accesslog overlay (cn=accesslog) for the latest reqStart timestamp.
    /// This establishes the watermark for the next delta import.
    ///
    /// Strategy:
    /// 1. Try server-side sort (reverse by reqStart) with SizeLimit=1 to get only the latest entry.
    ///    This is the most efficient approach but requires the sssvlv overlay to be enabled.
    /// 2. If sort is not supported, fall back to a simple query that handles size limit exceeded
    ///    by extracting partial results from the exception response.
    ///
    /// OpenLDAP enforces olcSizeLimit (default 500) as a hard cap for non-rootDN clients, even
    /// with paging controls. The bind account used by the connector is typically not the rootDN
    /// of the cn=accesslog database, so paging alone cannot bypass the limit. The strategies
    /// above are designed to work within this constraint.
    /// </summary>
    private string? QueryAccesslogForLatestTimestamp()
    {
        try
        {
            // Strategy 1: Server-side sort (reverse) with SizeLimit=1
            // This gets only the single latest entry, avoiding size limit issues entirely.
            var result = QueryAccesslogWithServerSideSort();
            if (result != null)
                return result;

            // Strategy 2: Simple query with size limit exceeded handling.
            // If the accesslog has fewer entries than olcSizeLimit, this returns all entries normally.
            // If it exceeds the limit, we catch the DirectoryOperationException and extract
            // the latest timestamp from the partial results in the exception's response.
            return QueryAccesslogWithSizeLimitHandling();
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
    private string? QueryAccesslogWithServerSideSort()
    {
        try
        {
            var request = new SearchRequest("cn=accesslog",
                "(&(objectClass=auditWriteObject)(reqResult=0))",
                SearchScope.OneLevel,
                "reqStart");

            // Request reverse sort by reqStart so the latest entry comes first
            var sortControl = new SortRequestControl(new SortKey("reqStart", "caseIgnoreOrderingMatch", true));
            sortControl.IsCritical = true;
            request.Controls.Add(sortControl);
            request.SizeLimit = 1;

            var response = (SearchResponse)_connection.SendRequest(request, _searchTimeout);
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
            // Matching rule not supported for this attribute — fall back
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
    private string? QueryAccesslogWithSizeLimitHandling()
    {
        string? latestTimestamp = null;
        var totalEntries = 0;
        var iterations = 0;
        const int maxIterations = 100; // Safety limit to prevent infinite loops

        // Start with an unfiltered query to get the first batch
        var currentFilter = "(&(objectClass=auditWriteObject)(reqResult=0))";

        while (iterations < maxIterations)
        {
            iterations++;
            string? batchLatest;
            int batchCount;
            var hitSizeLimit = false;

            try
            {
                var request = new SearchRequest("cn=accesslog", currentFilter,
                    SearchScope.OneLevel, "reqStart");

                var response = (SearchResponse)_connection.SendRequest(request, _searchTimeout);
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
                break; // Got all results without hitting the limit — done

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

    private LdapConnectorRootDse GetRootDseInformation()
    {
        var request = new SearchRequest()
        {
            Scope = SearchScope.Base,
        };

        request.Attributes.AddRange(new[] {
            "DNSHostName",
            "HighestCommittedUSN",
            "supportedCapabilities",
            "vendorName",
            "structuralObjectClass"
        });

        var response = (SearchResponse)_connection.SendRequest(request);

        if (response == null)
            throw new LdapCommunicationException("LDAP response was null when querying directory information.");

        if (response.ResultCode != ResultCode.Success)
            throw new LdapCommunicationException($"LDAP request failed with result code {response.ResultCode} when querying directory information.");

        if (response.Entries.Count == 0)
            throw new LdapCommunicationException("No entries returned from rootDSE query. Verify the LDAP server is reachable and correctly configured.");

        var rootDseEntry = response.Entries[0];

        // Detect directory type from rootDSE capabilities
        var capabilities = LdapConnectorUtilities.GetEntryAttributeStringValues(rootDseEntry, "supportedCapabilities");
        var vendorName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "vendorName");
        var structuralObjectClass = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "structuralObjectClass");
        var directoryType = LdapConnectorUtilities.DetectDirectoryType(capabilities, vendorName, structuralObjectClass);

        var rootDse = new LdapConnectorRootDse
        {
            DnsHostName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "DNSHostName"),
            HighestCommittedUsn = LdapConnectorUtilities.GetEntryAttributeLongValue(rootDseEntry, "HighestCommittedUSN"),
            DirectoryType = directoryType,
            VendorName = vendorName
        };

        // For non-AD directories, capture the current delta watermark.
        // This must run during BOTH full and delta imports:
        // - Full import: establishes the baseline watermark for the first delta import
        // - Delta import: captures the current position so the next delta starts from here
        if (!rootDse.UseUsnDeltaImport)
        {
            if (rootDse.UseAccesslogDeltaImport)
            {
                // OpenLDAP: query cn=accesslog for the latest reqStart timestamp
                rootDse.LastAccesslogTimestamp = QueryAccesslogForLatestTimestamp();
            }
            else
            {
                // Generic/Oracle: query cn=changelog for the latest changeNumber
                rootDse.LastChangeNumber = QueryDirectoryForLastChangeNumber(0);
            }
        }

        _logger.Information("GetRootDseInformation: Directory capabilities detected. DirectoryType={DirectoryType}, VendorName={VendorName}, SupportsPaging={SupportsPaging}, HighestUSN={Usn}, LastChangeNumber={ChangeNum}, LastAccesslogTimestamp={AccesslogTs}",
            rootDse.DirectoryType, rootDse.VendorName ?? "(not set)", rootDse.SupportsPaging, rootDse.HighestCommittedUsn, rootDse.LastChangeNumber, rootDse.LastAccesslogTimestamp ?? "(not set)");
        return rootDse;
    }

    private void GetFisoResults(ConnectedSystemImportResult connectedSystemImportResult, ConnectedSystemContainer connectedSystemContainer, ConnectedSystemObjectType connectedSystemObjectType, byte[]? lastRunsCookie)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetFisoResults: O1 Cancellation requested. Stopping");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var ldapFilter = $"(objectClass={connectedSystemObjectType.Name})"; // todo: add in implicit support for returning containers/organisational units?

        // add user selected attributes
        var attributes = connectedSystemObjectType.Attributes.Where(a => a.Selected).Select(a => a.Name).ToList();

        // ensure we are also retrieving the unique identifier attribute(s)
        attributes.AddRange(connectedSystemObjectType.Attributes.Where(a => a.IsExternalId).Select(a => a.Name));

        // ensure we also retrieve the secondary external ID attribute (e.g., distinguishedName) so that
        // export confirmation can verify DN changes (moves/renames) were applied successfully
        attributes.AddRange(connectedSystemObjectType.Attributes.Where(a => a.IsSecondaryExternalId).Select(a => a.Name));

        // we also need the objectClass for type matching purposes
        attributes.Add("objectClass");

        // remove any duplicates we might have added and change to a simple array for use with the search request
        var queryAttributes = attributes.Distinct().ToArray();

        var searchRequest = new SearchRequest(connectedSystemContainer.ExternalId, ldapFilter, SearchScope.Subtree, queryAttributes);

        // Only add paging control if the directory supports it
        // Samba AD claims AD compatibility but returns duplicate results when using paging cookies
        var supportsPaging = _currentRootDse?.SupportsPaging ?? true; // Default to true for backwards compatibility
        if (supportsPaging)
        {
            var pageResultRequestControl = new PageResultRequestControl(_connectedSystemRunProfile.PageSize)
            {
                // Make paging non-critical so servers that don't support paging can ignore it
                IsCritical = false
            };
            if (lastRunsCookie is { Length: > 0 })
                pageResultRequestControl.Cookie = lastRunsCookie;

            searchRequest.Controls.Add(pageResultRequestControl);
        }
        else
        {
            _logger.Debug("GetFisoResults: Paging disabled for this directory (VendorName={VendorName}). Retrieving all results in single request.",
                _currentRootDse?.VendorName ?? "unknown");
        }

        SearchResponse searchResponse;
        try
        {
            searchResponse = (SearchResponse)_connection.SendRequest(searchRequest, _searchTimeout);
        }
        catch (DirectoryOperationException ex) when (lastRunsCookie is { Length: > 0 } &&
            ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
        {
            // Server returned a cookie on first page but doesn't actually support paging (e.g., Samba AD)
            // Retry without paging control - results should have already been returned on first page
            _logger.Warning("GetFisoResults: Server rejected paging cookie, assuming all results were returned on first page. Error: {Message}", ex.Message);
            return;
        }

        // Only track pagination tokens if paging is supported
        // For directories without paging support, all results are returned in a single request
        if (supportsPaging && searchResponse.Controls != null && searchResponse.Controls.SingleOrDefault(c => c is PageResultResponseControl) is PageResultResponseControl pageResultResponseControl && pageResultResponseControl.Cookie.Length > 0)
        {
            var tokenName = LdapConnectorUtilities.GetPaginationTokenName(connectedSystemContainer, connectedSystemObjectType);
            connectedSystemImportResult.PaginationTokens.Add(new ConnectedSystemPaginationToken(tokenName, pageResultResponseControl.Cookie));
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetFisoResults: O2 Cancellation requested. Stopping");
            return;
        }

        // Use NotSet for Full Imports - JIM will determine Create vs Update based on CSO existence.
        // Only delta imports with change tracking should specify explicit Create/Update/Delete.
        connectedSystemImportResult.ImportObjects.AddRange(ConvertLdapResults(searchResponse.Entries, ObjectChangeType.NotSet));
        stopwatch.Stop();
        _logger.Debug($"GetFisoResults: Executed for object type '{connectedSystemObjectType.Name}' within container '{connectedSystemContainer.Name}' in {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Gets delta results for Active Directory using USN-based change tracking.
    /// Queries for objects where uSNChanged is greater than the previous watermark.
    /// </summary>
    private void GetDeltaResultsUsingUsn(ConnectedSystemImportResult result, ConnectedSystemContainer container, ConnectedSystemObjectType objectType, long previousUsn, byte[]? lastRunsCookie)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeltaResultsUsingUsn: Cancellation requested. Stopping");
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // Build filter for objects changed since last USN, of the specified object type
        // uSNChanged is a 64-bit integer stored as a string
        var ldapFilter = $"(&(objectClass={objectType.Name})(uSNChanged>={previousUsn + 1}))";

        // Build attribute list
        var attributes = objectType.Attributes.Where(a => a.Selected).Select(a => a.Name).ToList();
        attributes.AddRange(objectType.Attributes.Where(a => a.IsExternalId).Select(a => a.Name));
        attributes.AddRange(objectType.Attributes.Where(a => a.IsSecondaryExternalId).Select(a => a.Name));
        attributes.Add("objectClass");
        attributes.Add("isDeleted"); // To detect deleted objects (when searching deleted objects container)
        var queryAttributes = attributes.Distinct().ToArray();

        var searchRequest = new SearchRequest(container.ExternalId, ldapFilter, SearchScope.Subtree, queryAttributes);

        // Only add paging control if the directory supports it
        var supportsPaging = _currentRootDse?.SupportsPaging ?? true;
        if (supportsPaging)
        {
            var pageResultRequestControl = new PageResultRequestControl(_connectedSystemRunProfile.PageSize)
            {
                // Make paging non-critical so servers that don't support paging can ignore it
                IsCritical = false
            };
            if (lastRunsCookie is { Length: > 0 })
                pageResultRequestControl.Cookie = lastRunsCookie;

            searchRequest.Controls.Add(pageResultRequestControl);
        }

        SearchResponse searchResponse;
        try
        {
            searchResponse = (SearchResponse)_connection.SendRequest(searchRequest, _searchTimeout);
        }
        catch (DirectoryOperationException ex) when (lastRunsCookie is { Length: > 0 } &&
            ex.Message.Contains("does not support the control", StringComparison.OrdinalIgnoreCase))
        {
            // Server returned a cookie on first page but doesn't actually support paging (e.g., Samba AD)
            // Retry without paging control - results should have already been returned on first page
            _logger.Warning("GetDeltaResultsUsingUsn: Server rejected paging cookie, assuming all results were returned on first page. Error: {Message}", ex.Message);
            return;
        }

        // Handle pagination - only if paging is supported
        if (supportsPaging && searchResponse.Controls != null &&
            searchResponse.Controls.SingleOrDefault(c => c is PageResultResponseControl) is PageResultResponseControl pageResultResponseControl &&
            pageResultResponseControl.Cookie.Length > 0)
        {
            var tokenName = LdapConnectorUtilities.GetPaginationTokenName(container, objectType);
            result.PaginationTokens.Add(new ConnectedSystemPaginationToken(tokenName, pageResultResponseControl.Cookie));
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeltaResultsUsingUsn: Cancellation requested after search. Stopping");
            return;
        }

        // USN-based delta imports cannot distinguish Create vs Update, only that something changed.
        // Use NotSet so JIM determines the actual change type based on CSO existence.
        result.ImportObjects.AddRange(ConvertLdapResults(searchResponse.Entries, ObjectChangeType.NotSet));

        stopwatch.Stop();
        _logger.Debug("GetDeltaResultsUsingUsn: Found {Count} changed objects for type '{ObjectType}' in container '{Container}' (USN > {Usn}) in {Elapsed}",
            searchResponse.Entries.Count, objectType.Name, container.Name, previousUsn, stopwatch.Elapsed);
    }

    /// <summary>
    /// Gets deleted objects (tombstones) from the AD Deleted Objects container using USN-based change tracking.
    /// This enables delta imports to detect deletions that occurred since the last import.
    ///
    /// When AD deletes an object, it:
    /// 1. Moves the object to CN=Deleted Objects,&lt;partition DN&gt;
    /// 2. Sets isDeleted=TRUE
    /// 3. Strips most attributes, keeping only objectGUID, objectSid, distinguishedName (mangled), lastKnownParent
    /// 4. Updates uSNChanged
    ///
    /// We use the LDAP_SERVER_SHOW_DELETED_OID control to query this container.
    /// </summary>
    private void GetDeletedObjectsUsingUsn(ConnectedSystemImportResult result, ConnectedSystemPartition partition, long previousUsn)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeletedObjectsUsingUsn: Cancellation requested. Stopping");
            return;
        }

        if (_connectedSystem.ObjectTypes == null)
        {
            _logger.Warning("GetDeletedObjectsUsingUsn: ObjectTypes is null. Cannot query deleted objects.");
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // Build the Deleted Objects container DN for this partition
        // Format: CN=Deleted Objects,<partition DN>
        var deletedObjectsDn = $"CN=Deleted Objects,{partition.ExternalId}";

        // Build filter for deleted objects changed since last USN
        // We use (isDeleted=TRUE) to only get tombstones, combined with USN filter
        var ldapFilter = $"(&(isDeleted=TRUE)(uSNChanged>={previousUsn + 1}))";

        // Request minimal attributes needed to identify the deleted object
        // Most attributes are stripped from tombstones, but objectGUID is preserved
        // and is the recommended external ID for LDAP connector
        var queryAttributes = new[] { "objectGUID", "objectClass", "isDeleted", "lastKnownParent", "distinguishedName" };

        var searchRequest = new SearchRequest(deletedObjectsDn, ldapFilter, SearchScope.Subtree, queryAttributes);

        // Add the Show Deleted Objects control - this is required to search the Deleted Objects container
        var showDeletedControl = new DirectoryControl(
            LdapConnectorConstants.LDAP_SERVER_SHOW_DELETED_OID,
            null,
            true,  // IsCritical - server must support this for the query to work
            true); // ServerSide
        searchRequest.Controls.Add(showDeletedControl);

        SearchResponse searchResponse;
        try
        {
            searchResponse = (SearchResponse)_connection.SendRequest(searchRequest, _searchTimeout);
        }
        catch (DirectoryOperationException ex)
        {
            // The Show Deleted Objects control may not be supported by all directories
            // (e.g., some Samba AD configurations). Log and continue without failing the import.
            _logger.Warning("GetDeletedObjectsUsingUsn: Failed to query Deleted Objects container. " +
                "The directory may not support the Show Deleted Objects control. Error: {Message}", ex.Message);
            return;
        }
        catch (LdapException ex) when (ex.ErrorCode == 32) // NoSuchObject
        {
            // The Deleted Objects container may not exist in some configurations
            _logger.Debug("GetDeletedObjectsUsingUsn: Deleted Objects container not found at {Dn}. Skipping deletion detection.", deletedObjectsDn);
            return;
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            _logger.Debug("GetDeletedObjectsUsingUsn: Cancellation requested after search. Stopping");
            return;
        }

        // Process each deleted object (tombstone)
        var deletedCount = 0;
        foreach (SearchResultEntry entry in searchResponse.Entries)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetDeletedObjectsUsingUsn: Cancellation requested during processing. Stopping");
                return;
            }

            // Get the objectGUID - this is the stable identifier for matching to existing CSOs
            var objectGuid = LdapConnectorUtilities.GetEntryAttributeGuidValues(entry, "objectGUID")?.FirstOrDefault();
            if (objectGuid == null || objectGuid == Guid.Empty)
            {
                _logger.Warning("GetDeletedObjectsUsingUsn: Deleted object has no objectGUID. DN: {Dn}", entry.DistinguishedName);
                continue;
            }

            // Determine the object type from objectClass
            // Tombstones retain their objectClass hierarchy
            var objectClasses = LdapConnectorUtilities.GetEntryAttributeStringValues(entry, "objectClass");
            if (objectClasses == null || objectClasses.Count == 0)
            {
                _logger.Warning("GetDeletedObjectsUsingUsn: Deleted object has no objectClass. DN: {Dn}", entry.DistinguishedName);
                continue;
            }

            // Find the matching object type from our schema
            ConnectedSystemObjectType? objectType = null;
            foreach (var objectClass in objectClasses)
            {
                objectType = _connectedSystem.ObjectTypes.FirstOrDefault(ot =>
                    ot.Selected && ot.Name.Equals(objectClass, StringComparison.OrdinalIgnoreCase));
                if (objectType != null)
                    break;
            }

            if (objectType == null)
            {
                // This tombstone is for an object type we're not importing - skip it
                _logger.Verbose("GetDeletedObjectsUsingUsn: Skipping deleted object with unselected object type. Classes: {Classes}", string.Join(",", objectClasses));
                continue;
            }

            // Create an import object with Delete change type
            var importObject = new ConnectedSystemImportObject
            {
                ObjectType = objectType.Name,
                ChangeType = ObjectChangeType.Deleted
            };

            // Add the objectGUID as an attribute so JIM can match this to the existing CSO
            // The external ID attribute for the LDAP connector is typically objectGUID
            var guidAttribute = objectType.Attributes.FirstOrDefault(a => a.IsExternalId && a.Type == AttributeDataType.Guid);
            if (guidAttribute != null)
            {
                importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                {
                    Name = guidAttribute.Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = new List<Guid> { objectGuid.Value }
                });
            }
            else
            {
                // Fallback: try to add objectGUID directly if it's a selected attribute
                var objectGuidAttr = objectType.Attributes.FirstOrDefault(a =>
                    a.Name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase));
                if (objectGuidAttr != null)
                {
                    importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                    {
                        Name = "objectGUID",
                        Type = AttributeDataType.Guid,
                        GuidValues = new List<Guid> { objectGuid.Value }
                    });
                }
            }

            result.ImportObjects.Add(importObject);
            deletedCount++;

            _logger.Debug("GetDeletedObjectsUsingUsn: Detected deleted {ObjectType} with objectGUID {Guid}",
                objectType.Name, objectGuid);
        }

        stopwatch.Stop();
        _logger.Information("GetDeletedObjectsUsingUsn: Found {Count} deleted objects in partition '{Partition}' (USN > {Usn}) in {Elapsed}",
            deletedCount, partition.Name, previousUsn, stopwatch.Elapsed);
    }

    /// <summary>
    /// Gets delta results for changelog-based directories (e.g., OpenLDAP, Oracle Directory).
    /// Queries the cn=changelog container for changes since the last change number.
    /// </summary>
    private void GetDeltaResultsUsingChangelog(ConnectedSystemImportResult result, int previousChangeNumber)
    {
        _logger.Debug("GetDeltaResultsUsingChangelog: Querying for changes since changeNumber {PreviousChange}", previousChangeNumber);

        var ldapFilter = $"(&(!(cn=changelog))(changeNumber>{previousChangeNumber}))";
        var ldapRequest = new SearchRequest("cn=changelog", ldapFilter, SearchScope.Subtree,
            "changeNumber", "changeType", "targetDN", "changes");

        try
        {
            var ldapResponse = (SearchResponse)_connection.SendRequest(ldapRequest, _searchTimeout);

            if (ldapResponse == null || ldapResponse.ResultCode != ResultCode.Success)
            {
                _logger.Warning("GetDeltaResultsUsingChangelog: Failed to query changelog. ResultCode: {ResultCode}",
                    ldapResponse?.ResultCode);
                return;
            }

            _logger.Debug("GetDeltaResultsUsingChangelog: Found {Count} changelog entries", ldapResponse.Entries.Count);

            foreach (SearchResultEntry changeEntry in ldapResponse.Entries)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("GetDeltaResultsUsingChangelog: Cancellation requested. Stopping");
                    return;
                }

                var changeType = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, "changeType");
                var targetDn = LdapConnectorUtilities.GetEntryAttributeStringValue(changeEntry, "targetDN");

                if (string.IsNullOrEmpty(targetDn))
                    continue;

                // Map changelog changeType to ObjectChangeType
                // Changelog provides explicit change types, so we use them directly.
                // Unknown types fall back to NotSet so JIM determines based on CSO existence.
                var objectChangeType = changeType?.ToLowerInvariant() switch
                {
                    "add" => ObjectChangeType.Added,
                    "modify" => ObjectChangeType.Updated,
                    "delete" => ObjectChangeType.Deleted,
                    "modrdn" or "moddn" => ObjectChangeType.Updated,
                    _ => ObjectChangeType.NotSet
                };

                // For deletes, we can create a minimal import object
                if (objectChangeType == ObjectChangeType.Deleted)
                {
                    var deleteObject = new ConnectedSystemImportObject
                    {
                        ChangeType = ObjectChangeType.Deleted,
                        // Note: For deletes, we need the DN as the identifier
                        // The synchronisation engine will need to match this to existing objects
                    };
                    result.ImportObjects.Add(deleteObject);
                }
                else
                {
                    // For adds/modifies, we need to fetch the current state of the object
                    var currentObject = GetObjectByDn(targetDn, objectChangeType);
                    if (currentObject != null)
                    {
                        result.ImportObjects.Add(currentObject);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetDeltaResultsUsingChangelog: Error querying changelog");
        }
    }

    /// <summary>
    /// Gets delta results using OpenLDAP's accesslog overlay (slapo-accesslog).
    /// Queries cn=accesslog for write operations that occurred after the previous watermark timestamp.
    /// For each change, fetches the current state of the affected object.
    ///
    /// Handles the server-side size limit (olcSizeLimit, default 500) by iterating through batches:
    /// when the size limit is exceeded, the latest timestamp from partial results is used to narrow
    /// the next query, effectively walking forward through the accesslog until all changes are found.
    /// </summary>
    private void GetDeltaResultsUsingAccesslog(ConnectedSystemImportResult result, string previousTimestamp)
    {
        _logger.Debug("GetDeltaResultsUsingAccesslog: Querying for changes since {PreviousTimestamp}", previousTimestamp);

        var currentTimestamp = previousTimestamp;
        var totalEntries = 0;
        var iterations = 0;
        const int maxIterations = 100; // Safety limit
        // Track processed DNs+timestamps to avoid duplicates when iterating with >= filters
        var processedEntries = new HashSet<string>(StringComparer.Ordinal);

        while (iterations < maxIterations)
        {
            iterations++;

            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("GetDeltaResultsUsingAccesslog: Cancellation requested. Stopping");
                return;
            }

            // LDAP only supports >= (not >), so we use >= and skip already-processed entries in code.
            var ldapFilter = $"(&(objectClass=auditWriteObject)(reqResult=0)(reqStart>={currentTimestamp}))";
            var request = new SearchRequest("cn=accesslog", ldapFilter, SearchScope.OneLevel,
                "reqStart", "reqType", "reqDN");

            SearchResponse response;
            var hitSizeLimit = false;

            try
            {
                response = (SearchResponse)_connection.SendRequest(request, _searchTimeout);

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
                // Size limit exceeded — process the partial results we got
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
                if (_cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("GetDeltaResultsUsingAccesslog: Cancellation requested. Stopping");
                    return;
                }

                var reqStart = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqStart");
                var reqType = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqType");
                var reqDn = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "reqDN");

                if (string.IsNullOrEmpty(reqDn) || string.IsNullOrEmpty(reqStart))
                    continue;

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

                if (objectChangeType == ObjectChangeType.Deleted)
                {
                    // For deletes, create a minimal import object — the object no longer exists in the directory
                    var deleteObject = new ConnectedSystemImportObject
                    {
                        ChangeType = ObjectChangeType.Deleted,
                    };
                    result.ImportObjects.Add(deleteObject);
                }
                else
                {
                    // For add/modify/modrdn, fetch the current state of the object
                    var currentObject = GetObjectByDn(reqDn, objectChangeType);
                    if (currentObject != null)
                    {
                        result.ImportObjects.Add(currentObject);
                    }
                }
            }

            if (!hitSizeLimit)
                break; // Got all results without hitting the limit — done

            // Size limit was hit. Narrow the query to start from the latest timestamp we've seen
            // to walk forward through the remaining entries.
            if (batchLatestTimestamp == null || batchLatestTimestamp == currentTimestamp)
            {
                // No progress made — all entries have the same timestamp. Cannot narrow further.
                _logger.Warning("GetDeltaResultsUsingAccesslog: Cannot narrow accesslog query further. " +
                    "All {Count} entries in this batch have timestamp {Timestamp}. Some changes may be missed.",
                    response.Entries.Count, currentTimestamp);
                break;
            }

            currentTimestamp = batchLatestTimestamp;
        }

        _logger.Debug("GetDeltaResultsUsingAccesslog: Processed {TotalEntries} change entries in {Iterations} iterations",
            totalEntries, iterations);
    }

    /// <summary>
    /// Fetches a single object by its DN for changelog/accesslog-based delta imports.
    /// </summary>
    private ConnectedSystemImportObject? GetObjectByDn(string dn, ObjectChangeType changeType)
    {
        if (_connectedSystem.ObjectTypes == null)
            return null;

        try
        {
            // Get all selected attributes across all object types
            var allAttributes = _connectedSystem.ObjectTypes
                .Where(ot => ot.Selected)
                .SelectMany(ot => ot.Attributes.Where(a => a.Selected || a.IsExternalId).Select(a => a.Name))
                .Distinct()
                .ToList();
            allAttributes.Add("objectClass");

            var searchRequest = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, allAttributes.ToArray());
            var searchResponse = (SearchResponse)_connection.SendRequest(searchRequest, _searchTimeout);

            if (searchResponse.Entries.Count == 0)
            {
                _logger.Verbose("GetObjectByDn: Object not found at DN {Dn}", dn);
                return null;
            }

            var results = ConvertLdapResults(searchResponse.Entries, changeType).ToList();
            return results.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "GetObjectByDn: Error fetching object at DN {Dn}", dn);
            return null;
        }
    }

    private IEnumerable<ConnectedSystemImportObject> ConvertLdapResults(SearchResultEntryCollection searchResults, ObjectChangeType changeType)
    {
        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("_connectedSystem.ObjectTypes is null. Cannot continue.");

        var importObjects = new List<ConnectedSystemImportObject>();

        // todo: experiment with parallel foreach to see if we can speed up processing
        foreach (SearchResultEntry searchResult in searchResults)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Information("ConvertLdapResults: Cancellation requested. Stopping.");
                return importObjects;
            }

            // start to build the object that will represent the object in the connected system. we will pass this back to JIM
            var importObject = new ConnectedSystemImportObject
            {
                ChangeType = changeType
            };

            // work out what JIM object type this result is
            // AD returns objectClass values from most specific to most general (e.g., user, organizationalPerson, person, top)
            // We need to find the most specific object type that we have selected in our schema
            var objectClasses = (string[])searchResult.Attributes["objectclass"].GetValues(typeof(string));
            ConnectedSystemObjectType? objectType = null;
            foreach (var objectClass in objectClasses)
            {
                objectType = _connectedSystem.ObjectTypes.FirstOrDefault(ot =>
                    ot.Selected && ot.Name.Equals(objectClass, StringComparison.OrdinalIgnoreCase));
                if (objectType != null)
                    break;
            }
            if (objectType == null)
            {
                importObject.ErrorType = ConnectedSystemImportObjectError.CouldNotDetermineObjectType;
                importObject.ErrorMessage = $"ConvertLdapResults: Couldn't match object type to object classes received: {string.Join(',', objectClasses)}";
                importObjects.Add(importObject);
                continue;
            }
            else
            {
                importObject.ObjectType = objectType.Name;
            }

            // start populating import object attribute values from the search result
            foreach (string attributeName in searchResult.Attributes.AttributeNames)
            {
                // get the schema attribute for this search result attribute, so we can work out what type it is
                var schemaAttribute = objectType.Attributes.SingleOrDefault(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
                if (schemaAttribute == null)
                {
                    importObject.ErrorType = ConnectedSystemImportObjectError.ConfigurationError;
                    importObject.ErrorMessage = $"Search result attribute '{attributeName}' not found in schema!";
                    break;
                }

                var importObjectAttribute = new ConnectedSystemImportObjectAttribute
                {
                    Name = attributeName,
                    Type = schemaAttribute.Type
                };

                // assign the right type of value(s)
                switch (importObjectAttribute.Type)
                {
                    case AttributeDataType.Text:
                        var stringValues = LdapConnectorUtilities.GetEntryAttributeStringValues(searchResult, attributeName);
                        if (stringValues is { Count: > 0 })
                            importObjectAttribute.StringValues.AddRange(stringValues);
                        break;

                    case AttributeDataType.Number:
                        var numberValues = LdapConnectorUtilities.GetEntryAttributeIntValues(searchResult, attributeName);
                        if (numberValues is { Count: > 0 })
                            importObjectAttribute.IntValues.AddRange(numberValues);
                        break;

                    case AttributeDataType.LongNumber:
                        var longNumberValues = LdapConnectorUtilities.GetEntryAttributeLongValues(searchResult, attributeName);
                        if (longNumberValues is { Count: > 0 })
                        {
                            // Filter out protected attribute default values.
                            // AD has "protected" attributes that cannot be cleared — they store a sentinel
                            // value instead of null (e.g., accountExpires uses 9223372036854775807 for "never expires").
                            // On export, JIM substitutes null → sentinel. On import, we reverse that:
                            // sentinel → null (by not importing the value), so JIM consistently sees null
                            // for "no value" and drift detection doesn't produce false positives.
                            var protectedDefault = LdapConnectorExport.GetProtectedAttributeDefault(attributeName);
                            if (protectedDefault != null && long.TryParse(protectedDefault, out var defaultLongValue))
                            {
                                longNumberValues = longNumberValues.Where(v => v != defaultLongValue).ToList();
                            }

                            if (longNumberValues.Count > 0)
                                importObjectAttribute.LongValues.AddRange(longNumberValues);
                        }
                        break;

                    case AttributeDataType.Boolean:
                        importObjectAttribute.BoolValue = LdapConnectorUtilities.GetEntryAttributeBooleanValue(searchResult, attributeName);
                        break;

                    case AttributeDataType.DateTime:
                        importObjectAttribute.DateTimeValue = LdapConnectorUtilities.GetEntryAttributeDateTimeValue(searchResult, attributeName);
                        break;

                    case AttributeDataType.Guid:
                        var guidValues = LdapConnectorUtilities.GetEntryAttributeGuidValues(searchResult, attributeName);
                        if (guidValues is { Count: > 0 })
                            importObjectAttribute.GuidValues.AddRange(guidValues);
                        break;

                    case AttributeDataType.Binary:
                        var binaryValues = LdapConnectorUtilities.GetEntryAttributeBinaryValues(searchResult, attributeName);
                        if (binaryValues is { Count: > 0 })
                            importObjectAttribute.ByteValues.AddRange(binaryValues);
                        break;

                    case AttributeDataType.Reference:
                        var referenceValues = LdapConnectorUtilities.GetEntryAttributeStringValues(searchResult, attributeName);
                        if (referenceValues is { Count: > 0 })
                        {
                            // Filter out the placeholder member DN so it never enters the metaverse.
                            // The placeholder is injected by the connector during export to satisfy the
                            // groupOfNames MUST member constraint — it should be invisible to JIM.
                            var filteredValues = referenceValues.Where(v =>
                                !_placeholderMemberDn.Equals(v, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (filteredValues.Count > 0)
                                importObjectAttribute.ReferenceValues.AddRange(filteredValues);
                            else if (referenceValues.Count > filteredValues.Count)
                                _logger.Debug("LdapConnectorImport: Filtered placeholder member '{Placeholder}' from attribute '{Attr}' on '{Dn}'",
                                    _placeholderMemberDn, attributeName, searchResult.DistinguishedName);
                        }
                        break;
                    case AttributeDataType.NotSet:
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                importObject.Attributes.Add(importObjectAttribute);
            }

            // Synthesise distinguishedName for directories that don't return it as an attribute.
            // OpenLDAP (and most RFC-compliant directories) expose the DN as the entry's DistinguishedName
            // property, not as a searchable/importable attribute. The connector schema synthesises
            // distinguishedName as an attribute (for DN-based provisioning), so we need to populate it
            // from the entry's DN during import for export confirmation to match correctly.
            if (!importObject.Attributes.Any(a => a.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
                && objectType.Attributes.Any(a => a.Name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase) && a.Selected))
            {
                importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                {
                    Name = "distinguishedName",
                    Type = AttributeDataType.Text,
                    StringValues = { searchResult.DistinguishedName }
                });
            }

            importObjects.Add(importObject);
        }

        return importObjects;
    }
    #endregion
}