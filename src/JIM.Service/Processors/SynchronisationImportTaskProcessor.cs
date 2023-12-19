using Activity = JIM.Models.Activities.Activity;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.Diagnostics;

namespace JIM.Service.Processors
{
    internal class SynchronisationImportTaskProcessor
    {
        private readonly JimApplication _jim;
        private readonly IConnector _connector;
        private readonly ConnectedSystem _connectedSystem;
        private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
        private readonly MetaverseObject _initiatedBy;
        private readonly Activity _activity;
        private readonly CancellationTokenSource _cancellationTokenSource;

        internal SynchronisationImportTaskProcessor(
            JimApplication jimApplication,
            IConnector connector,
            ConnectedSystem connectedSystem,
            ConnectedSystemRunProfile connectedSystemRunProfile,
            MetaverseObject initiatedBy,
            Activity activity,
            CancellationTokenSource cancellationTokenSource)
        {
            _jim = jimApplication;
            _connector = connector;
            _connectedSystem = connectedSystem;
            _connectedSystemRunProfile = connectedSystemRunProfile;
            _initiatedBy = initiatedBy;
            _activity = activity;
            _cancellationTokenSource = cancellationTokenSource;
        }

        internal async Task PerformFullImportAsync()
        {
            Log.Verbose("PerformFullImportAsync: Starting");

            if (_connectedSystem.ObjectTypes == null)
                throw new InvalidDataException("PerformFullImportAsync: _connectedSystem.ObjectTypes was null. Cannot continue.");

            if (_connector is IConnectorImportUsingCalls callBasedImportConnector)
            {
                callBasedImportConnector.OpenImportConnection(_connectedSystem.SettingValues, Log.Logger);

                var initialPage = true;
                var paginationTokens = new List<ConnectedSystemPaginationToken>();
                while (initialPage || paginationTokens.Count > 0)
                {
                    // perform the import for this page
                    var result = await callBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, paginationTokens, null, Log.Logger, _cancellationTokenSource.Token);

                    // make sure we pass the pagination tokens back in on the next page (if there is one)
                    paginationTokens = result.PaginationTokens;

                    if (result.PersistedConnectorData != _connectedSystem.PersistedConnectorData)
                    {
                        // the connector wants to persist some data between sync runs. update the connected system with the new value
                        Log.Debug($"ExecuteAsync: updating persisted connector data. old value: '{_connectedSystem.PersistedConnectorData}', new value: '{result.PersistedConnectorData}'");
                        _connectedSystem.PersistedConnectorData = result.PersistedConnectorData;

                        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem, _initiatedBy, _activity);
                    }

                    // decision: do we want to load the whole connector space into memory to maximise performance? for now, let's keep it db-centric.
                    // todo: experiment with using parallel foreach to see if we can speed up processing
                    foreach (var importObject in result.ImportObjects)
                    {
                        // this will store the detail for the import object that will persist in the history for the run
                        var activityRunProfileExecutionItem = _activity.AddRunProfileExecutionItem();

                        // is this a new, or existing object for the Connected System within JIM?
                        // find the external id attribute(s) for this connected system object type, and then pull out the right type attribute values from the imported object.

                        // match the string object type to a name of an object type in the schema..
                        var csObjectType = _connectedSystem.ObjectTypes.SingleOrDefault(q => q.Name.Equals(importObject.ObjectType, StringComparison.OrdinalIgnoreCase));
                        if (csObjectType == null || !csObjectType.Attributes.Any(a => a.IsExternalId))
                        {
                            activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.CouldntMatchObjectType;
                            activityRunProfileExecutionItem.ErrorMessage = $"PerformFullImportAsync: Couldn't find valid connected system ({_connectedSystem.Id}) object type for imported object type: {importObject.ObjectType}";
                            continue;
                        }

                        // try and find a matching connected system object
                        var connectedSystemObject = await TryAndFindMatchingConnectedSystemObjectAsync(importObject, csObjectType);

                        // is new - new cso required
                        // is existing - apply any changes to the cso from the import object
                        if (connectedSystemObject == null)
                        {
                            activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Create;
                            await CreateConnectedSystemObjectFromImportObjectAsync(importObject, csObjectType, activityRunProfileExecutionItem);
                        }
                        else
                        {
                            // existing connected system object - update from import object if necessary
                            activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Update;
                            activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                            await UpdateConnectedSystemObjectFromImportObjectAsync(importObject, connectedSystemObject, activityRunProfileExecutionItem);
                        }
                    }

                    // todo: process deletes - what wasn't imported? how do we do this when paging is being used?
                    // make sure it doesn't apply deletes if no objects were imported, as this suggests there was a problem collecting data from the connected system?

                    if (initialPage)
                        initialPage = false;

                    // update the activity with the results from this page's processing
                    await _jim.Activities.UpdateActivityAsync(_activity);
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

        private async Task<ConnectedSystemObject?> TryAndFindMatchingConnectedSystemObjectAsync(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObjectType connectedSystemObjectType)
        {
            // todo: add support for multiple external id attributes, i.e. compound primary keys
            var externalIdAttribute = connectedSystemObjectType.Attributes.First(a => a.IsExternalId);

            // find the matching import object attribute
            var importObjectAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(csioa => csioa.Name.Equals(externalIdAttribute.Name, StringComparison.OrdinalIgnoreCase)) ?? 
                throw new MissingExternalIdAttributeException($"The imported object is missing the External Id attribute '{externalIdAttribute.Name}'. It cannot be processed as we will not be able to determine if it's an existing object or not.");

            if (importObjectAttribute.IntValues.Count > 1 ||
                importObjectAttribute.StringValues.Count > 1 ||
                importObjectAttribute.GuidValues.Count > 1)
                throw new ExternalIdAttributeNotSingleValuedException($"External Id attribute ({externalIdAttribute.Name}) on the imported object has multiple values! The External Id attribute must be single-valued.");

            if (externalIdAttribute.Type == AttributeDataType.Text)
            {
                if (importObjectAttribute.StringValues.Count == 0)
                    throw new ExternalIdAttributeValueMissingException($"External Id string attribute ({externalIdAttribute.Name}) on the imported object has no value.");

                return await _jim.ConnectedSystems.GetConnectedSystemObjectByExternalIdAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.StringValues[0]);
            }
            else if (externalIdAttribute.Type == AttributeDataType.Number)
            {
                if (importObjectAttribute.IntValues.Count == 0)
                    throw new ExternalIdAttributeValueMissingException($"External Id number attribute({externalIdAttribute.Name}) on the imported object has no value.");

                return await _jim.ConnectedSystems.GetConnectedSystemObjectByExternalIdAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.IntValues[0]);
            }
            else if (externalIdAttribute.Type == AttributeDataType.Guid)
            {
                if (importObjectAttribute.GuidValues.Count == 0)
                    throw new ExternalIdAttributeValueMissingException($"External Id guid attribute ({externalIdAttribute.Name}) on the imported object has no value.");

                return await _jim.ConnectedSystems.GetConnectedSystemObjectByExternalIdAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.GuidValues[0]);
            }

