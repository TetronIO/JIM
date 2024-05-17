using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.Diagnostics;
using JIM.Worker.Models;

namespace JIM.Worker.Processors;

public class SyncImportTaskProcessor
{
    private readonly JimApplication _jim;
    private readonly IConnector _connector;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly MetaverseObject _initiatedBy;
    private readonly JIM.Models.Activities.Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;

    public SyncImportTaskProcessor(
        JimApplication jimApplication,
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        MetaverseObject initiatedBy,
        JIM.Models.Activities.Activity activity,
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

    public async Task PerformFullImportAsync()
    {
        Log.Verbose("PerformFullImportAsync: Starting");

        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("PerformFullImportAsync: _connectedSystem.ObjectTypes was null. Cannot continue.");

        // we keep track of all processed CSOs here, so we can bulk-persist later, when all waves of CSO changes are prepared
        var connectedSystemObjectsToBeCreated = new List<ConnectedSystemObject>();
        var connectedSystemObjectsToBeUpdated = new List<ConnectedSystemObject>();
        
        // we keep track of the external ids for all imported objects (over all pages, if applicable) so we can look for deletions.
        var externalIdsImported = new List<ExternalIdPair>();
        var totalObjectsImported = 0;
            
        switch (_connector)
        {
            case IConnectorImportUsingCalls callBasedImportConnector:
            {
                callBasedImportConnector.OpenImportConnection(_connectedSystem.SettingValues, Log.Logger);

                var initialPage = true;
                var paginationTokens = new List<ConnectedSystemPaginationToken>();
                while (initialPage || paginationTokens.Count > 0)
                {
                    // perform the import for this page
                    var result = await callBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, paginationTokens, null, Log.Logger, _cancellationTokenSource.Token);
                    totalObjectsImported += result.ImportObjects.Count;
                    
                    // add the external ids from this page worth of results to our external-id collection for later deletion calculation
                    AddExternalIdsToCollection(result, externalIdsImported);
                    
                    // make sure we pass the pagination tokens back in on the next page (if there is one)
                    paginationTokens = result.PaginationTokens;

                    if (result.PersistedConnectorData != _connectedSystem.PersistedConnectorData)
                    {
                        // the connector wants to persist some data between sync runs. update the connected system with the new value
                        Log.Debug($"ExecuteAsync: updating persisted connector data. old value: '{_connectedSystem.PersistedConnectorData}', new value: '{result.PersistedConnectorData}'");
                        _connectedSystem.PersistedConnectorData = result.PersistedConnectorData;
                        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem, _initiatedBy, _activity);
                    }

                    // process the results from this page
                    await ProcessImportObjectsAsync(result, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);

                    if (initialPage)
                        initialPage = false;
                }

                callBasedImportConnector.CloseImportConnection();
                break;
            }
            case IConnectorImportUsingFiles fileBasedImportConnector:
            {
                // file based connectors return all the results from the connected system in one go. no paging.
                var result = await fileBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, Log.Logger, _cancellationTokenSource.Token);
                totalObjectsImported = result.ImportObjects.Count;
                
                // todo: simplify externalIdsImported. objects are too complex
                // add the external ids from the results to our external id collection for later deletion calculation
                AddExternalIdsToCollection(result, externalIdsImported);
                
                await ProcessImportObjectsAsync(result, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
                break;
            }
            default:
                throw new NotSupportedException("Connector inheritance type is not supported (not calls, not files)");
        }
        
        // process deletions
        // note: make sure it doesn't apply deletes if no objects were imported, as this suggests there was a problem collecting data from the connected system?
        // note: if it's expected that 0 imported objects means all objects were deleted, then an admin will have to clear the Connected System manually to achieve the same result.
        if (totalObjectsImported > 0)
            await ProcessConnectedSystemObjectDeletionsAsync(externalIdsImported, connectedSystemObjectsToBeUpdated);

        // now that all objects have been imported, we can attempt to resolve unresolved reference attribute values
        // i.e. attempt to convert unresolved reference strings into hard links to other Connected System Objects
        await ResolveReferencesAsync(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);

