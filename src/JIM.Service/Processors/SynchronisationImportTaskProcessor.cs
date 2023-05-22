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

                        // is new - new cso required
                        // is existing - apply any changes to the cso from the import object
                        if (connectedSystemObject == null)
                        {
                            await CreateConnectedSystemObjectFromImportObjectAsync(importObject, csObjectType, synchronisationRunHistoryDetailItem);
                        }
                        else
                        {
                            // existing connected system object - update from import object if necessary
                            await UpdateConnectedSystemObjectFromImportObjectAsync(importObject, connectedSystemObject, synchronisationRunHistoryDetailItem);
                        }
                    }

                    // process deletes - what wasn't imported? how do we do this when paging is being used?
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

        private async Task CreateConnectedSystemObjectFromImportObjectAsync(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObjectType connectedSystemObjectType, SynchronisationRunHistoryDetailItem synchronisationRunHistoryDetailItem)
        {
            // this has been tested earlier, no need to error handle
            if (connectedSystemObjectType.UniqueIdentifierAttribute == null)
                return;

            // new object - create connected system object
            var connectedSystemObject = new ConnectedSystemObject
            {
                ConnectedSystem = _connectedSystem,
                UniqueIdentifierAttribute = connectedSystemObjectType.UniqueIdentifierAttribute,
                Type = connectedSystemObjectType
            };

            var csoIsInvalid = false;
            foreach (var importObjectAttribute in connectedSystemImportObject.Attributes)
            {
                // find the connected system schema attribute that has the same name
                var csAttribute = connectedSystemObjectType.Attributes.SingleOrDefault(q => q.Name.Equals(importObjectAttribute.Name, StringComparison.CurrentCultureIgnoreCase));
                if (csAttribute == null)
                {
                    // unexpected import attribute!
                    synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.UnexpectedAttribute;
                    synchronisationRunHistoryDetailItem.ErrorMessage = $"Was not expecting the imported object attribute '{importObjectAttribute.Name}'.";
                    _synchronisationRunHistoryDetail.Items.Add(synchronisationRunHistoryDetailItem);
                    csoIsInvalid = true;
                    break;
                }

                // assign the attribute value(s)
                // remember, jim treats attributes requires an attribute value object for each connected system attribute value, i.e. everything's multi-valued capable
                switch (csAttribute.Type)
                {
                    case AttributeDataType.String:
                        foreach (var importObjectAttributeStringValue in importObjectAttribute.StringValues)
                        {
                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                            {
                                Attribute = csAttribute,
                                StringValue = importObjectAttributeStringValue
                            });
                        }
                        break;
                    case AttributeDataType.Number:
                        foreach (var importObjectAttributeIntValue in importObjectAttribute.IntValues)
                        {
                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                            {
                                Attribute = csAttribute,
                                IntValue = importObjectAttributeIntValue
                            });
                        }
                        break;
                    case AttributeDataType.Binary:
                        foreach (var importObjectAttributeByteValue in importObjectAttribute.ByteValues)
                        {
                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                            {
                                Attribute = csAttribute,
                                ByteValue = importObjectAttributeByteValue
                            });
                        }
                        break;
                    case AttributeDataType.Guid:
                        foreach (var importObjectAttributeGuidValue in importObjectAttribute.GuidValues)
                        {
                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                            {
                                Attribute = csAttribute,
                                GuidValue = importObjectAttributeGuidValue
                            });
                        }
                        break;
                    case AttributeDataType.DateTime:
                        foreach (var importObjectAttributeDateTimeValue in importObjectAttribute.DateTimeValues)
                        {
                            connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                            {
                                Attribute = csAttribute,
                                DateTimeValue = importObjectAttributeDateTimeValue
                            });
                        }
                        break;
                    case AttributeDataType.Bool:
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            BoolValue = importObjectAttribute.BoolValue
                        });
                        break;
                        //case AttributeDataType.Reference:
                        //    break;
                }
            }

            if (csoIsInvalid)
                return;

            // persist the new cso
            await _jim.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject);
        }

        private async Task UpdateConnectedSystemObjectFromImportObjectAsync(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObject connectedSystemObject, SynchronisationRunHistoryDetailItem synchronisationRunHistoryDetailItem)
        {
            // attribute value additions and removals for all attributes will be collected together for persistence in one go
            var attributeValueRemovals  = new List<ConnectedSystemObjectAttributeValue>();
            var attributeValueAdditions = new List<ConnectedSystemObjectAttributeValue>();

            // process known attributes (potential updates)
            // need to work with the fact that we have individual objects for multi-valued attribute values
            // get a list of distinct attributes
            var csoAttributeNames = connectedSystemObject.AttributeValues.Select(q => q.Attribute.Name).Distinct();
            foreach (var csoAttributeName in csoAttributeNames)
            {
                // is there a matching attribute in the import object?
                var importedAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(q => q.Name != null && q.Name.Equals(csoAttributeName, StringComparison.OrdinalIgnoreCase));
                if (importedAttribute != null)
                {
                    // work out what data type this attribute is and get the matching imported object attribute
                    var csoAttribute = connectedSystemObject.Type.Attributes.Single(a => a.Name.Equals(csoAttributeName, StringComparison.CurrentCultureIgnoreCase));
                    var importedObjectAttributeList = connectedSystemImportObject.Attributes.Where(a => a.Name != null && a.Name.Equals(csoAttributeName, StringComparison.CurrentCultureIgnoreCase)).ToList();
                    if (importedObjectAttributeList.Count > 1)
                    {
                        // imported objects attributes should be distinct, i.e. one per name
                        synchronisationRunHistoryDetailItem.Error = SynchronisationRunHistoryDetailItemError.DuplicateImportedAttribute;
                        synchronisationRunHistoryDetailItem.ErrorMessage = $"Attribute '{csoAttributeName}' was present more than one once the import object. Cannot continue processing this object.";
                        return;
                    }
                    var importedObjectAttribute = importedObjectAttributeList[0];

                    // process attribute additions and removals...
                    switch (csoAttribute.Type)
                    {
                        case AttributeDataType.String:

                            // find values on the cso of type string that aren't on the imported object and remove them first
                            var missingStringAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.StringValue != null && !(importedObjectAttribute.StringValues.Any(i => i.Equals(av.StringValue))));
                            attributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingStringAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type string that aren't on the cso and add them
                            var newStringValues = importedObjectAttribute.StringValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.StringValue != null && av.StringValue.Equals(sv)));
                            foreach (var newStringValue in newStringValues)
                                attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, StringValue = newStringValue });

                            break;
                        case AttributeDataType.Number:

                            // find values on the cso of type int that aren't on the imported object and remove them first
                            var missingIntAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.IntValue != null && !(importedObjectAttribute.IntValues.Any(i => i.Equals(av.IntValue))));
                            attributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingIntAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type int that aren't on the cso and add them
                            var newIntValues = importedObjectAttribute.IntValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.IntValue != null && av.IntValue.Equals(sv)));
                            foreach (var newIntValue in newIntValues)
                                attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, IntValue = newIntValue });

                            break;
                        case AttributeDataType.DateTime:

                            // find values on the cso of type DateTime that aren't on the imported object and remove them first
                            var missingDateTimeAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.DateTimeValue != null && !(importedObjectAttribute.DateTimeValues.Any(i => i.Equals(av.DateTimeValue))));
                            attributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingDateTimeAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type DateTime that aren't on the cso and add them
                            var newDateTimeValues = importedObjectAttribute.DateTimeValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.DateTimeValue != null && av.DateTimeValue.Equals(sv)));
                            foreach (var newDateTimeValue in newDateTimeValues)
                                attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, DateTimeValue = newDateTimeValue });

                            break;
                        case AttributeDataType.Binary:

                            // find values on the cso of type byte array that aren't on the imported object and remove them first
                            var missingByteArrayAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.ByteValue != null && !importedObjectAttribute.ByteValues.Any(i => Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue)));
                            attributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingByteArrayAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type byte array that aren't on the cso and add them
                            var newByteArrayValues = importedObjectAttribute.ByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue)));
                            foreach (var newByteArrayValue in newByteArrayValues)
                                attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, ByteValue = newByteArrayValue });

                            break;

                        case AttributeDataType.Reference:
                            // todo: handle references...
                            // what will we get back? full references for objects either in, or potentially out of OU selection scope?
                            // reconcile this against selected OUs. what kind of response and information do we want to pass back to sync admins in this scenario? 
                            var x = 1;
                            break;

                        case AttributeDataType.Guid:

                            // find values on the cso of type Guid that aren't on the imported object and remove them first
                            var missingGuidAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.GuidValue != null && !(importedObjectAttribute.GuidValues.Any(i => i.Equals(av.GuidValue))));
                            attributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingGuidAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type Guid that aren't on the cso and add them
                            var newGuidValues = importedObjectAttribute.GuidValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.GuidValue != null && av.GuidValue.Equals(sv)));
                            foreach (var newGuidValue in newGuidValues)
                                attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, GuidValue = newGuidValue });

                            break;
                        case AttributeDataType.Bool:

                            // there will be only a single value for a bool. is it the same or different?
                            // if different, remove the old value, add the new one
                            // observation: removing and adding sva values is costlier than just updating a row. it also results in increased primary key usage, i.e. constantly generating new values
                            // todo: consider having the ability to update values instead of replacing.

                            var csAttributeValue = connectedSystemObject.AttributeValues.Single(av => av.Attribute.Name == csoAttributeName);
                            if (csAttributeValue.BoolValue != importedObjectAttribute.BoolValue)
                            {
                                attributeValueRemovals.Add(csAttributeValue);
                                attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, BoolValue = importedObjectAttribute.BoolValue });
                            }

                            break;
                    }
                }
                else
                {
                    // no values were imported for this attribute. delete all the cso attribute values for this attribute
                    var attributeValuesToDelete = connectedSystemObject.AttributeValues.Where(q => q.Attribute.Name == csoAttributeName).ToList();
                    await _jim.ConnectedSystems.DeleteConnectedSystemObjectAttributeValuesAsync(connectedSystemObject, attributeValuesToDelete);
                }
            }

            // process new imported attributes (add attribute values where they were null before)
            var newAttributes = connectedSystemImportObject.Attributes.Where(csio => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name.Equals(csio.Name, StringComparison.CurrentCultureIgnoreCase)));
            foreach (var newAttribute in newAttributes)
            {
                // work out what data type this attribute is
                var csoAttribute = connectedSystemObject.Type.Attributes.Single(a => a.Name.Equals(newAttribute.Name, StringComparison.CurrentCultureIgnoreCase));

                switch (csoAttribute.Type)
                {
                    case AttributeDataType.String:
                        foreach (var newStringValue in newAttribute.StringValues)
                            attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, StringValue = newStringValue });
                        break;
                    case AttributeDataType.Number:
                        foreach (var newIntValue in newAttribute.IntValues)
                            attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, IntValue = newIntValue });
                        break;
                    case AttributeDataType.DateTime:
                        foreach (var newDateTimeValue in newAttribute.DateTimeValues)
                            attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, DateTimeValue = newDateTimeValue });
                        break;
                    case AttributeDataType.Binary:
                        foreach (var newByteArrayValue in newAttribute.ByteValues)
                            attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, ByteValue = newByteArrayValue });
                        break;
                    case AttributeDataType.Reference:
                        // todo: handle references...
                        // what will we get back? full references for objects either in, or potentially out of OU selection scope?
                        // reconcile this against selected OUs. what kind of response and information do we want to pass back to sync admins in this scenario? 
                        var x = 1;
                        break;
                    case AttributeDataType.Guid:
                        foreach (var newGuidValue in newAttribute.GuidValues)
                            attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, GuidValue = newGuidValue });
                        break;
                    case AttributeDataType.Bool:
                        attributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystem = _connectedSystem, Attribute = csoAttribute, BoolValue = newAttribute.BoolValue });
                        break;
                }
            }

            // persist addition and removals...
            await _jim.ConnectedSystems.CreateConnectedSystemObjectAttributeValuesAsync(attributeValueAdditions);
            await _jim.ConnectedSystems.DeleteConnectedSystemObjectAttributeValuesAsync(connectedSystemObject, attributeValueRemovals);
        }
    }
}
