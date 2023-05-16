using JIM.Application;
using JIM.Models.Core;
using JIM.Models.History;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Service.Processors
{
    internal class SynchronisationImportTaskProcessor
    {
        private readonly JimApplication _jim;
        private readonly IConnector _connector;
        private readonly ConnectedSystem _connectedSystem;
        private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
        private readonly SynchronisationRunHistoryDetail _synchronisationRunHistoryDetail;
        private readonly CancellationTokenSource _cancellationTokenSource;

        internal SynchronisationImportTaskProcessor(
            JimApplication jimApplication,
            IConnector connector,
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile connectedSystemRunProfile,
            SynchronisationRunHistoryDetail synchronisationRunHistoryDetail,
            CancellationTokenSource cancellationTokenSource)
        {
            _jim = jimApplication;
            _connector = connector;
            _connectedSystem = connectedSystem;
            _connectedSystemRunProfile = connectedSystemRunProfile;
            _synchronisationRunHistoryDetail = synchronisationRunHistoryDetail;
            _cancellationTokenSource = cancellationTokenSource;
        }

        internal async Task PerformFullImportAsync()
        {
            if (_connectedSystem.ObjectTypes == null)
                throw new InvalidDataException("PerformFullImportAsync: _connectedSystem.ObjectTypes was null. Cannot continue.");

            if (_connector is IConnectorImportUsingCalls callBasedImportConnector)
            {
                callBasedImportConnector.OpenImportConnection(_connectedSystem.SettingValues, Log.Logger);

                var initialPage = true;
                var paginationTokens = new List<ConnectedSystemPaginationToken>();
                var wereResultsReturned = false;
                while (initialPage || paginationTokens.Count > 0 || wereResultsReturned)
                {
                    // perform the import for this page
                    var result = await callBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, paginationTokens, null, Log.Logger, _cancellationTokenSource.Token);
                    wereResultsReturned = result.ImportObjects.Count > 0;

                    // make sure we pass the pagination tokens back in on the next page (if there is one)
                    paginationTokens = result.PaginationTokens;

                    if (result.PersistedConnectorData != _connectedSystem.PersistedConnectorData)
                    {
                        // the connector wants to persist some data between sync runs. update the connected system with the new value
                        Log.Debug($"ExecuteAsync: updating persisted connector data. old value: '{_connectedSystem.PersistedConnectorData}', new value: '{result.PersistedConnectorData}'");
                        _connectedSystem.PersistedConnectorData = result.PersistedConnectorData;
                        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem);
                    }

                    // decision: do we want to load the whole connector space into memory to maximise performance? for now, let's keep it db-centric.
                    // todo: experiment with using parallel foreach to see if we can speed up processing
                    foreach (var importObject in result.ImportObjects)
                    {
                        // this will store the detail for the import object that will persist in the history for the run
                        var synchronisationRunHistoryDetailItem = new SynchronisationRunHistoryDetailItem();

                        // is this a new, or existing object as far as JIM is aware?
                        // find the unique id attribute for this connected system object type, and then pull out the right type attribute value from the importobject
                        // match the string object type to a name of an object type in the schema..
                        var csObjectType = _connectedSystem.ObjectTypes.SingleOrDefault(q => q.Name.Equals(importObject.ObjectType, StringComparison.OrdinalIgnoreCase));
                        if (csObjectType == null || csObjectType.UniqueIdentifierAttribute == null)
                        {
                            synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.CouldntMatchObjectType;
                            synchronisationRunHistoryDetailItem.ErrorMessage = $"PerformFullImportAsync: Couldn't find connected system ({_connectedSystem.Id}) object type for imported object type: {importObject.ObjectType}";
                            continue;
                        }

                        ConnectedSystemObject? connectedSystemObject;
                        if (csObjectType.UniqueIdentifierAttribute.Type == AttributeDataType.String)
                        {
                            if (string.IsNullOrEmpty(importObject.UniqueIdentifierAttributeStringValue))
                            {
                                // connector has not set a valid unique identifier attribute string value
                                synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.MissingUniqueIdentifierAttributeValue;
                                synchronisationRunHistoryDetailItem.ErrorMessage = $"PerformFullImportAsync: Connector hasn't supplied a valid unique identifier string value.";
                                _synchronisationRunHistoryDetail.Items.Add(synchronisationRunHistoryDetailItem);
                                continue;
                            }

                            connectedSystemObject = await _jim.ConnectedSystems.GetConnectedSystemObjectByUniqueIdAsync(_connectedSystem.Id, csObjectType.UniqueIdentifierAttribute.Id, importObject.UniqueIdentifierAttributeStringValue);
                        }
                        else if (csObjectType.UniqueIdentifierAttribute.Type == AttributeDataType.Number)
                        {
                            if (importObject.UniqueIdentifierIntValue == null || importObject.UniqueIdentifierIntValue < 1)
                            {
                                // connector has not set a valid unique identifier attribute int value
                                synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.MissingUniqueIdentifierAttributeValue;
                                synchronisationRunHistoryDetailItem.ErrorMessage = $"PerformFullImportAsync: Connector hasn't supplied a valid unique identifier int value.";
                                _synchronisationRunHistoryDetail.Items.Add(synchronisationRunHistoryDetailItem);
                                continue;
                            }

                            connectedSystemObject = await _jim.ConnectedSystems.GetConnectedSystemObjectByUniqueIdAsync(_connectedSystem.Id, csObjectType.UniqueIdentifierAttribute.Id, (int)importObject.UniqueIdentifierIntValue);
                        }
                        else if (csObjectType.UniqueIdentifierAttribute.Type == AttributeDataType.Guid)
                        {
                            if (importObject.UniqueIdentifierAttributeGuidValue == null || importObject.UniqueIdentifierAttributeGuidValue == Guid.Empty)
                            {
                                // connector has not set a valid unique identifier attribute guid value
                                synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.MissingUniqueIdentifierAttributeValue;
                                synchronisationRunHistoryDetailItem.ErrorMessage = $"PerformFullImportAsync: Connector hasn't supplied a valid unique identifier guid value.";
                                _synchronisationRunHistoryDetail.Items.Add(synchronisationRunHistoryDetailItem);
                                continue;
                            }

                            connectedSystemObject = await _jim.ConnectedSystems.GetConnectedSystemObjectByUniqueIdAsync(_connectedSystem.Id, csObjectType.UniqueIdentifierAttribute.Id, (Guid)importObject.UniqueIdentifierAttributeGuidValue);
                        }
                        else
                        {
                            synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.UnsupportedUniqueIdentifierAttribyteType;
                            synchronisationRunHistoryDetailItem.ErrorMessage = $"PerformFullImportAsync: Unsupported connected system object type unique identifier type: {csObjectType.UniqueIdentifierAttribute.Type}";
                            _synchronisationRunHistoryDetail.Items.Add(synchronisationRunHistoryDetailItem);
                            continue;
                        }

                        // todo: process import object and apply connector space changes as necessary
                        // is new - new cso required
                        // is existing - apply any changes to the cso from the import object

                        if (connectedSystemObject == null)
                        {
                            // new object - create connected system object
                            connectedSystemObject = new ConnectedSystemObject
                            {
                                ConnectedSystem = _connectedSystem,
                                UniqueIdentifierAttribute = csObjectType.UniqueIdentifierAttribute,
                                Type = csObjectType
                            };

                            var needToSkipImportObject = false;
                            foreach (var importObjectAttribute in importObject.Attributes)
                            {
                                // find the connected system schema attribute that has the same name
                                var csAttribute = csObjectType.Attributes.SingleOrDefault(q => q.Name.Equals(importObjectAttribute.Name, StringComparison.CurrentCultureIgnoreCase));
                                if (csAttribute == null)
                                {
                                    // unexpected attribute!
                                    synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.UnexpectedAttribute;
                                    synchronisationRunHistoryDetailItem.ErrorMessage = $"Was not expecting the imported object attribute '{importObjectAttribute.Name}'.";
                                    _synchronisationRunHistoryDetail.Items.Add(synchronisationRunHistoryDetailItem);
                                    needToSkipImportObject = true;
                                    break;
                                }
                                
                                // assign the attribute value(s)
                                // remember, jim treats attributes requires an attribute value object for each connected system attribute value, i.e. everything's multi-valued capable
                                switch (csAttribute.Type)
                                {
                                    case AttributeDataType.String:
                                        foreach (var importObjectAttributeStringValue in importObjectAttribute.StringValues)
                                        {
                                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemAttributeValue
                                            {
                                                Attribute = csAttribute,
                                                StringValue = importObjectAttributeStringValue
                                            });
                                        }
                                        break;
                                    case AttributeDataType.Number:
                                        foreach (var importObjectAttributeIntValue in importObjectAttribute.IntValues)
                                        {
                                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemAttributeValue
                                            {
                                                Attribute = csAttribute,
                                                IntValue = importObjectAttributeIntValue
                                            });
                                        }
                                        break;
                                    case AttributeDataType.Binary:
                                        foreach (var importObjectAttributeByteValue in importObjectAttribute.ByteValues)
                                        {
                                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemAttributeValue
                                            {
                                                Attribute = csAttribute,
                                                ByteValue = importObjectAttributeByteValue
                                            });
                                        }
                                        break;
                                    // todo: change import object attribute value to mva. everything should be mva capable, except bool, that would make no sense
                                    case AttributeDataType.Guid:
                                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemAttributeValue
                                        {
                                            Attribute = csAttribute,
                                            GuidValue = importObjectAttribute.GuidValue
                                        });
                                        break;
                                    // todo: change import object attribute value to mva. everything should be mva capable, except bool, that would make no sense
                                    case AttributeDataType.DateTime:
                                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemAttributeValue
                                        {
                                            Attribute = csAttribute,
                                            DateTimeValue = importObjectAttribute.DateTimeValue
                                        });
                                        break;
                                    case AttributeDataType.Bool:
                                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemAttributeValue
                                        {
                                            Attribute = csAttribute,
                                            BoolValue = importObjectAttribute.BoolValue
                                        });
                                        break;
                                    //case AttributeDataType.Reference:
                                    //    break;
                                }
                            }

                            if (needToSkipImportObject)
                                continue;

                            // persist the new cso
                            await _jim.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject);
                        }
                        else
                        {
                            // existing connected system object - update from import object if necessary
                        }
                    }

                    // process deletes - what wasn't imported?
                    // make sure it doesn't apply deletes if no objects were imported, as this suggests there was a problem collecting data from the connected system?

                    if (initialPage)
                        initialPage = false;

                    // update the history item with the results from this page
                    await _jim.History.UpdateSynchronisationRunAsync(_synchronisationRunHistoryDetail);
                }

                callBasedImportConnector.CloseImportConnection();
            }
            else if (_connector is IConnectorImportUsingFiles)
            {
                throw new NotImplementedException("Import connector using files it not yet supported.");
            }
            else
            {
                throw new NotSupportedException("Connector inheritance type is not supported (not calls, not files)");
            }
        }
    }
}