        // now persist all CSOs which will also create the required change objects within the activity tree.
        await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjectsToBeCreated, _activity);
        await _jim.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjectsToBeUpdated, _activity);

        // update the activity with the results from all pages.
        // this will also persist the ActivityRunProfileExecutionItem and ConnectedSystemObjectChanges for each CSO.
        await _jim.Activities.UpdateActivityAsync(_activity);
    }

    private async Task ProcessConnectedSystemObjectDeletionsAsync(IReadOnlyCollection<ExternalIdPair> externalIdsImported, ICollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        if (_connectedSystem.ObjectTypes == null)
            return;
        
        // have any objects been deleted in the connected system since our last import?
        // get the connected system object type list for the ones the user has selected to manage
        foreach (var selectedObjectType in _connectedSystem.ObjectTypes.Where(ot => ot.Selected))
        {
            // what's the external id attribute for this object type?
            var objectTypeExternalIdAttribute = selectedObjectType.Attributes.Single(q => q.IsExternalId);
            switch (objectTypeExternalIdAttribute.Type)
            {
                case AttributeDataType.Number:
                {
                    // get the int connected system object external ids for this object type
                    var connectedSystemObjectExternalIdsOfTypeInt = await _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeIntAsync(_connectedSystem.Id, selectedObjectType.Id);

                    // get the int import object external ids for this object type
                    var connectedSystemIntExternalIdValues = externalIdsImported
                        .Where(q => q.ConnectedSystemObjectType.Id == selectedObjectType.Id)
                        .SelectMany(externalId => externalId.ConnectedSystemImportObjectAttribute.IntValues);

                    // create a collection with the connected system objects no longer in the connected system for this object type
                    var connectedSystemObjectDeletesExternalIds = connectedSystemObjectExternalIdsOfTypeInt.Except(connectedSystemIntExternalIdValues);

                    // obsolete the connected system objects no longer in the connected system for this object type
                    foreach (var externalId in connectedSystemObjectDeletesExternalIds)
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated);
                    break;
                }
                case AttributeDataType.Text:
                {
                    // get the string connected system object external ids for this object type
                    var connectedSystemObjectExternalIdsOfTypeString = await _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeStringAsync(_connectedSystem.Id, selectedObjectType.Id);

                    // get the string import object external ids for this object type
                    var connectedSystemStringExternalIdValues = externalIdsImported
                        .Where(q => q.ConnectedSystemObjectType.Id == selectedObjectType.Id)
                        .SelectMany(externalId => externalId.ConnectedSystemImportObjectAttribute.StringValues);

                    // create a collection with the connected system objects no longer in the connected system for this object type
                    var connectedSystemObjectDeletesExternalIds = connectedSystemObjectExternalIdsOfTypeString.Except(connectedSystemStringExternalIdValues);
                    
                    // obsolete the connected system objects no longer in the connected system for this object type
                    foreach (var externalId in connectedSystemObjectDeletesExternalIds)
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated);
                    break;
                }
                case AttributeDataType.Guid:
                {
                    // get the guid connected system object external ids for this object type
                    var connectedSystemObjectExternalIdsOfTypeGuid = await _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeGuidAsync(_connectedSystem.Id, selectedObjectType.Id);

                    // get the guid import object external ids for this object type
                    var connectedSystemGuidExternalIdValues = externalIdsImported
                        .Where(q => q.ConnectedSystemObjectType.Id == selectedObjectType.Id)
                        .SelectMany(externalId => externalId.ConnectedSystemImportObjectAttribute.GuidValues);

                    // create a collection with the connected system objects no longer in the connected system for this object type
                    var connectedSystemObjectDeletesExternalIds = connectedSystemObjectExternalIdsOfTypeGuid.Except(connectedSystemGuidExternalIdValues);
                    
                    // obsolete the connected system objects no longer in the connected system for this object type
                    foreach (var externalId in connectedSystemObjectDeletesExternalIds)
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated);
                    break;
                }
                case AttributeDataType.NotSet:
                case AttributeDataType.DateTime:
                case AttributeDataType.Binary:
                case AttributeDataType.Reference:
                case AttributeDataType.Boolean:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private async Task ObsoleteConnectedSystemObjectAsync<T>(T connectedSystemObjectExternalId, int connectedSystemAttributeId, ICollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        // find the cso
        var cso = connectedSystemObjectExternalId switch
        {
            int intId => await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, connectedSystemAttributeId, intId),
            string stringId => await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, connectedSystemAttributeId, stringId),
            Guid guidId => await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, connectedSystemAttributeId, guidId),
            _ => null
        };

        if (cso == null)
        {
            Log.Information($"ObsoleteConnectedSystemObjectAsync: CSO with external id '{connectedSystemObjectExternalId}' not found. No work to do.");
            return;
        }
        
        // we need to create a run profile execution item for the object deletion. it will get persisted in the activity tree.
        var activityRunProfileExecutionItem = _activity.AddRunProfileExecutionItem();
        activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Obsolete;
        activityRunProfileExecutionItem.ConnectedSystemObject = cso;
        
        // mark it obsolete, so that it's deleted when a synchronisation run profile is performed.
        cso.Status = ConnectedSystemObjectStatus.Obsolete;

        // add it to the list of objects to be updated. this will persist and create a change object in the activity tree.
        connectedSystemObjectsToBeUpdated.Add(cso);
    }
    
    private void AddExternalIdsToCollection(ConnectedSystemImportResult result, ICollection<ExternalIdPair> externalIdsImported)
    {
        if (_connectedSystem.ObjectTypes == null)
            return;
        
        // add the external ids from the results to our external id collection
        foreach (var importedObject in result.ImportObjects)
        {
            // find the object type for the imported object in our schema
            var connectedSystemObjectType = _connectedSystem.ObjectTypes.Single(q => q.Name.Equals(importedObject.ObjectType, StringComparison.InvariantCultureIgnoreCase));
                        
            // what is the external id attribute for this object type in our schema?
            var externalIdAttributeName = connectedSystemObjectType.Attributes.Single(q => q.IsExternalId).Name;
            externalIdsImported.Add(new ExternalIdPair
            {
                ConnectedSystemObjectType = connectedSystemObjectType,
                ConnectedSystemImportObjectAttribute = importedObject.Attributes.Single(q => q.Name.Equals(externalIdAttributeName, StringComparison.InvariantCultureIgnoreCase))
            });
        }
    }

    private async Task ProcessImportObjectsAsync(ConnectedSystemImportResult connectedSystemImportResult, ICollection<ConnectedSystemObject> connectedSystemObjectsToBeCreated, ICollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("ProcessImportObjectsAsync: _connectedSystem.ObjectTypes was null. Cannot continue.");

        // decision: do we want to load the whole connector space into memory to maximise performance? for now, let's keep it db-centric.
        // todo: experiment with using parallel foreach to see if we can speed up processing
        foreach (var importObject in connectedSystemImportResult.ImportObjects)
        {
            // this will store the detail for the import object that will persist in the history for the run
            var activityRunProfileExecutionItem = _activity.AddRunProfileExecutionItem();
            
            try
            {
                // validate the results.
                // are any of the attribute values duplicated? stop processing if so
                var duplicateAttributeNames = importObject.Attributes.GroupBy(a => a.Name, StringComparer.InvariantCultureIgnoreCase).Where(g => g.Count() > 1).Select(n => n.Key).ToList();
                if (duplicateAttributeNames is { Count: > 0 })
                {
                    activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.DuplicateImportedAttributes;
                    activityRunProfileExecutionItem.ErrorMessage = $"The imported object has one or more duplicate attributes: {string.Join(", ", duplicateAttributeNames)}. Please de-duplicate and try again.";

                    // todo: include a serialised snapshot of the imported object that is also presented to sync admin when viewing sync errors
                    continue;
                }

                // is this a new, or existing object for the Connected System within JIM?
                // find the external id attribute(s) for this connected system object type, and then pull out the right type attribute values from the imported object.

                // match the string object type to a name of an object type in the schema…
                var csObjectType = _connectedSystem.ObjectTypes.SingleOrDefault(q => q.Name.Equals(importObject.ObjectType, StringComparison.OrdinalIgnoreCase));
                if (csObjectType == null || !csObjectType.Attributes.Any(a => a.IsExternalId))
                {
                    activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.CouldNotMatchObjectType;
                    activityRunProfileExecutionItem.ErrorMessage = $"PerformFullImportAsync: Couldn't find valid connected system ({_connectedSystem.Id}) object type for imported object type: {importObject.ObjectType}";
                    continue;
                }
                
                // precautionary pre-processing...
                RemoveNullImportObjectAttributes(importObject);

                // see if we already have a matching connected system object for this imported object within JIM
                var connectedSystemObject = await TryAndFindMatchingConnectedSystemObjectAsync(importObject, csObjectType);
                
                // is new - new cso required
                // is existing - apply any changes to the cso from the import object
                if (connectedSystemObject == null)
                {
                    activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Create;
                    connectedSystemObject = CreateConnectedSystemObjectFromImportObject(importObject, csObjectType, activityRunProfileExecutionItem);
                    
                    // cso could be null at this point if the create-cso flow failed due to unexpected import attributes, etc.
                    if (connectedSystemObject != null)
                        connectedSystemObjectsToBeCreated.Add(connectedSystemObject);
                }
                else
                {
                    // existing connected system object - update from import object if necessary
                    activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Update;
                    activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                    UpdateConnectedSystemObjectFromImportObject(importObject, connectedSystemObject, csObjectType, activityRunProfileExecutionItem);
                    connectedSystemObjectsToBeUpdated.Add(connectedSystemObject);
                }
            }
            catch (Exception e)
            {
                // log the unhandled exception to the run profile execution item, so admins can see the error via a client.
                activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
                activityRunProfileExecutionItem.ErrorMessage = e.Message;
                activityRunProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            
                // still perform system logging.
                Log.Error(e, $"ProcessImportObjectsAsync: Unhandled {_connectedSystemRunProfile} sync error whilst processing import object {importObject}.");
            }
        }
    }

    /// <summary>
    /// It's possible the Connector has supplied some attributes with null values. These shouldn't be passed to JIM,
    /// so as a precaution let's ensure we have only populated attributes.
    /// </summary>
    private static void RemoveNullImportObjectAttributes(ConnectedSystemImportObject connectedSystemImportObject)
    {
        var nullConnectedSystemImportObjectAttributes = new List<ConnectedSystemImportObjectAttribute>();
        foreach (var attribute in connectedSystemImportObject.Attributes)
        {
            var noGuids = false;
            var noIntegers = false;
            var noStrings = false;
            var noBytes = false;
            var noReferences = false;
            
            // first remove any null attribute values. this might mean we'll be left with no values at all
            attribute.GuidValues.RemoveAll(q => q.Equals(null));
            attribute.IntValues.RemoveAll(q => q.Equals(null));
            attribute.StringValues.RemoveAll(string.IsNullOrEmpty);
            attribute.ByteValues.RemoveAll(q => q.Equals(null));
            attribute.ReferenceValues.RemoveAll(string.IsNullOrEmpty);
            
            // now work out if we're left with any values at all
            if (attribute.GuidValues.Count == 0)
                noGuids = true;
            if (attribute.IntValues.Count == 0)
                noIntegers = true;
            if (attribute.StringValues.Count == 0)
                noStrings = true;
            var noBool = !attribute.BoolValue.HasValue;
            var noDateTime = !attribute.DateTimeValue.HasValue;
            if (attribute.ByteValues.Count == 0)
                noBytes = true;
            if (attribute.ReferenceValues.Count == 0)
                noReferences = true;

            // if all types of values are empty, we'll add this attribute to a list for removal
            if (noGuids && noIntegers && noStrings && noBool && noDateTime && noBytes && noReferences)
                nullConnectedSystemImportObjectAttributes.Add(attribute);
        }

        foreach (var nullAttribute in nullConnectedSystemImportObjectAttributes)
            connectedSystemImportObject.Attributes.Remove(nullAttribute);
    }

    private async Task<ConnectedSystemObject?> TryAndFindMatchingConnectedSystemObjectAsync(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObjectType connectedSystemObjectType)
    {
        // todo: consider support for multiple external id attributes, i.e. compound primary keys
        var externalIdAttribute = connectedSystemObjectType.Attributes.First(a => a.IsExternalId);

        // find the matching import object attribute
        var importObjectAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(csioa => csioa.Name.Equals(externalIdAttribute.Name, StringComparison.OrdinalIgnoreCase)) ?? 
                                    throw new MissingExternalIdAttributeException($"The imported object is missing the External Id attribute '{externalIdAttribute.Name}'. It cannot be processed as we will not be able to determine if it's an existing object or not.");

        if (importObjectAttribute.IntValues.Count > 1 ||
            importObjectAttribute.StringValues.Count > 1 ||
            importObjectAttribute.GuidValues.Count > 1)
            throw new ExternalIdAttributeNotSingleValuedException($"External Id attribute ({externalIdAttribute.Name}) on the imported object has multiple values! The External Id attribute must be single-valued.");

        switch (externalIdAttribute.Type)
        {
            case AttributeDataType.Text when importObjectAttribute.StringValues.Count == 0:
                throw new ExternalIdAttributeValueMissingException($"External Id string attribute ({externalIdAttribute.Name}) on the imported object has no value.");
            case AttributeDataType.Text:
                return await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.StringValues[0]);
            case AttributeDataType.Number when importObjectAttribute.IntValues.Count == 0:
                throw new ExternalIdAttributeValueMissingException($"External Id number attribute({externalIdAttribute.Name}) on the imported object has no value.");
            case AttributeDataType.Number:
                return await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.IntValues[0]);
            case AttributeDataType.Guid when importObjectAttribute.GuidValues.Count == 0:
                throw new ExternalIdAttributeValueMissingException($"External Id guid attribute ({externalIdAttribute.Name}) on the imported object has no value.");
            case AttributeDataType.Guid:
                return await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.GuidValues[0]);
            case AttributeDataType.NotSet:
            case AttributeDataType.DateTime:
            case AttributeDataType.Binary:
            case AttributeDataType.Reference:
            case AttributeDataType.Boolean:
            default:
                throw new InvalidDataException($"TryAndFindMatchingConnectedSystemObjectAsync: Unsupported connected system object type External Id attribute type: {externalIdAttribute.Type}");
        }
    }

    private ConnectedSystemObject? CreateConnectedSystemObjectFromImportObject(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObjectType connectedSystemObjectType, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        var stopwatch = Stopwatch.StartNew();

        // new object - create connected system object using data from an import object
        var connectedSystemObject = new ConnectedSystemObject
        {
            ConnectedSystem = _connectedSystem,
            ExternalIdAttributeId = connectedSystemObjectType.Attributes.First(a => a.IsExternalId).Id,
            Type = connectedSystemObjectType
        };

        // not every system uses a secondary external id attribute, but some do, i.e. LDAP
        var secondaryExternalIdAttribute = connectedSystemObjectType.Attributes.FirstOrDefault(a => a.IsSecondaryExternalId);
        if (secondaryExternalIdAttribute != null)
            connectedSystemObject.SecondaryExternalIdAttributeId = secondaryExternalIdAttribute.Id;

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
                            StringValue = importObjectAttributeStringValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.Number:
                    foreach (var importObjectAttributeIntValue in importObjectAttribute.IntValues)
                    {
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            IntValue = importObjectAttributeIntValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.Binary:
                    foreach (var importObjectAttributeByteValue in importObjectAttribute.ByteValues)
                    {
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            ByteValue = importObjectAttributeByteValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.Guid:
                    foreach (var importObjectAttributeGuidValue in importObjectAttribute.GuidValues)
                    {
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            GuidValue = importObjectAttributeGuidValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.DateTime:
                    connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                    {
                        Attribute = csAttribute,
                        DateTimeValue = importObjectAttribute.DateTimeValue,
                        ConnectedSystemObject = connectedSystemObject
                    });
                    break;
                case AttributeDataType.Boolean:
                    connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                    {
                        Attribute = csAttribute,
                        BoolValue = importObjectAttribute.BoolValue,
                        ConnectedSystemObject = connectedSystemObject
                    });
                    break;
                case AttributeDataType.Reference:
                    foreach (var importObjectAttributeReferenceValue in importObjectAttribute.ReferenceValues)
                    {
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            UnresolvedReferenceValue = importObjectAttributeReferenceValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.NotSet:
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (csoIsInvalid)
            return null;

        // now associate the persisted cso with the activityRunProfileExecutionItem
        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;

        stopwatch.Stop();
        Log.Debug($"CreateConnectedSystemObjectFromImportObject: completed for {connectedSystemObject.Type.Name} ExtId: '{connectedSystemObject.ExternalIdAttributeValue}', SecExtId: '{connectedSystemObject.SecondaryExternalIdAttributeValue}' in {stopwatch.Elapsed}");

        return connectedSystemObject;
    }

    private static void UpdateConnectedSystemObjectFromImportObject(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObject connectedSystemObject, ConnectedSystemObjectType connectedSystemObjectType, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        // process known attributes (potential updates)
        // need to work with the fact that we have individual objects for multivalued attribute values
        foreach (var csoAttributeName in connectedSystemObjectType.Attributes.Select(a => a.Name))
        {
            // is there a matching attribute in the import object?
            var importedAttribute = connectedSystemImportObject.Attributes.SingleOrDefault(q => q.Name.Equals(csoAttributeName, StringComparison.OrdinalIgnoreCase));
            if (importedAttribute != null)
            {
                // work out what data type this attribute is and get the matching imported object attribute
                var csoAttribute = connectedSystemObject.Type.Attributes.Single(a => a.Name.Equals(csoAttributeName, StringComparison.CurrentCultureIgnoreCase));
                var importedObjectAttributeList = connectedSystemImportObject.Attributes.Where(a => a.Name.Equals(csoAttributeName, StringComparison.CurrentCultureIgnoreCase)).ToList();
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
                        var existingCsoDateTimeAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == csoAttributeName);
                        if (existingCsoDateTimeAttributeValue == null)
                        {
                            // set initial value
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                        }
                        else if (existingCsoDateTimeAttributeValue.DateTimeValue != importedObjectAttribute.DateTimeValue)
                        {
                            // update existing value by removing and adding
                            connectedSystemObject.PendingAttributeValueRemovals.Add(existingCsoDateTimeAttributeValue);
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, DateTimeValue = importedObjectAttribute.DateTimeValue });
                        }
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
                        // find unresolved reference values on the cso that aren't on the imported object and remove them first
                        var missingUnresolvedReferenceValues = connectedSystemObject.AttributeValues.Where(av => av.Attribute.Name == csoAttributeName && av.UnresolvedReferenceValue != null && !importedObjectAttribute.ReferenceValues.Any(i => i.Equals(av.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase)));
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(connectedSystemObject.AttributeValues.Where(av => missingUnresolvedReferenceValues.Any(msav => msav.Id == av.Id)));

                        // find imported unresolved reference values that aren't on the cso and add them
                        var newUnresolvedReferenceValues = importedObjectAttribute.ReferenceValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => av.Attribute.Name == csoAttributeName && av.UnresolvedReferenceValue != null && av.UnresolvedReferenceValue.Equals(sv, StringComparison.InvariantCultureIgnoreCase)));
                        foreach (var newUnresolvedReferenceValue in newUnresolvedReferenceValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, UnresolvedReferenceValue = newUnresolvedReferenceValue });
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
                        // observation: removing and adding SVA values is costlier than just updating a row. it also results in increased primary key usage, i.e. constantly generating new values
                        // todo: consider having the ability to update values instead of replacing.
                        var csoBooleanAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => av.Attribute.Name == csoAttributeName);
                        if (csoBooleanAttributeValue == null)
                        {
                            // set initial value
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, BoolValue = importedObjectAttribute.BoolValue });
                        }
                        else if (csoBooleanAttributeValue.BoolValue != importedObjectAttribute.BoolValue)
                        {
                            // update existing value by removing and adding
                            connectedSystemObject.PendingAttributeValueRemovals.Add(csoBooleanAttributeValue);
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, BoolValue = importedObjectAttribute.BoolValue });
                        }
                        break;
                    
                    case AttributeDataType.NotSet:
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else
            {
                // no values were imported for this attribute. delete all the cso attribute values for this attribute
                var attributeValuesToDelete = connectedSystemObject.AttributeValues.Where(q => q.Attribute.Name == csoAttributeName);
                connectedSystemObject.PendingAttributeValueRemovals.AddRange(attributeValuesToDelete);
            }
        }
    }

    /// <summary>
    /// Enumerate each connected system object with an unresolved reference string value and attempts to convert it to a resolved reference to another connected system object.
    /// </summary>
    private async Task ResolveReferencesAsync(IReadOnlyCollection<ConnectedSystemObject> connectedSystemObjectsToBeCreated, IReadOnlyCollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        // get all csos with attributes that have unresolved reference values
        // see if we can find a cso that has an external id or secondary external id attribute value matching the string value
        // add the cso id as the reference value
        // remove the unresolved reference value
        // update the cso
        // create a connected system object change for this
        
        // enumerate just the CSOs with unresolved references, for efficiency
        foreach (var csoToProcess in connectedSystemObjectsToBeCreated.Where(cso => cso.AttributeValues.Any(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue))))
        {
            var externalIdAttribute = csoToProcess.Type.Attributes.Single(a => a.IsExternalId);
            var secondaryExternalIdAttribute = csoToProcess.Type.Attributes.SingleOrDefault(a => a.IsSecondaryExternalId);
            var externalIdAttributeToUse = secondaryExternalIdAttribute ?? externalIdAttribute;
            
            // enumerate just the attribute values for this CSO that are for unresolved references
            foreach (var referenceAttributeValue in csoToProcess.AttributeValues.Where(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue)))
                await ResolveAttributeValueReferenceAsync(csoToProcess, referenceAttributeValue, externalIdAttributeToUse, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
        }
        
        foreach (var csoToProcess in connectedSystemObjectsToBeUpdated.Where(cso => cso.PendingAttributeValueAdditions.Any(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue))))
        {
            var externalIdAttribute = csoToProcess.Type.Attributes.Single(a => a.IsExternalId);
            var secondaryExternalIdAttribute = csoToProcess.Type.Attributes.SingleOrDefault(a => a.IsSecondaryExternalId);
            var externalIdAttributeToUse = secondaryExternalIdAttribute ?? externalIdAttribute;
            
            // enumerate just the attribute values for this CSO that are for unresolved references
            foreach (var referenceAttributeValue in csoToProcess.PendingAttributeValueAdditions.Where(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue)))
                await ResolveAttributeValueReferenceAsync(csoToProcess, referenceAttributeValue, externalIdAttributeToUse, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
        }
    }
    
    private async Task ResolveAttributeValueReferenceAsync(ConnectedSystemObject csoToProcess, ConnectedSystemObjectAttributeValue referenceAttributeValue, ConnectedSystemObjectTypeAttribute externalIdAttribute, IReadOnlyCollection<ConnectedSystemObject> connectedSystemObjectsToBeCreated, IReadOnlyCollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        // try and find a cso in the database, or in the processing list we've been passed in, that has an identifier mentioned in the UnresolvedReferenceValue property.
        // to do this:
        // - work out what type of target attribute the unresolved reference is pointing to
        //   most connected systems use the external id attribute when referencing other objects
        //   but connected systems that use a secondary id use the secondary external id for references (i.e. LDAP and their DNs).
        // - search the processing list for a cso match
        // - failing that, search the database for a cso match
        // - assign the cso in the reference property, and remove the unresolved reference string property

        // vs linting issue. it doesn't know how to interpret the loop query and thinks UnresolvedReferenceValue may be null.
        if (string.IsNullOrEmpty(referenceAttributeValue.UnresolvedReferenceValue))
            return;

        // try and find the referenced object by the external id amongst the two processing lists of CSOs first
        ConnectedSystemObject? referencedConnectedSystemObject;

        // couldn't get this to match anything. no idea why
        //referencedConnectedSystemObject = connectedSystemObjectsToProcess.SingleOrDefault(cso =>
        //    cso.AttributeValues.Any(av =>
        //        av.Attribute.Id == externalIdAttributeToUse.Id &&
        //        av.StringValue != null &&
        //        av.StringValue.Equals(referenceAttributeValue.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase)));

        // this does work, but might not be optimal:
        // ideally fix the above query, so it works and don't use this, but for now, works is works.

        if (externalIdAttribute.IsExternalId)
        {
            switch (externalIdAttribute.Type)
            {
                case AttributeDataType.Text:
                    referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.ExternalIdAttributeValue?.StringValue != null && cso.ExternalIdAttributeValue.StringValue.Equals(referenceAttributeValue.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase)) ??
                                                      connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.ExternalIdAttributeValue?.StringValue != null && cso.ExternalIdAttributeValue.StringValue.Equals(referenceAttributeValue.UnresolvedReferenceValue, StringComparison.InvariantCultureIgnoreCase));
                    break;
                case AttributeDataType.Number:
                    if (int.TryParse(referenceAttributeValue.UnresolvedReferenceValue, out var intUnresolvedReferenceValue))
                        referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.ExternalIdAttributeValue != null && cso.ExternalIdAttributeValue.IntValue == intUnresolvedReferenceValue) ??
                                                          connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.ExternalIdAttributeValue != null && cso.ExternalIdAttributeValue.IntValue == intUnresolvedReferenceValue);
                    else
                        throw new InvalidCastException(
                            $"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} with value '{referenceAttributeValue.UnresolvedReferenceValue}' cannot be parsed to an int.");
                    break;
                case AttributeDataType.Guid:
                    if (Guid.TryParse(referenceAttributeValue.UnresolvedReferenceValue, out var guidUnresolvedReferenceValue))
                        referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.ExternalIdAttributeValue != null && cso.ExternalIdAttributeValue.GuidValue == guidUnresolvedReferenceValue) ??
                                                          connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.ExternalIdAttributeValue != null && cso.ExternalIdAttributeValue.GuidValue == guidUnresolvedReferenceValue);
                    else
                        throw new InvalidCastException($"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} with value '{referenceAttributeValue.UnresolvedReferenceValue}' cannot be parsed to a guid.");
                    break;
                case AttributeDataType.DateTime:
                case AttributeDataType.Binary:
                case AttributeDataType.Reference:
                case AttributeDataType.Boolean:
                case AttributeDataType.NotSet:
                default:
                    throw new ArgumentOutOfRangeException($"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} cannot be used for external ids.");
            }
        } 
        else if (externalIdAttribute.IsSecondaryExternalId)
        {
            switch (externalIdAttribute.Type)
            {
                case AttributeDataType.Text:
                    referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.StringValue == referenceAttributeValue.UnresolvedReferenceValue) ??
                                                      connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.StringValue == referenceAttributeValue.UnresolvedReferenceValue);
                    break;
                case AttributeDataType.Number:
                    if (int.TryParse(referenceAttributeValue.UnresolvedReferenceValue, out var intUnresolvedReferenceValue))
                        referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.IntValue == intUnresolvedReferenceValue) ??
                                                          connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.IntValue == intUnresolvedReferenceValue);
                    else
                        throw new InvalidCastException($"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} with value '{referenceAttributeValue.UnresolvedReferenceValue}' cannot be parsed to an int.");
                    break;
                case AttributeDataType.Guid:
                    if (Guid.TryParse(referenceAttributeValue.UnresolvedReferenceValue, out var guidUnresolvedReferenceValue))
                        referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.GuidValue == guidUnresolvedReferenceValue) ?? 
                                                          connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.GuidValue == guidUnresolvedReferenceValue);
                    else
                        throw new InvalidCastException($"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} with value '{referenceAttributeValue.UnresolvedReferenceValue}' cannot be parsed to a guid.");
                    break;
                case AttributeDataType.DateTime:
                case AttributeDataType.Binary:
                case AttributeDataType.Reference:
                case AttributeDataType.Boolean:
                case AttributeDataType.NotSet:
                default:
                    throw new ArgumentOutOfRangeException($"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} cannot be used for secondary external ids.");
            }    
        }
        else
        {
            throw new InvalidDataException("externalIdAttributeToUse wasn't external or secondary external id");
        }
        
        // no match, try and find a matching CSO in the database
        referencedConnectedSystemObject ??= await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, referenceAttributeValue.UnresolvedReferenceValue);

        if (referencedConnectedSystemObject != null)
        {
            // referenced cso found! set the ReferenceValue property, and leave the UnresolvedReferenceValue in place, as we'll use that for looking for updates to existing references on import.
            Log.Debug($"ResolveReferencesAsync: Matched an unresolved reference ({referenceAttributeValue.UnresolvedReferenceValue}) to CSO: {referencedConnectedSystemObject.Id}");
            referenceAttributeValue.ReferenceValue = referencedConnectedSystemObject;
        }
        else
        {
            // reference not found. referenced object probably out of container scope!
            // todo: make it a per-connected system setting whether to raise an error, or ignore. sometimes this is desirable.
            var activityRunProfileExecutionItem = _activity.RunProfileExecutionItems.SingleOrDefault(q => q.ConnectedSystemObject == csoToProcess);
            if (activityRunProfileExecutionItem != null && (activityRunProfileExecutionItem.ErrorType == null || (activityRunProfileExecutionItem.ErrorType == null && activityRunProfileExecutionItem.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet)))
            {
                activityRunProfileExecutionItem.ErrorMessage = $"Couldn't resolve a reference to a Connected System Object: {referenceAttributeValue.UnresolvedReferenceValue} (there may be more, view the Connected System Object for unresolved references). Make sure that Container Scope for the Connected System includes the location of the referenced object.";
                activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnresolvedReference;
            }
            else
            {
                throw new InvalidDataException($"Couldn't find an ActivityRunProfileExecutionItem for cso: {csoToProcess.Id}!");
            }

            Log.Debug($"ResolveReferencesAsync: Couldn't resolve a CSO reference: {referenceAttributeValue.UnresolvedReferenceValue}");
        }
    }
}
