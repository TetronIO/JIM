using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Text.Json;

namespace JIM.Connectors.LDAP
{
    internal class LdapConnectorImport
    {
        private readonly CancellationToken _cancellationToken;
        private readonly ConnectedSystem _connectedSystem;
        private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
        private readonly ILogger _logger;
        private readonly LdapConnection _connection;
        private readonly List<ConnectedSystemPaginationToken> _paginationTokens;
        private readonly string? _persistedConnectorData;

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
                var rootDseInfo = GetRootDseInformation();

                // serialise the info to json for persistence
                // todo: this will need to change to support delta imports. there will need to be checks done and the connector data
                // only persisted in certain situations (see JIM Processes flowcharts)
                result.PersistedConnectorData = JsonSerializer.Serialize(rootDseInfo);

                // if directory supports USNs (ADDS/ADLDS) then persist hostname and highestcommmittedusn info
                // else if directory supports changelog, then persist hostname and last change number

                // we use the persisted connector data property to store the last known change number in the directory.
                // this enables us to perform a delta-import later on by only importing changes beyond this current value.
                // note: we only want to do this on the initial call into here to avoid the situation where we miss changes
                // made after our initial LDAP query but whilst processing subsequent pages.
                //if (string.IsNullOrEmpty(_persistedConnectorData))
                //    result.PersistedConnectorData = QueryDirectoryForLastChangeNumber(0).ToString();
                //else
                //    result.PersistedConnectorData = QueryDirectoryForLastChangeNumber(int.Parse(_persistedConnectorData)).ToString();
            }

            // enumerate all selected partitions
            foreach (var selectedPartition in _connectedSystem.Partitions.Where(p => p.Selected))
            {
                // enumerate all selected containers in this partition
                foreach (var selectedContainer in ConnectedSystemUtilities.GetAllSelectedContainers(selectedPartition))
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
            // getting more results back per pass than they expect. Consider refactoring the interface between JIM.Service and the connector so it is executed once per selected container
            // so the connector always returns a page of results to the JIM.Service (a page of results per container).

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
                "LastChangeNumber"
            });

            var response = (SearchResponse)_connection.SendRequest(request);
            var rootDseEntry = response.Entries[0];
            var rootDse = new LdapConnectorRootDse
            {
                DnsHostName = LdapConnectorUtilities.GetEntryAttributeStringValue(rootDseEntry, "DNSHostName"),
                HighestCommittedUsn = LdapConnectorUtilities.GetEntryAttributeIntValue(rootDseEntry, "HighestCommittedUSN")
            };

            Log.Verbose("LDAPConnector > GetRootDseInformation: Got info");
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
            var pageResultRequestControl = new PageResultRequestControl(_connectedSystemRunProfile.PageSize);
            if (lastRunsCookie is { Length: > 0 })
                pageResultRequestControl.Cookie = lastRunsCookie;

            searchRequest.Controls.Add(pageResultRequestControl);
            var searchResponse = (SearchResponse)_connection.SendRequest(searchRequest, TimeSpan.FromMinutes(5)); // might want to make this configurable

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

            connectedSystemImportResult.ImportObjects.AddRange(ConvertLdapResults(searchResponse.Entries));
            stopwatch.Stop();
            _logger.Debug($"GetFisoResults: Executed for object type '{connectedSystemObjectType.Name}' within container '{connectedSystemContainer.Name}' in {stopwatch.Elapsed}");
        }

        private List<ConnectedSystemImportObject> ConvertLdapResults(SearchResultEntryCollection searchResults)
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
                // TODO: expand this to support the UPDATE scenario
                var importObject = new ConnectedSystemImportObject
                {
                    ChangeType = ObjectChangeType.Create
                };

                // work out what JIM object type this result is
                var objectClasses = (string[])searchResult.Attributes["objectclass"].GetValues(typeof(string));
                var objectType = _connectedSystem.ObjectTypes.SingleOrDefault(ot => objectClasses.Any(oc => oc.Equals(ot.Name, StringComparison.CurrentCultureIgnoreCase)));
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
                    var schemaAttribute = objectType.Attributes.SingleOrDefault(a => a.Name.Equals(attributeName, StringComparison.InvariantCultureIgnoreCase));
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

                        case AttributeDataType.Boolean:
                            importObjectAttribute.BoolValue = LdapConnectorUtilities.GetEntryAttributeBooleanValue(searchResult, attributeName);
                            break;

                        case AttributeDataType.DateTime:
                            var dateTimeValues = LdapConnectorUtilities.GetEntryAttributeDateTimeValues(searchResult, attributeName);
                            if (dateTimeValues is { Count: > 0 })
                                importObjectAttribute.DateTimeValues.AddRange(dateTimeValues);
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
}