            // should never happen, but it's worth covering all possible scenarios
            throw new InvalidDataException($"TryAndFindMatchingConnectedSystemObjectAsync: Unsupported connected system object type External Id attribute type: {externalIdAttribute.Type}");
        }

        private async Task CreateConnectedSystemObjectFromImportObjectAsync(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObjectType connectedSystemObjectType, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
        {
            var stopwatch = Stopwatch.StartNew();

            // new object - create connected system object
            var connectedSystemObject = new ConnectedSystemObject
            {
                ConnectedSystem = _connectedSystem,
                ExternalIdAttributeId = connectedSystemObjectType.Attributes.First(a => a.IsExternalId).Id,
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
                    activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnexpectedAttribute;
                    activityRunProfileExecutionItem.ErrorMessage = $"Was not expecting the imported object attribute '{importObjectAttribute.Name}'.";
                    csoIsInvalid = true;
                    break;
                }

                // assign the attribute value(s)
                // remember, jim requires an attribute value object for each connected system attribute value, i.e. everything's multi-valued capable
                switch (csAttribute.Type)
                {
                    case AttributeDataType.Text:
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
                    case AttributeDataType.Boolean:
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
            await _jim.ConnectedSystems.CreateConnectedSystemObjectAsync(connectedSystemObject, activityRunProfileExecutionItem);

            // now associate the persisted cso (now it has a db-generated id) with the activityRunProfileExecutionItem
            activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;

            stopwatch.Stop();
            Log.Debug($"CreateConnectedSystemObjectFromImportObjectAsync: completed for '{connectedSystemObject.Id}' in {stopwatch.Elapsed}");
        }

        private async Task UpdateConnectedSystemObjectFromImportObjectAsync(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
        {
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
                        activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.DuplicateImportedAttribute;
                        activityRunProfileExecutionItem.ErrorMessage = $"Attribute '{csoAttributeName}' was present more than one once the import object. Cannot continue processing this object.";
                        return;
                    }
                    var importedObjectAttribute = importedObjectAttributeList[0];

                    // process attribute additions and removals...
                    switch (csoAttribute.Type)
                    {
                        case AttributeDataType.Text:

                            // find values on the cso of type string that aren't on the imported object and remove them first
                            var missingStringAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.StringValue != null && !importedObjectAttribute.StringValues.Any(i => i.Equals(av.StringValue)));
                            connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingStringAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type string that aren't on the cso and add them
                            var newStringValues = importedObjectAttribute.StringValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.StringValue != null && av.StringValue.Equals(sv)));
                            foreach (var newStringValue in newStringValues)
                                connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, StringValue = newStringValue });

                            break;
                        case AttributeDataType.Number:

                            // find values on the cso of type int that aren't on the imported object and remove them first
                            var missingIntAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.IntValue != null && !importedObjectAttribute.IntValues.Any(i => i.Equals(av.IntValue)));
                            connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingIntAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type int that aren't on the cso and add them
                            var newIntValues = importedObjectAttribute.IntValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.IntValue != null && av.IntValue.Equals(sv)));
                            foreach (var newIntValue in newIntValues)
                                connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, IntValue = newIntValue });

                            break;
                        case AttributeDataType.DateTime:

                            // find values on the cso of type DateTime that aren't on the imported object and remove them first
                            var missingDateTimeAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.DateTimeValue != null && !importedObjectAttribute.DateTimeValues.Any(i => i.Equals(av.DateTimeValue)));
                            connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingDateTimeAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type DateTime that aren't on the cso and add them
                            var newDateTimeValues = importedObjectAttribute.DateTimeValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.DateTimeValue != null && av.DateTimeValue.Equals(sv)));
                            foreach (var newDateTimeValue in newDateTimeValues)
                                connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, DateTimeValue = newDateTimeValue });

                            break;
                        case AttributeDataType.Binary:

                            // find values on the cso of type byte array that aren't on the imported object and remove them first
                            var missingByteArrayAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.ByteValue != null && !importedObjectAttribute.ByteValues.Any(i => Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue)));
                            connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingByteArrayAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type byte array that aren't on the cso and add them
                            var newByteArrayValues = importedObjectAttribute.ByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue)));
                            foreach (var newByteArrayValue in newByteArrayValues)
                                connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, ByteValue = newByteArrayValue });

                            break;

                        case AttributeDataType.Reference:
                            // todo: handle references...
                            // what will we get back? full references for objects either in, or potentially out of OU selection scope?
                            // reconcile this against selected OUs. what kind of response do we want to pass back to sync admins in this scenario? 
                            var x = 1;
                            break;

                        case AttributeDataType.Guid:

                            // find values on the cso of type Guid that aren't on the imported object and remove them first
                            var missingGuidAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.GuidValue != null && !importedObjectAttribute.GuidValues.Any(i => i.Equals(av.GuidValue)));
                            connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingGuidAttributeValues.Any(msav => msav.Id == av.Id)));

                            // find imported values of type Guid that aren't on the cso and add them
                            var newGuidValues = importedObjectAttribute.GuidValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.GuidValue != null && av.GuidValue.Equals(sv)));
                            foreach (var newGuidValue in newGuidValues)
                                connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, GuidValue = newGuidValue });

                            break;
                        case AttributeDataType.Boolean:

                            // there will be only a single value for a bool. is it the same or different?
                            // if different, remove the old value, add the new one
                            // observation: removing and adding sva values is costlier than just updating a row. it also results in increased primary key usage, i.e. constantly generating new values
                            // todo: consider having the ability to update values instead of replacing.

                            var csAttributeValue = connectedSystemObject.AttributeValues.Single(av => av.Attribute.Name == csoAttributeName);
                            if (csAttributeValue.BoolValue != importedObjectAttribute.BoolValue)
                            {
                                connectedSystemObject.PendingAttributeValueRemovals.Add(csAttributeValue);
                                connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, BoolValue = importedObjectAttribute.BoolValue });
                            }

                            break;
                    }
                }
                else
                {
                    // no values were imported for this attribute. delete all the cso attribute values for this attribute
                    var attributeValuesToDelete = connectedSystemObject.AttributeValues.Where(q => q.Attribute.Name == csoAttributeName);
                    connectedSystemObject.PendingAttributeValueRemovals.AddRange(attributeValuesToDelete);
                }
            }

            // process new imported attributes (addding attribute values where they were null before)
            var newAttributes = connectedSystemImportObject.Attributes.Where(csio => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name.Equals(csio.Name, StringComparison.CurrentCultureIgnoreCase)));
            foreach (var newAttribute in newAttributes)
            {
                // work out what data type this attribute is
                var csoAttribute = connectedSystemObject.Type.Attributes.Single(a => a.Name.Equals(newAttribute.Name, StringComparison.CurrentCultureIgnoreCase));

                switch (csoAttribute.Type)
                {
                    case AttributeDataType.Text:
                        foreach (var newStringValue in newAttribute.StringValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, StringValue = newStringValue });
                        break;
                    case AttributeDataType.Number:
                        foreach (var newIntValue in newAttribute.IntValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, IntValue = newIntValue });
                        break;
                    case AttributeDataType.DateTime:
                        foreach (var newDateTimeValue in newAttribute.DateTimeValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, DateTimeValue = newDateTimeValue });
                        break;
                    case AttributeDataType.Binary:
                        foreach (var newByteArrayValue in newAttribute.ByteValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, ByteValue = newByteArrayValue });
                        break;
                    case AttributeDataType.Reference:
                        // todo: handle references...
                        // what will we get back? full references for objects either in, or potentially out of OU selection scope?
                        // reconcile this against selected OUs. what kind of response and information do we want to pass back to sync admins in this scenario? 
                        var x = 1;
                        break;
                    case AttributeDataType.Guid:
                        foreach (var newGuidValue in newAttribute.GuidValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, GuidValue = newGuidValue });
                        break;
                    case AttributeDataType.Boolean:
                        connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, BoolValue = newAttribute.BoolValue });
                        break;
                }
            }

            // persist the attribute value changes
            await _jim.ConnectedSystems.UpdateConnectedSystemObjectAttributeValuesAsync(connectedSystemObject, activityRunProfileExecutionItem);
        }
    }
}
