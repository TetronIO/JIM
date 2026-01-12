using JIM.Models.Core;
using JIM.Models.Enums;
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

        // enumerate all selected partitions
        foreach (var selectedPartition in _connectedSystem.Partitions.Where(p => p.Selected))
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
            throw new InvalidOperationException("No persisted connector data available. Run a full import first to establish a baseline.");
        }

        _previousRootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(_persistedConnectorData);
        if (_previousRootDse == null)
        {
            _logger.Warning("GetDeltaImportObjects: Could not deserialise persisted connector data.");
            throw new InvalidOperationException("Could not deserialise persisted connector data. Run a full import to re-establish baseline.");
        }

        if (_paginationTokens.Count == 0)
        {
            // Initial page - get the current RootDSE info
            _currentRootDse = GetRootDseInformation();
            result.PersistedConnectorData = JsonSerializer.Serialize(_currentRootDse);
        }

        // Determine which delta strategy to use
        if (_previousRootDse.IsActiveDirectory)
        {
            if (!_previousRootDse.HighestCommittedUsn.HasValue)
            {
                throw new InvalidOperationException("Previous USN watermark not available. Run a full import first.");
            }

            _logger.Debug("GetDeltaImportObjects: Using AD USN-based delta import. Previous USN: {PreviousUsn}",
                _previousRootDse.HighestCommittedUsn);

            // For AD, query objects where uSNChanged > previous HighestCommittedUSN
            foreach (var selectedPartition in _connectedSystem.Partitions.Where(p => p.Selected))
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

                        GetDeltaResultsUsingUsn(result, selectedContainer, selectedObjectType, _previousRootDse.HighestCommittedUsn.Value, lastRunsCookie);
                    }
                }
            }
        }
        else
        {
            // For changelog-based directories
            if (!_previousRootDse.LastChangeNumber.HasValue)
            {
                throw new InvalidOperationException("Previous changelog number not available. Run a full import first.");
            }

            _logger.Debug("GetDeltaImportObjects: Using changelog-based delta import. Previous ChangeNumber: {PreviousChange}",
                _previousRootDse.LastChangeNumber);

            GetDeltaResultsUsingChangelog(result, _previousRootDse.LastChangeNumber.Value);
        }

        return result;
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

    private LdapConnectorRootDse GetRootDseInformation()
    {
        var request = new SearchRequest()
        {
            Scope = SearchScope.Base,
        };

        request.Attributes.AddRange(new[] {
            "DNSHostName",
            "HighestCommittedUSN",
            "supportedCapabilities"
        });

        var response = (SearchResponse)_connection.SendRequest(request);

        if (response == null)
            throw new InvalidOperationException("GetRootDseInformation: LDAP response was null");

        if (response.ResultCode != ResultCode.Success)
            throw new InvalidOperationException($"GetRootDseInformation: LDAP request failed with result code {response.ResultCode}");

        if (response.Entries.Count == 0)
            throw new InvalidOperationException("GetRootDseInformation: No entries returned from rootDSE query");

        var rootDseEntry = response.Entries[0];

        // Check if this is Active Directory by looking for AD capability OIDs
        var capabilities = LdapConnectorUtilities.GetEntryAttributeStringValues(rootDseEntry, "supportedCapabilities");
        var isActiveDirectory = capabilities != null &&
            (capabilities.Contains(LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID) ||
             capabilities.Contains(LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_ADAM_OID));

        var rootDse = new LdapConnectorRootDse
        {
            DnsHostName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "DNSHostName"),
            HighestCommittedUsn = LdapConnectorUtilities.GetEntryAttributeLongValue(rootDseEntry, "HighestCommittedUSN"),
            IsActiveDirectory = isActiveDirectory
        };

        // For non-AD directories, try to get the last change number from the changelog
        if (!isActiveDirectory)
        {
            rootDse.LastChangeNumber = QueryDirectoryForLastChangeNumber(0);
        }

        _logger.Verbose("GetRootDseInformation: Got info. IsActiveDirectory={IsAd}, HighestUSN={Usn}, LastChangeNumber={ChangeNum}",
            rootDse.IsActiveDirectory, rootDse.HighestCommittedUsn, rootDse.LastChangeNumber);
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
        var lastRunsCookieLength = lastRunsCookie != null ? lastRunsCookie.Length.ToString() : "null";
        var ldapFilter = $"(objectClass={connectedSystemObjectType.Name})"; // todo: add in implicit support for returning containers/organisational units?

        // add user selected attributes
        var attributes = connectedSystemObjectType.Attributes.Where(a => a.Selected).Select(a => a.Name).ToList();

        // ensure we are also retrieving the unique identifier attribute(s)
        attributes.AddRange(connectedSystemObjectType.Attributes.Where(a => a.IsExternalId).Select(a => a.Name));

        // we also need the objectClass for type matching purposes
        attributes.Add("objectClass");

        // remove any duplicates we might have added and change to a simple array for use with the search request
        var queryAttributes = attributes.Distinct().ToArray();

        var searchRequest = new SearchRequest(connectedSystemContainer.ExternalId, ldapFilter, SearchScope.Subtree, queryAttributes);
        var pageResultRequestControl = new PageResultRequestControl(_connectedSystemRunProfile.PageSize)
        {
            // Make paging non-critical so servers that don't support paging can ignore it
            IsCritical = false
        };
        if (lastRunsCookie is { Length: > 0 })
            pageResultRequestControl.Cookie = lastRunsCookie;

        searchRequest.Controls.Add(pageResultRequestControl);

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

        // if there's more results, keep track of the paging cookie so we can keep requesting subsequent pages
        if (searchResponse.Controls != null && searchResponse.Controls.SingleOrDefault(c => c is PageResultResponseControl) is PageResultResponseControl pageResultResponseControl && pageResultResponseControl.Cookie.Length > 0)
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
        attributes.Add("objectClass");
        attributes.Add("isDeleted"); // To detect deleted objects (when searching deleted objects container)
        var queryAttributes = attributes.Distinct().ToArray();

        var searchRequest = new SearchRequest(container.ExternalId, ldapFilter, SearchScope.Subtree, queryAttributes);
        var pageResultRequestControl = new PageResultRequestControl(_connectedSystemRunProfile.PageSize)
        {
            // Make paging non-critical so servers that don't support paging can ignore it
            IsCritical = false
        };
        if (lastRunsCookie is { Length: > 0 })
            pageResultRequestControl.Cookie = lastRunsCookie;

        searchRequest.Controls.Add(pageResultRequestControl);

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

        // Handle pagination
        if (searchResponse.Controls != null &&
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
    /// Fetches a single object by its DN for changelog-based delta imports.
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
                            importObjectAttribute.LongValues.AddRange(longNumberValues);
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
                            importObjectAttribute.ReferenceValues.AddRange(referenceValues);
                        break;
                    case AttributeDataType.NotSet:
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                importObject.Attributes.Add(importObjectAttribute);
            }

            importObjects.Add(importObject);
        }

        return importObjects;
    }
    #endregion
}