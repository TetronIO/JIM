using System.Diagnostics;
using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using Serilog;
using JIM.Worker.Models;

namespace JIM.Worker.Processors;

public class SyncImportTaskProcessor
{
    private readonly JimApplication _jim;
    private readonly IConnector _connector;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly ActivityInitiatorType _initiatedByType;
    private readonly MetaverseObject? _initiatedByMetaverseObject;
    private readonly ApiKey? _initiatedByApiKey;
    private readonly JIM.Models.Activities.Activity _activity;
    private readonly List<ActivityRunProfileExecutionItem> _activityRunProfileExecutionItems;
    private readonly CancellationTokenSource _cancellationTokenSource;

    public SyncImportTaskProcessor(
        JimApplication jimApplication,
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        WorkerTask workerTask,
        CancellationTokenSource cancellationTokenSource)
    {
        _jim = jimApplication;
        _connector = connector;
        _connectedSystem = connectedSystem;
        _cancellationTokenSource = cancellationTokenSource;
        _connectedSystemRunProfile = connectedSystemRunProfile;
        _initiatedByType = workerTask.InitiatedByType;
        _initiatedByMetaverseObject = workerTask.InitiatedByMetaverseObject;
        _initiatedByApiKey = workerTask.InitiatedByApiKey;
        _activity = workerTask.Activity;

        // we will maintain this list separate from the activity, and add the items to the activity when all CSOs are persisted
        // this is so we don't create a dependency on CSOs with the Activity whilst we're still processing and updating the activity status, which would cause EF to persist
        // CSOs before we're ready to do so.
        _activityRunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>();
    }

    public async Task PerformFullImportAsync()
    {
        using var importSpan = Diagnostics.Sync.StartSpan("FullImport");
        importSpan.SetTag("connectedSystemId", _connectedSystem.Id);
        importSpan.SetTag("connectedSystemName", _connectedSystem.Name);
        importSpan.SetTag("connectorType", _connector.GetType().Name);

        Log.Verbose("PerformFullImportAsync: Starting");

        if (_connectedSystem.ObjectTypes == null)
            throw new InvalidDataException("PerformFullImportAsync: _connectedSystem.ObjectTypes was null. Cannot continue.");

        // we keep track of all processed CSOs here, so we can bulk-persist later, when all waves of CSO changes are prepared
        var connectedSystemObjectsToBeCreated = new List<ConnectedSystemObject>();
        var connectedSystemObjectsToBeUpdated = new List<ConnectedSystemObject>();

        // we keep track of the external ids for all imported objects (over all pages, if applicable) so we can look for deletions later.
        var externalIdsImported = new List<ExternalIdPair>();
        var totalObjectsImported = 0;

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Performing import");
        switch (_connector)
        {
            case IConnectorImportUsingCalls callBasedImportConnector:
            {
                using var connectorSpan = Diagnostics.Connector.StartSpan("CallBasedImport");

                // Inject certificate provider for connectors that support it
                if (callBasedImportConnector is IConnectorCertificateAware certificateAwareConnector)
                {
                    var certificateProvider = new CertificateProviderService(_jim);
                    certificateAwareConnector.SetCertificateProvider(certificateProvider);
                }

                // Inject credential protection for connectors that support it (for password decryption)
                if (callBasedImportConnector is IConnectorCredentialAware credentialAwareConnector)
                {
                    var credentialProtection = new CredentialProtectionService(
                        DataProtectionHelper.CreateProvider());
                    credentialAwareConnector.SetCredentialProtection(credentialProtection);
                }

                using (Diagnostics.Connector.StartSpan("OpenImportConnection"))
                {
                    callBasedImportConnector.OpenImportConnection(_connectedSystem.SettingValues, Log.Logger);
                }

                var initialPage = true;
                var paginationTokens = new List<ConnectedSystemPaginationToken>();
                var pageNumber = 0;

                // Keep track of the original persisted data at the START of the import.
                // This is critical for delta imports where subsequent pages must use the SAME
                // watermark (USN) as the first page to query for changes.
                // The connector will return a NEW watermark on the first page that we'll save
                // AFTER all pages are processed.
                var originalPersistedData = _connectedSystem.PersistedConnectorData;
                string? newPersistedData = null;

                while (initialPage || paginationTokens.Count > 0)
                {
                    // perform the import for this page
                    // IMPORTANT: Always pass the ORIGINAL persisted data to ensure consistent
                    // watermark queries across all pages of a delta import.
                    ConnectedSystemImportResult result;
                    using (Diagnostics.Connector.StartSpan("ImportPage").SetTag("pageNumber", pageNumber))
                    {
                        result = await callBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, paginationTokens, originalPersistedData, Log.Logger, _cancellationTokenSource.Token);
                    }
                    pageNumber++;
                    totalObjectsImported += result.ImportObjects.Count;

                    // Update progress - for paginated imports we don't know the total, but we track objects imported so far
                    _activity.ObjectsProcessed = totalObjectsImported;
                    await _jim.Activities.UpdateActivityMessageAsync(_activity, $"Imported {totalObjectsImported} objects (page {pageNumber})");

                    // add the external ids from this page worth of results to our external-id collection for later deletion calculation
                    AddExternalIdsToCollection(result, externalIdsImported);

                    // make sure we pass the pagination tokens back in on the next page (if there is one)
                    paginationTokens = result.PaginationTokens;

                    // Capture the new persisted connector data from the first page only.
                    // Subsequent pages return null (indicating "no change"), so we only capture once.
                    // We'll save this AFTER all pages are processed to avoid affecting watermark
                    // queries on subsequent pages.
                    if (result.PersistedConnectorData != null && newPersistedData == null)
                    {
                        Log.Debug($"ExecuteAsync: captured new persisted connector data from page {pageNumber}. old value: '{originalPersistedData}', new value: '{result.PersistedConnectorData}'");
                        newPersistedData = result.PersistedConnectorData;
                    }

                    // process the results from this page
                    using (Diagnostics.Sync.StartSpan("ProcessImportObjects").SetTag("objectCount", result.ImportObjects.Count))
                    {
                        await ProcessImportObjectsAsync(result, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
                    }

                    if (initialPage)
                        initialPage = false;
                }

                // Now that all pages are processed, update the persisted connector data
                // with the new watermark captured from the first page.
                if (newPersistedData != null && newPersistedData != originalPersistedData)
                {
                    Log.Debug($"ExecuteAsync: updating persisted connector data after all pages. old value: '{originalPersistedData}', new value: '{newPersistedData}'");
                    _connectedSystem.PersistedConnectorData = newPersistedData;
                    await UpdateConnectedSystemWithInitiatorAsync();
                }

                using (Diagnostics.Connector.StartSpan("CloseImportConnection"))
                {
                    callBasedImportConnector.CloseImportConnection();
                }
                break;
            }
            case IConnectorImportUsingFiles fileBasedImportConnector:
            {
                using var connectorSpan = Diagnostics.Connector.StartSpan("FileBasedImport");

                // file based connectors return all the results from the connected system in one go. no paging.
                ConnectedSystemImportResult result;
                using (Diagnostics.Connector.StartSpan("ReadFile"))
                {
                    result = await fileBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, Log.Logger, _cancellationTokenSource.Token);
                }
                totalObjectsImported = result.ImportObjects.Count;
                connectorSpan.SetTag("objectCount", totalObjectsImported);

                // Update progress - for file-based imports we know the total after reading the file
                _activity.ObjectsToProcess = totalObjectsImported;
                _activity.ObjectsProcessed = 0;
                await _jim.Activities.UpdateActivityMessageAsync(_activity, $"Processing {totalObjectsImported} objects");

                // todo: simplify externalIdsImported. objects are unnecessarily complex
                // add the external ids from the results to our external id collection for later deletion calculation
                AddExternalIdsToCollection(result, externalIdsImported);

                using (Diagnostics.Sync.StartSpan("ProcessImportObjects").SetTag("objectCount", totalObjectsImported))
                {
                    await ProcessImportObjectsAsync(result, connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
                }

                // Mark file processing complete
                _activity.ObjectsProcessed = totalObjectsImported;
                await _jim.Activities.UpdateActivityAsync(_activity);
                break;
            }
            default:
                throw new NotSupportedException("Connector inheritance type is not supported (not calls, not files)");
        }

        // process deletions
        // note: only run deletion detection for Full Imports
        // Delta Imports only return changed objects, so absence doesn't mean deletion
        // Explicit deletes from delta imports are handled in ProcessImportObjectsAsync via ObjectChangeType.Deleted
        // note: make sure it doesn't apply deletes if no objects were imported, as this suggests there was a problem collecting data from the connected system?
        // note: if it's expected that 0 imported objects means all objects were deleted, then an admin will have to clear the Connected System manually to achieve the same result.
        if (totalObjectsImported > 0 && _connectedSystemRunProfile.RunType == ConnectedSystemRunType.FullImport)
        {
            // Get count of existing CSOs to determine how many we need to check for deletions
            var existingCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
            _activity.ObjectsToProcess = existingCsoCount;
            _activity.ObjectsProcessed = 0;
            await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing deletions");
            using (Diagnostics.Sync.StartSpan("ProcessDeletions"))
            {
                await ProcessConnectedSystemObjectDeletionsAsync(externalIdsImported, connectedSystemObjectsToBeUpdated);
            }
        }

        // now that all objects have been imported, we can attempt to resolve unresolved reference attribute values
        // i.e. attempt to convert unresolved reference strings into hard links to other Connected System Objects
        var objectsWithReferences = connectedSystemObjectsToBeCreated.Count(cso => cso.AttributeValues.Any(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue))) +
                                    connectedSystemObjectsToBeUpdated.Count(cso => cso.PendingAttributeValueAdditions.Any(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue)));
        _activity.ObjectsToProcess = objectsWithReferences;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Resolving references");
        using (Diagnostics.Sync.StartSpan("ResolveReferences"))
        {
            await ResolveReferencesAsync(connectedSystemObjectsToBeCreated, connectedSystemObjectsToBeUpdated);
        }

        // now persist all CSOs which will also create the required Change Objects within the Activity.
        var totalChanges = connectedSystemObjectsToBeCreated.Count + connectedSystemObjectsToBeUpdated.Count;
        _activity.ObjectsToProcess = totalChanges;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Saving changes");
        using (var persistSpan = Diagnostics.Database.StartSpan("PersistConnectedSystemObjects"))
        {
            persistSpan.SetTag("createCount", connectedSystemObjectsToBeCreated.Count);
            persistSpan.SetTag("updateCount", connectedSystemObjectsToBeUpdated.Count);

            // Log RPEI error status before persistence
            var rpeiWithErrors = _activityRunProfileExecutionItems.Where(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
            if (rpeiWithErrors.Count > 0)
            {
                Log.Warning("About to persist {RpeiCount} RPEIs. {RpeiErrorCount} have errors: {ErrorDetails}",
                    _activityRunProfileExecutionItems.Count,
                    rpeiWithErrors.Count,
                    string.Join("; ", rpeiWithErrors.Select(r => $"[Id={r.Id}, ErrorType={r.ErrorType}, Message={r.ErrorMessage}]")));
            }

            await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(connectedSystemObjectsToBeCreated, _activityRunProfileExecutionItems);
            _activity.ObjectsProcessed = connectedSystemObjectsToBeCreated.Count;
            await _jim.Activities.UpdateActivityAsync(_activity);

            await _jim.ConnectedSystems.UpdateConnectedSystemObjectsAsync(connectedSystemObjectsToBeUpdated, _activityRunProfileExecutionItems);
            _activity.ObjectsProcessed = totalChanges;
            await _jim.Activities.UpdateActivityAsync(_activity);
        }

        // Reconcile pending exports against imported values (confirming import)
        // This confirms exported attribute changes or marks them for retry
        _activity.ObjectsToProcess = connectedSystemObjectsToBeUpdated.Count;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Reconciling pending exports");
        using (Diagnostics.Sync.StartSpan("ReconcilePendingExports"))
        {
            await ReconcilePendingExportsAsync(connectedSystemObjectsToBeUpdated);
        }

        // Validate all RPEIs before persisting - catch any that have no CSO and no error (indicates a bug)
        // Check for RPEIs where: Create operation, no CSO ID assigned, and no error recorded
        var orphanedRpeis = _activityRunProfileExecutionItems
            .Where(r => r.ObjectChangeType == ObjectChangeType.Added &&
                        r.ConnectedSystemObjectId == null &&
                        r.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();

        if (orphanedRpeis.Count > 0)
        {
            foreach (var orphanedRpei in orphanedRpeis)
            {
                var extId = orphanedRpei.ConnectedSystemObject?.ExternalIdAttributeValue?.StringValue ?? "[unknown]";
                var hasCsoRef = orphanedRpei.ConnectedSystemObject != null;
                Log.Error("VALIDATION FAILURE: RPEI has ObjectChangeType=Create but no ConnectedSystemObjectId and no error. HasCsoRef={HasCsoRef}, ExtId={ExtId}. This is a bug! Setting error type.",
                    hasCsoRef, extId);
                orphanedRpei.ErrorType = ActivityRunProfileExecutionItemErrorType.CsoCreationFailed;
                orphanedRpei.ErrorMessage = $"Internal error: RPEI was created for import (ExtId={extId}) but CSO was not persisted. CSO reference exists={hasCsoRef}. This indicates a bug in the persistence layer.";
            }
        }

        // now persist the activity run profile execution items with the activity
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Creating activity run profile execution items");
        _activity.AddRunProfileExecutionItems(_activityRunProfileExecutionItems);
        await _jim.Activities.UpdateActivityAsync(_activity);

        importSpan.SetTag("totalObjectsImported", totalObjectsImported);
        importSpan.SetTag("objectsCreated", connectedSystemObjectsToBeCreated.Count);
        importSpan.SetTag("objectsUpdated", connectedSystemObjectsToBeUpdated.Count);
        importSpan.SetSuccess();
    }

    private async Task ProcessConnectedSystemObjectDeletionsAsync(IReadOnlyCollection<ExternalIdPair> externalIdsImported, ICollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated)
    {
        if (_connectedSystem.ObjectTypes == null)
            return;

        // Get the IDs of CSOs that were already processed in this import run
        // These should not be marked as obsolete even if their external ID isn't in the import (e.g., because their
        // external ID was updated during import processing and the new value isn't in externalIdsImported)
        var processedCsoIds = connectedSystemObjectsToBeUpdated.Select(cso => cso.Id).ToHashSet();

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
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated, processedCsoIds);
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
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated, processedCsoIds);
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
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated, processedCsoIds);
                    break;
                }
                case AttributeDataType.LongNumber:
                {
                    // get the long connected system object external ids for this object type
                    var connectedSystemObjectExternalIdsOfTypeLong = await _jim.ConnectedSystems.GetAllExternalIdAttributeValuesOfTypeLongAsync(_connectedSystem.Id, selectedObjectType.Id);

                    // get the long import object external ids for this object type
                    var connectedSystemLongExternalIdValues = externalIdsImported
                        .Where(q => q.ConnectedSystemObjectType.Id == selectedObjectType.Id)
                        .SelectMany(externalId => externalId.ConnectedSystemImportObjectAttribute.LongValues);

                    // create a collection with the connected system objects no longer in the connected system for this object type
                    var connectedSystemObjectDeletesExternalIds = connectedSystemObjectExternalIdsOfTypeLong.Except(connectedSystemLongExternalIdValues);

                    // obsolete the connected system objects no longer in the connected system for this object type
                    foreach (var externalId in connectedSystemObjectDeletesExternalIds)
                        await ObsoleteConnectedSystemObjectAsync(externalId, objectTypeExternalIdAttribute.Id, connectedSystemObjectsToBeUpdated, processedCsoIds);
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

    /// <summary>
    /// Have any CSOs in our Connected System not been imported, and thus are now no longer valid? Put them into an
    /// Obsolete state, so they can be processed for deletion during a synchronisation run.
    /// </summary>
    /// <param name="connectedSystemObjectExternalId">The value for the External ID attribute.</param>
    /// <param name="connectedSystemAttributeId">The unique identifier for the attribute that represents the External ID in the current Connected System.</param>
    /// <param name="connectedSystemObjectsToBeUpdated">The cache of CSOs that have been updated as part of this import run.</param>
    /// <typeparam name="T">The type for the External ID attribute.</typeparam>
    private async Task ObsoleteConnectedSystemObjectAsync<T>(T connectedSystemObjectExternalId, int connectedSystemAttributeId, ICollection<ConnectedSystemObject> connectedSystemObjectsToBeUpdated, HashSet<Guid> processedCsoIds)
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

        // Skip CSOs that were already processed in this import run (e.g., matched by secondary external ID)
        // Their external ID may have been updated during import, so they appear as "not in import" by old ID
        if (processedCsoIds.Contains(cso.Id))
        {
            Log.Debug("ObsoleteConnectedSystemObjectAsync: CSO {CsoId} was already processed in this import run. Skipping obsolete.",
                cso.Id);
            return;
        }

        // we need to create a run profile execution item for the object deletion. it will get persisted in the activity tree.
        var activityRunProfileExecutionItem = new ActivityRunProfileExecutionItem();
        _activityRunProfileExecutionItems.Add(activityRunProfileExecutionItem);
        activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Deleted;
        activityRunProfileExecutionItem.ConnectedSystemObject = cso;
        activityRunProfileExecutionItem.ConnectedSystemObjectId = cso.Id;
        // Snapshot the external ID so it's preserved even after CSO is deleted
        activityRunProfileExecutionItem.ExternalIdSnapshot = cso.ExternalIdAttributeValue?.StringValue;

        // mark it obsolete internally, so that it's deleted when a synchronisation run profile is performed.
        // Note: The RPEI uses Delete (user-facing), but the CSO status uses Obsolete (internal state)
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;

        // add it to the list of objects to be updated. this will persist and create a change object in the activity tree.
        connectedSystemObjectsToBeUpdated.Add(cso);
    }

    /// <summary>
    /// Adds the External IDs on CSOs returned in an import result to a collection to help with resolving references later.
    /// </summary>
    /// <param name="importResult">The entire, or a page's worth of import results from a Connected System to retrieve External IDs from.</param>
    /// <param name="externalIdsImported">The collection used to store all External IDs, over all pages of import results.</param>
    private void AddExternalIdsToCollection(ConnectedSystemImportResult importResult, ICollection<ExternalIdPair> externalIdsImported)
    {
        if (_connectedSystem.ObjectTypes == null)
            return;

        // add the external ids from the results to our external id collection
        foreach (var importedObject in importResult.ImportObjects)
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

        // Track external IDs seen in THIS batch to detect same-batch duplicates.
        // Key: "objectTypeId:externalIdValue" (composite key to handle multiple object types)
        // Value: Tuple of (index in ImportObjects, RPEI, CSO if created)
        // When duplicate found, we error BOTH objects - no "random winner" based on file order.
        var seenExternalIds = new Dictionary<string, (int index, ActivityRunProfileExecutionItem rpei, ConnectedSystemObject? cso)>(StringComparer.OrdinalIgnoreCase);
        var knownDuplicateExternalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // decision: do we want to load the whole connector space into memory to maximise performance? for now, let's keep it db-centric.
        // todo: experiment with using parallel foreach to see if we can speed up processing
        var importIndex = -1;
        foreach (var importObject in connectedSystemImportResult.ImportObjects)
        {
            importIndex++;

            // this will store the detail for the import object that will persist in the history for the run
            var activityRunProfileExecutionItem = new ActivityRunProfileExecutionItem();
            _activityRunProfileExecutionItems.Add(activityRunProfileExecutionItem);

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

                // Same-batch duplicate detection: Check if another object in THIS batch has the same external ID.
                // If so, error BOTH objects - we don't pick a "random winner" based on file order.
                // This forces the data owner to fix the source data.
                var externalIdAttribute = csObjectType.Attributes.First(a => a.IsExternalId);

                // DEBUG: Log what attributes are present in this import object
                Log.Debug("ProcessImportObjectsAsync: Import object at index {Index}. ObjectType: {ObjectType}. Expected external ID attribute: {ExternalIdAttrName}. Available attributes: {Attributes}",
                    importIndex, importObject.ObjectType, externalIdAttribute.Name,
                    string.Join(", ", importObject.Attributes.Select(a => a.Name)));

                var externalIdImportAttr = importObject.Attributes.SingleOrDefault(a => a.Name.Equals(externalIdAttribute.Name, StringComparison.OrdinalIgnoreCase));

                // DEBUG: Log whether external ID attribute was found
                if (externalIdImportAttr == null)
                {
                    Log.Warning("ProcessImportObjectsAsync: External ID attribute '{ExternalIdName}' NOT FOUND in import object at index {Index}. Available attributes: {Attributes}. SKIPPING DUPLICATE DETECTION.",
                        externalIdAttribute.Name, importIndex, string.Join(", ", importObject.Attributes.Select(a => a.Name)));
                }
                else
                {
                    Log.Debug("ProcessImportObjectsAsync: External ID attribute '{ExternalIdName}' FOUND in import object at index {Index}.",
                        externalIdAttribute.Name, importIndex);
                }

                if (externalIdImportAttr != null)
                {
                    // Extract the external ID value as a string for tracking (works for all data types)
                    var externalIdValue = externalIdAttribute.Type switch
                    {
                        AttributeDataType.Text => externalIdImportAttr.StringValues.FirstOrDefault(),
                        AttributeDataType.Number => externalIdImportAttr.IntValues.FirstOrDefault().ToString(),
                        AttributeDataType.LongNumber => externalIdImportAttr.LongValues.FirstOrDefault().ToString(),
                        AttributeDataType.Guid => externalIdImportAttr.GuidValues.FirstOrDefault().ToString(),
                        _ => null
                    };

                    if (!string.IsNullOrEmpty(externalIdValue))
                    {
                        // Composite key: objectTypeId:externalIdValue to handle multiple object types in same import
                        var duplicateKey = $"{csObjectType.Id}:{externalIdValue}";

                        // DEBUG: Log the external ID value extracted
                        Log.Debug("ProcessImportObjectsAsync: Extracted external ID value '{ExternalIdValue}' from import object at index {Index}. Duplicate key: {DuplicateKey}",
                            externalIdValue, importIndex, duplicateKey);

                        // Snapshot the external ID for error reporting
                        activityRunProfileExecutionItem.ExternalIdSnapshot = externalIdValue;

                        if (knownDuplicateExternalIds.Contains(duplicateKey))
                        {
                            // This is the 3rd+ duplicate - just mark it as error (first already handled)
                            activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.DuplicateObject;
                            activityRunProfileExecutionItem.ErrorMessage = $"Duplicate external ID '{externalIdValue}' found in the same import batch. All objects with this external ID have been rejected. Fix the source data to ensure unique external IDs.";
                            Log.Warning("ProcessImportObjectsAsync: Duplicate external ID '{ExternalId}' (3rd+ occurrence) at index {Index}. Marking as error.",
                                externalIdValue, importIndex);
                            continue;
                        }

                        if (seenExternalIds.TryGetValue(duplicateKey, out var firstOccurrence))
                        {
                            // Duplicate found! Error BOTH objects.
                            Log.Warning("ProcessImportObjectsAsync: Duplicate external ID '{ExternalId}' found at index {CurrentIndex}. First occurrence was at index {FirstIndex}. Erroring BOTH objects.",
                                externalIdValue, importIndex, firstOccurrence.index);

                            // Mark THIS object as duplicate
                            activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.DuplicateObject;
                            activityRunProfileExecutionItem.ErrorMessage = $"Duplicate external ID '{externalIdValue}' found in the same import batch. All objects with this external ID have been rejected. Fix the source data to ensure unique external IDs.";

                            // Go back and mark the FIRST object as duplicate too
                            firstOccurrence.rpei.ErrorType = ActivityRunProfileExecutionItemErrorType.DuplicateObject;
                            firstOccurrence.rpei.ErrorMessage = $"Duplicate external ID '{externalIdValue}' found in the same import batch. All objects with this external ID have been rejected. Fix the source data to ensure unique external IDs.";
                            // Reset change type since no object was actually created/updated
                            firstOccurrence.rpei.ObjectChangeType = ObjectChangeType.NotSet;

                            // If the first object had already created a CSO, remove it from the create list
                            if (firstOccurrence.cso != null)
                            {
                                var removed = connectedSystemObjectsToBeCreated.Remove(firstOccurrence.cso);
                                if (removed)
                                {
                                    Log.Debug("ProcessImportObjectsAsync: Removed CSO for first occurrence of duplicate external ID '{ExternalId}' from create list.",
                                        externalIdValue);
                                }
                                // Clear the CSO reference from the RPEI since we're not persisting it
                                firstOccurrence.rpei.ConnectedSystemObject = null;
                            }

                            // Track this as a known duplicate for any subsequent occurrences (3rd, 4th, etc.)
                            knownDuplicateExternalIds.Add(duplicateKey);

                            // Remove from seenExternalIds since we've handled it
                            seenExternalIds.Remove(duplicateKey);

                            continue;
                        }

                        // First time seeing this external ID in this batch - track it
                        // We'll update the CSO reference after it's created (if it gets created)
                        seenExternalIds[duplicateKey] = (importIndex, activityRunProfileExecutionItem, null);
                    }
                }

                // see if we already have a matching connected system object for this imported object within JIM
                ConnectedSystemObject? connectedSystemObject;
                using (Diagnostics.Sync.StartSpan("FindMatchingCso"))
                {
                    connectedSystemObject = await TryAndFindMatchingConnectedSystemObjectAsync(importObject, csObjectType);
                }

                // Handle delete requests from delta imports (e.g., LDAP changelog)
                // When a connector specifies Delete, mark the existing CSO as Obsolete internally
                if (importObject.ChangeType == ObjectChangeType.Deleted)
                {
                    if (connectedSystemObject != null)
                    {
                        // RPEI uses Delete (user-facing), CSO status uses Obsolete (internal state)
                        activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Deleted;
                        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                        activityRunProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
                        // Snapshot the external ID so it's preserved even after CSO is deleted
                        activityRunProfileExecutionItem.ExternalIdSnapshot = connectedSystemObject.ExternalIdAttributeValue?.StringValue;
                        connectedSystemObject.Status = ConnectedSystemObjectStatus.Obsolete;
                        connectedSystemObject.LastUpdated = DateTime.UtcNow;
                        connectedSystemObjectsToBeUpdated.Add(connectedSystemObject);
                        Log.Information("ProcessImportObjectsAsync: Connector requested delete for object with external ID in type '{ObjectType}'. Marking CSO {CsoId} for deletion.",
                            importObject.ObjectType, connectedSystemObject.Id);
                    }
                    else
                    {
                        // Connector says delete, but we don't have the object - nothing to do, remove the RPEI
                        _activityRunProfileExecutionItems.Remove(activityRunProfileExecutionItem);
                        Log.Debug("ProcessImportObjectsAsync: Connector requested delete for object type '{ObjectType}' but no matching CSO found. Ignoring.",
                            importObject.ObjectType);
                    }
                    continue;
                }

                // is new - new cso required
                // is existing - apply any changes to the cso from the import object
                if (connectedSystemObject == null)
                {
                    // Log warning if connector said Update but object doesn't exist
                    if (importObject.ChangeType == ObjectChangeType.Updated)
                    {
                        Log.Warning("ProcessImportObjectsAsync: Connector indicated Update for object type '{ObjectType}' but no matching CSO found. Creating new object instead. " +
                            "ConnectedSystem: {ConnectedSystemId} ({ConnectedSystemName}), RunProfile: {RunProfileId} ({RunProfileName}), Activity: {ActivityId}",
                            importObject.ObjectType,
                            _connectedSystem.Id, _connectedSystem.Name,
                            _connectedSystemRunProfile.Id, _connectedSystemRunProfile.Name,
                            _activity.Id);
                    }

                    activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Added;

                    // Note: ExternalIdSnapshot is already set earlier in the duplicate detection block (line ~564)
                    // for all data types (Text, Number, LongNumber, Guid). No need to set it again here.

                    connectedSystemObject = CreateConnectedSystemObjectFromImportObject(importObject, csObjectType, activityRunProfileExecutionItem);

                    // cso could be null at this point if the create-cso flow failed due to unexpected import attributes, etc.
                    if (connectedSystemObject != null)
                    {
                        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                        connectedSystemObjectsToBeCreated.Add(connectedSystemObject);

                        // Update the seenExternalIds entry with the CSO reference so we can remove it if a duplicate is found later
                        var extIdAttr = csObjectType.Attributes.First(a => a.IsExternalId);
                        var extIdImportAttr = importObject.Attributes
                            .FirstOrDefault(a => a.Name.Equals(extIdAttr.Name, StringComparison.OrdinalIgnoreCase));
                        if (extIdImportAttr != null)
                        {
                            var extIdVal = extIdAttr.Type switch
                            {
                                AttributeDataType.Text => extIdImportAttr.StringValues.FirstOrDefault(),
                                AttributeDataType.Number => extIdImportAttr.IntValues.FirstOrDefault().ToString(),
                                AttributeDataType.LongNumber => extIdImportAttr.LongValues.FirstOrDefault().ToString(),
                                AttributeDataType.Guid => extIdImportAttr.GuidValues.FirstOrDefault().ToString(),
                                _ => null
                            };
                            if (!string.IsNullOrEmpty(extIdVal))
                            {
                                var dupKey = $"{csObjectType.Id}:{extIdVal}";
                                if (seenExternalIds.ContainsKey(dupKey))
                                {
                                    seenExternalIds[dupKey] = (seenExternalIds[dupKey].index, seenExternalIds[dupKey].rpei, connectedSystemObject);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Ensure the RPEI has an error type if CSO creation failed but no specific error was set
                        if (activityRunProfileExecutionItem.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet)
                        {
                            activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.CsoCreationFailed;
                            var extIdForError = activityRunProfileExecutionItem.ExternalIdSnapshot ?? "[unknown]";
                            activityRunProfileExecutionItem.ErrorMessage = $"Failed to create Connected System Object for import object with external ID '{extIdForError}'. No specific error was recorded.";
                            Log.Error("ProcessImportObjectsAsync: CSO creation failed for external ID '{ExternalId}' with no specific error. This indicates a bug in import processing.",
                                extIdForError);
                        }
                    }
                }
                else
                {
                    // Log warning if connector said Add but object already exists
                    if (importObject.ChangeType == ObjectChangeType.Added)
                    {
                        Log.Warning("ProcessImportObjectsAsync: Connector indicated Add for object type '{ObjectType}' but CSO {CsoId} already exists. Updating instead. " +
                            "ConnectedSystem: {ConnectedSystemId} ({ConnectedSystemName}), RunProfile: {RunProfileId} ({RunProfileName}), Activity: {ActivityId}",
                            importObject.ObjectType, connectedSystemObject.Id,
                            _connectedSystem.Id, _connectedSystem.Name,
                            _connectedSystemRunProfile.Id, _connectedSystemRunProfile.Name,
                            _activity.Id);
                    }

                    // Transition PendingProvisioning CSOs to Normal status now that import confirms they exist
                    // in the connected system. This is essential for proper reconciliation and subsequent lookups.
                    var statusTransitioned = false;
                    if (connectedSystemObject.Status == ConnectedSystemObjectStatus.PendingProvisioning)
                    {
                        Log.Information("ProcessImportObjectsAsync: Transitioning CSO {CsoId} from PendingProvisioning to Normal status. Object now confirmed in connected system.",
                            connectedSystemObject.Id);
                        connectedSystemObject.Status = ConnectedSystemObjectStatus.Normal;
                        statusTransitioned = true;
                    }

                    // Calculate attribute changes before processing
                    UpdateConnectedSystemObjectFromImportObject(importObject, connectedSystemObject, csObjectType, activityRunProfileExecutionItem);

                    // Check if there are any actual attribute changes
                    var hasAttributeChanges = connectedSystemObject.PendingAttributeValueAdditions.Count > 0 ||
                                              connectedSystemObject.PendingAttributeValueRemovals.Count > 0;

                    // Always add to update list - needed for reference resolution even if no attribute changes
                    // The update list is used by ResolveReferencesAsync to resolve references between objects
                    if (!connectedSystemObjectsToBeUpdated.Any(cso => cso.Id == connectedSystemObject.Id))
                    {
                        connectedSystemObjectsToBeUpdated.Add(connectedSystemObject);
                    }
                    else
                    {
                        Log.Warning("ProcessImportObjectsAsync: CSO {CsoId} was already matched by a previous import object. Skipping duplicate addition to update list.",
                            connectedSystemObject.Id);
                    }

                    // Only create RPEI if there are actual changes (attributes or status transition)
                    if (hasAttributeChanges || statusTransitioned)
                    {
                        activityRunProfileExecutionItem.ObjectChangeType = ObjectChangeType.Updated;
                        activityRunProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                        activityRunProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
                        // Snapshot the external ID so it's preserved even if CSO is later deleted
                        activityRunProfileExecutionItem.ExternalIdSnapshot = connectedSystemObject.ExternalIdAttributeValue?.StringValue;
                        connectedSystemObject.LastUpdated = DateTime.UtcNow;
                    }
                    else
                    {
                        // No changes - remove the RPEI from the list (it was added at the start of the loop)
                        _activityRunProfileExecutionItems.Remove(activityRunProfileExecutionItem);
                        Log.Debug("ProcessImportObjectsAsync: No attribute changes for CSO {CsoId}. Skipping RPEI creation.",
                            connectedSystemObject.Id);
                    }
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

        // DEBUG: Summary statistics for duplicate detection
        var duplicateCount = _activityRunProfileExecutionItems.Count(x => x.ErrorType == ActivityRunProfileExecutionItemErrorType.DuplicateObject);
        var successCount = _activityRunProfileExecutionItems.Count(x => x.ErrorType == null);
        var errorCount = _activityRunProfileExecutionItems.Count(x => x.ErrorType != null);
        Log.Information("ProcessImportObjectsAsync: SUMMARY - Total objects: {Total}, Processed successfully: {Success}, Errors: {Errors}, Duplicates detected: {Duplicates}. Seen external IDs tracked: {SeenCount}",
            connectedSystemImportResult.ImportObjects.Count, successCount, errorCount, duplicateCount, seenExternalIds.Count);
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
            // first remove any null attribute values. this might mean we'll be left with no values at all
            attribute.GuidValues.RemoveAll(q => q.Equals(null));
            attribute.IntValues.RemoveAll(q => q.Equals(null));
            attribute.LongValues.RemoveAll(q => q.Equals(null));
            attribute.StringValues.RemoveAll(string.IsNullOrEmpty);
            attribute.ByteValues.RemoveAll(q => q.Equals(null));
            attribute.ReferenceValues.RemoveAll(string.IsNullOrEmpty);

            // now work out if we're left with any values at all
            var noGuids = attribute.GuidValues.Count == 0;
            var noIntegers = attribute.IntValues.Count == 0;
            var noLongs = attribute.LongValues.Count == 0;
            var noStrings = attribute.StringValues.Count == 0;
            var noBool = !attribute.BoolValue.HasValue;
            var noDateTime = !attribute.DateTimeValue.HasValue;
            var noBytes = attribute.ByteValues.Count == 0;
            var noReferences = attribute.ReferenceValues.Count == 0;

            // if all types of values are empty, we'll add this attribute to a list for removal
            if (noGuids && noIntegers && noLongs && noStrings && noBool && noDateTime && noBytes && noReferences)
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
            importObjectAttribute.LongValues.Count > 1 ||
            importObjectAttribute.StringValues.Count > 1 ||
            importObjectAttribute.GuidValues.Count > 1)
            throw new ExternalIdAttributeNotSingleValuedException($"External Id attribute ({externalIdAttribute.Name}) on the imported object has multiple values! The External Id attribute must be single-valued.");

        // First, try to find CSO by primary external ID
        ConnectedSystemObject? cso = externalIdAttribute.Type switch
        {
            AttributeDataType.Text when importObjectAttribute.StringValues.Count == 0 =>
                throw new ExternalIdAttributeValueMissingException($"External Id string attribute ({externalIdAttribute.Name}) on the imported object has no value."),
            AttributeDataType.Text =>
                await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.StringValues[0]),
            AttributeDataType.Number when importObjectAttribute.IntValues.Count == 0 =>
                throw new ExternalIdAttributeValueMissingException($"External Id number attribute({externalIdAttribute.Name}) on the imported object has no value."),
            AttributeDataType.Number =>
                await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.IntValues[0]),
            AttributeDataType.LongNumber when importObjectAttribute.LongValues.Count == 0 =>
                throw new ExternalIdAttributeValueMissingException($"External Id long number attribute({externalIdAttribute.Name}) on the imported object has no value."),
            AttributeDataType.LongNumber =>
                await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.LongValues[0]),
            AttributeDataType.Guid when importObjectAttribute.GuidValues.Count == 0 =>
                throw new ExternalIdAttributeValueMissingException($"External Id guid attribute ({externalIdAttribute.Name}) on the imported object has no value."),
            AttributeDataType.Guid =>
                await _jim.ConnectedSystems.GetConnectedSystemObjectByAttributeAsync(_connectedSystem.Id, externalIdAttribute.Id, importObjectAttribute.GuidValues[0]),
            _ => throw new InvalidDataException($"TryAndFindMatchingConnectedSystemObjectAsync: Unsupported connected system object type External Id attribute type: {externalIdAttribute.Type}")
        };

        if (cso != null)
            return cso;

        // No match found by primary external ID. Check for PendingProvisioning CSOs by secondary external ID.
        // This handles the case where a CSO was created during provisioning evaluation but the object
        // hasn't been imported yet with its system-assigned external ID (e.g., LDAP objectGUID).
        var secondaryExternalIdAttribute = connectedSystemObjectType.Attributes.FirstOrDefault(a => a.IsSecondaryExternalId);
        if (secondaryExternalIdAttribute == null)
            return null;

        var secondaryIdImportAttr = connectedSystemImportObject.Attributes.SingleOrDefault(
            csioa => csioa.Name.Equals(secondaryExternalIdAttribute.Name, StringComparison.OrdinalIgnoreCase));
        if (secondaryIdImportAttr == null)
            return null;

        cso = secondaryExternalIdAttribute.Type switch
        {
            AttributeDataType.Text when secondaryIdImportAttr.StringValues.Count > 0 =>
                await _jim.ConnectedSystems.GetConnectedSystemObjectBySecondaryExternalIdAsync(_connectedSystem.Id, connectedSystemObjectType.Id, secondaryIdImportAttr.StringValues[0]),
            _ => null
        };

        // Only return PendingProvisioning CSOs - if it's already Normal, the primary ID lookup should have found it
        if (cso != null && cso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
        {
            Log.Information("TryAndFindMatchingConnectedSystemObjectAsync: Found PendingProvisioning CSO {CsoId} by secondary external ID '{SecondaryId}'. This confirms a provisioned object.",
                cso.Id, secondaryIdImportAttr.StringValues[0]);
            return cso;
        }

        return null;
    }

    private ConnectedSystemObject? CreateConnectedSystemObjectFromImportObject(ConnectedSystemImportObject connectedSystemImportObject, ConnectedSystemObjectType connectedSystemObjectType, ActivityRunProfileExecutionItem activityRunProfileExecutionItem)
    {
        var stopwatch = Stopwatch.StartNew();

        // new object - create connected system object using data from an import object
        var connectedSystemObject = new ConnectedSystemObject
        {
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            ExternalIdAttributeId = connectedSystemObjectType.Attributes.First(a => a.IsExternalId).Id,
            Type = connectedSystemObjectType,
            TypeId = connectedSystemObjectType.Id
        };

        // not every system uses a secondary external id attribute, but some do, i.e. LDAP
        var secondaryExternalIdAttribute = connectedSystemObjectType.Attributes.FirstOrDefault(a => a.IsSecondaryExternalId);
        if (secondaryExternalIdAttribute != null)
            connectedSystemObject.SecondaryExternalIdAttributeId = secondaryExternalIdAttribute.Id;

        var csoIsInvalid = false;
        foreach (var importObjectAttribute in connectedSystemImportObject.Attributes)
        {
            // find the connected system schema attribute that has the same name
            var csAttribute = connectedSystemObjectType.Attributes.SingleOrDefault(q => q.Name.Equals(importObjectAttribute.Name, StringComparison.OrdinalIgnoreCase));
            if (csAttribute == null)
            {
                // unexpected import attribute!
                Log.Error("CreateConnectedSystemObjectFromImportObject: UnexpectedAttribute error - attribute '{AttributeName}' not found in schema for object type '{ObjectType}'. Available schema attributes: {AvailableAttributes}. RPEI ID: {RpeiId}",
                    importObjectAttribute.Name,
                    connectedSystemObjectType.Name,
                    string.Join(", ", connectedSystemObjectType.Attributes.Select(a => a.Name)),
                    activityRunProfileExecutionItem.Id);

                activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnexpectedAttribute;
                activityRunProfileExecutionItem.ErrorMessage = $"Was not expecting the imported object attribute '{importObjectAttribute.Name}'.";
                csoIsInvalid = true;

                Log.Error("CreateConnectedSystemObjectFromImportObject: Set ErrorType={ErrorType}, ErrorMessage={ErrorMessage} on RPEI {RpeiId}",
                    activityRunProfileExecutionItem.ErrorType,
                    activityRunProfileExecutionItem.ErrorMessage,
                    activityRunProfileExecutionItem.Id);
                break;
            }

            // assign the attribute value(s)
            // remember, JIM requires an attribute value object for each connected system attribute value, i.e. everything's multi-valued capable
            switch (csAttribute.Type)
            {
                case AttributeDataType.Text:
                    foreach (var importObjectAttributeStringValue in importObjectAttribute.StringValues)
                    {
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            AttributeId = csAttribute.Id,
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
                            AttributeId = csAttribute.Id,
                            IntValue = importObjectAttributeIntValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.LongNumber:
                    foreach (var importObjectAttributeLongValue in importObjectAttribute.LongValues)
                    {
                        connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                        {
                            Attribute = csAttribute,
                            AttributeId = csAttribute.Id,
                            LongValue = importObjectAttributeLongValue,
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
                            AttributeId = csAttribute.Id,
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
                            AttributeId = csAttribute.Id,
                            GuidValue = importObjectAttributeGuidValue,
                            ConnectedSystemObject = connectedSystemObject
                        });
                    }
                    break;
                case AttributeDataType.DateTime:
                    connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                    {
                        Attribute = csAttribute,
                        AttributeId = csAttribute.Id,
                        DateTimeValue = importObjectAttribute.DateTimeValue,
                        ConnectedSystemObject = connectedSystemObject
                    });
                    break;
                case AttributeDataType.Boolean:
                    connectedSystemObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                    {
                        Attribute = csAttribute,
                        AttributeId = csAttribute.Id,
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
                            AttributeId = csAttribute.Id,
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
        {
            Log.Error("CreateConnectedSystemObjectFromImportObject: Returning null because csoIsInvalid=true. RPEI {RpeiId} has ErrorType={ErrorType}, ErrorMessage={ErrorMessage}",
                activityRunProfileExecutionItem.Id,
                activityRunProfileExecutionItem.ErrorType,
                activityRunProfileExecutionItem.ErrorMessage);
            return null;
        }

        // now associate the cso with the activityRunProfileExecutionItem
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
                var csoAttribute = connectedSystemObject.Type.Attributes.Single(a => a.Name.Equals(csoAttributeName, StringComparison.OrdinalIgnoreCase));
                var importedObjectAttributeList = connectedSystemImportObject.Attributes.Where(a => a.Name.Equals(csoAttributeName, StringComparison.OrdinalIgnoreCase)).ToList();
                var importedObjectAttribute = importedObjectAttributeList[0];

                // process attribute additions and removals...
                switch (csoAttribute.Type)
                {
                    case AttributeDataType.Text:
                        // find values on the cso of type string that aren't on the imported object and remove them first
                        var missingStringAttributeValues = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.StringValue != null && !importedObjectAttribute.StringValues.Any(i => i.Equals(av.StringValue))).ToList();
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(missingStringAttributeValues);

                        // find imported values of type string that aren't on the cso and add them
                        var newStringValues = importedObjectAttribute.StringValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.StringValue != null && av.StringValue.Equals(sv))).ToList();
                        foreach (var newStringValue in newStringValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, StringValue = newStringValue });
                        break;

                    case AttributeDataType.Number:
                        // find values on the cso of type int that aren't on the imported object and remove them first
                        var missingIntAttributeValues = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.IntValue != null && !importedObjectAttribute.IntValues.Any(i => i.Equals(av.IntValue))).ToList();
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(missingIntAttributeValues);

                        // find imported values of type int that aren't on the cso and add them
                        var newIntValues = importedObjectAttribute.IntValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.IntValue != null && av.IntValue.Equals(sv))).ToList();
                        foreach (var newIntValue in newIntValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, IntValue = newIntValue });
                        break;

                    case AttributeDataType.LongNumber:
                        // find values on the cso of type long that aren't on the imported object and remove them first
                        var missingLongAttributeValues = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.LongValue != null && !importedObjectAttribute.LongValues.Any(i => i.Equals(av.LongValue))).ToList();
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(missingLongAttributeValues);

                        // find imported values of type long that aren't on the cso and add them
                        var newLongValues = importedObjectAttribute.LongValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.LongValue != null && av.LongValue.Equals(sv))).ToList();
                        foreach (var newLongValue in newLongValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, LongValue = newLongValue });
                        break;

                    case AttributeDataType.DateTime:
                        // date time attribute types can only be single-valued by nature. handle differently to multivalued attribute types.
                        var existingCsoDateTimeAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id);
                        if (existingCsoDateTimeAttributeValue == null)
                        {
                            // we don't have a CSO value for this attribute. set the initial value
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
                        var missingByteArrayAttributeValues = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.ByteValue != null && !importedObjectAttribute.ByteValues.Any(i => Utilities.Utilities.AreByteArraysTheSame(i, av.ByteValue))).ToList();
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(missingByteArrayAttributeValues);

                        // find imported values of type byte array that aren't on the cso and add them
                        var newByteArrayValues = importedObjectAttribute.ByteValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(sv, av.ByteValue))).ToList();
                        foreach (var newByteArrayValue in newByteArrayValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, ByteValue = newByteArrayValue });
                        break;

                    case AttributeDataType.Reference:
                        // find unresolved reference values on the cso that aren't on the imported object and remove them first
                        // Note: Reference values are compared case-sensitively to preserve data fidelity from source systems
                        var missingUnresolvedReferenceValues = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.UnresolvedReferenceValue != null && !importedObjectAttribute.ReferenceValues.Any(i => i.Equals(av.UnresolvedReferenceValue, StringComparison.Ordinal))).ToList();
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(missingUnresolvedReferenceValues);

                        // find imported unresolved reference values that aren't on the cso and add them
                        var newUnresolvedReferenceValues = importedObjectAttribute.ReferenceValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.UnresolvedReferenceValue != null && av.UnresolvedReferenceValue.Equals(sv, StringComparison.Ordinal))).ToList();
                        foreach (var newUnresolvedReferenceValue in newUnresolvedReferenceValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, UnresolvedReferenceValue = newUnresolvedReferenceValue });
                        break;

                    case AttributeDataType.Guid:
                        // find values on the cso of type Guid that aren't on the imported object and remove them first
                        var missingGuidAttributeValues = connectedSystemObject.AttributeValues.Where(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.GuidValue != null && !importedObjectAttribute.GuidValues.Any(i => i.Equals(av.GuidValue))).ToList();
                        connectedSystemObject.PendingAttributeValueRemovals.AddRange(missingGuidAttributeValues);

                        // find imported values of type Guid that aren't on the cso and add them
                        var newGuidValues = importedObjectAttribute.GuidValues.Where(sv => !connectedSystemObject.AttributeValues.Any(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id && av.GuidValue != null && av.GuidValue.Equals(sv))).ToList();
                        foreach (var newGuidValue in newGuidValues)
                            connectedSystemObject.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = connectedSystemObject, Attribute = csoAttribute, GuidValue = newGuidValue });
                        break;

                    case AttributeDataType.Boolean:
                        // there will be only a single value for a bool. is it the same or different?
                        // if different, remove the old value, add the new one
                        // observation: removing and adding SVA values is costlier than just updating a row. it also results in increased primary key usage, i.e. constantly generating new values
                        // todo: consider having the ability to update values instead of replacing.
                        var csoBooleanAttributeValue = connectedSystemObject.AttributeValues.SingleOrDefault(av => (av.AttributeId != 0 ? av.AttributeId : av.Attribute?.Id) == csoAttribute.Id);
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
                var attributeToDelete = connectedSystemObjectType.Attributes.Single(a => a.Name.Equals(csoAttributeName, StringComparison.OrdinalIgnoreCase));
                var attributeValuesToDelete = connectedSystemObject.AttributeValues.Where(q => (q.AttributeId != 0 ? q.AttributeId : q.Attribute?.Id) == attributeToDelete.Id).ToList();
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
                case AttributeDataType.LongNumber:
                    if (long.TryParse(referenceAttributeValue.UnresolvedReferenceValue, out var longUnresolvedReferenceValue))
                        referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.ExternalIdAttributeValue != null && cso.ExternalIdAttributeValue.LongValue == longUnresolvedReferenceValue) ??
                                                          connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.ExternalIdAttributeValue != null && cso.ExternalIdAttributeValue.LongValue == longUnresolvedReferenceValue);
                    else
                        throw new InvalidCastException(
                            $"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} with value '{referenceAttributeValue.UnresolvedReferenceValue}' cannot be parsed to a long.");
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
                case AttributeDataType.LongNumber:
                    if (long.TryParse(referenceAttributeValue.UnresolvedReferenceValue, out var longUnresolvedReferenceValue2))
                        referencedConnectedSystemObject = connectedSystemObjectsToBeCreated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.LongValue == longUnresolvedReferenceValue2) ??
                                                          connectedSystemObjectsToBeUpdated.SingleOrDefault(cso => cso.SecondaryExternalIdAttributeValue != null && cso.SecondaryExternalIdAttributeValue.LongValue == longUnresolvedReferenceValue2);
                    else
                        throw new InvalidCastException($"Attribute '{externalIdAttribute.Name}' of type {externalIdAttribute.Type} with value '{referenceAttributeValue.UnresolvedReferenceValue}' cannot be parsed to a long.");
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
            var activityRunProfileExecutionItem = _activityRunProfileExecutionItems.SingleOrDefault(q => q.ConnectedSystemObject == csoToProcess);
            if (activityRunProfileExecutionItem != null && (activityRunProfileExecutionItem.ErrorType == null || (activityRunProfileExecutionItem.ErrorType == null && activityRunProfileExecutionItem.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet)))
            {
                activityRunProfileExecutionItem.ErrorMessage = $"Couldn't resolve a reference to a Connected System Object: {referenceAttributeValue.UnresolvedReferenceValue} (there may be more, view the Connected System Object for unresolved references). Make sure that Container Scope for the Connected System includes the location of the referenced object.";
                activityRunProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnresolvedReference;
            }
            else
            {
                // CSO may not have been persisted yet or ActivityRunProfileExecutionItem wasn't created
                // Log a warning but don't throw - this can happen with references to objects outside container scope
                Log.Warning($"ResolveReferencesAsync: Couldn't find an ActivityRunProfileExecutionItem for cso: {csoToProcess.Id}, unresolved reference: {referenceAttributeValue.UnresolvedReferenceValue}");
            }

            Log.Debug($"ResolveReferencesAsync: Couldn't resolve a CSO reference: {referenceAttributeValue.UnresolvedReferenceValue}");
        }
    }

    /// <summary>
    /// Reconciles Pending Exports against imported CSO values.
    /// This is the "confirming import" step that validates exported attribute changes were persisted in the connected system.
    /// Creates ActivityRunProfileExecutionItems with warnings for unconfirmed or failed exports.
    /// Uses batched database operations for better performance, processing CSOs in pages using the sync page size setting.
    /// </summary>
    /// <param name="updatedCsos">The CSOs that were updated during this import run.</param>
    private async Task ReconcilePendingExportsAsync(IReadOnlyCollection<ConnectedSystemObject> updatedCsos)
    {
        if (updatedCsos.Count == 0)
            return;

        var reconciliationService = new PendingExportReconciliationService(_jim);
        var totalConfirmed = 0;
        var totalRetry = 0;
        var totalFailed = 0;
        var exportsDeleted = 0;

        // Use sync page size for consistent batching across all sync operations
        var pageSize = await _jim.ServiceSettings.GetSyncPageSizeAsync();
        var csoList = updatedCsos.ToList();
        var totalPages = (int)Math.Ceiling((double)csoList.Count / pageSize);

        Log.Debug("ReconcilePendingExportsAsync: Processing {CsoCount} CSOs in {PageCount} pages of {PageSize}",
            csoList.Count, totalPages, pageSize);

        var processedCount = 0;

        for (var page = 0; page < totalPages; page++)
        {
            var pageCsos = csoList.Skip(page * pageSize).Take(pageSize).ToList();

            // Batch collections for deferred database operations (per page)
            var pendingExportsToDelete = new List<JIM.Models.Transactional.PendingExport>();
            var pendingExportsToUpdate = new List<JIM.Models.Transactional.PendingExport>();

            // Bulk fetch pending exports for this page's CSOs in a single query
            Dictionary<Guid, JIM.Models.Transactional.PendingExport> pendingExportsByCsoId;
            using (Diagnostics.Sync.StartSpan("LoadPendingExports").SetTag("csoCount", pageCsos.Count))
            {
                pendingExportsByCsoId = await _jim.Repository.ConnectedSystems
                    .GetPendingExportsByConnectedSystemObjectIdsAsync(pageCsos.Select(c => c.Id));
            }

            Log.Verbose("ReconcilePendingExportsAsync: Page {Page}/{TotalPages}: Loaded {PendingExportCount} pending exports for {CsoCount} CSOs",
                page + 1, totalPages, pendingExportsByCsoId.Count, pageCsos.Count);

            // Process each CSO in this page against its pre-loaded pending export
            using (Diagnostics.Sync.StartSpan("ProcessReconciliation").SetTag("csoCount", pageCsos.Count))
            {
                foreach (var cso in pageCsos)
                {
                    processedCount++;
                    _activity.ObjectsProcessed = processedCount;

                    try
                    {
                        // Get the pre-loaded pending export for this CSO (if any)
                        pendingExportsByCsoId.TryGetValue(cso.Id, out var pendingExport);

                        // Perform in-memory reconciliation (no database operations)
                        var result = new PendingExportReconciliationResult();
                        reconciliationService.ReconcileCsoAgainstPendingExport(cso, pendingExport, result);

                        if (result.HasChanges)
                        {
                            totalConfirmed += result.ConfirmedChanges.Count;
                            totalRetry += result.RetryChanges.Count;
                            totalFailed += result.FailedChanges.Count;

                            // Collect pending exports for batch operations
                            if (result.PendingExportToDelete != null)
                            {
                                pendingExportsToDelete.Add(result.PendingExportToDelete);
                                exportsDeleted++;
                            }
                            else if (result.PendingExportToUpdate != null)
                            {
                                pendingExportsToUpdate.Add(result.PendingExportToUpdate);
                            }

                            // Create execution items for failed exports (permanent failures)
                            if (result.FailedChanges.Count > 0)
                            {
                                var failedAttrNames = string.Join(", ", result.FailedChanges.Select(c => c.Attribute?.Name ?? "unknown"));
                                var executionItem = new ActivityRunProfileExecutionItem
                                {
                                    Activity = _activity,
                                    ConnectedSystemObject = cso,
                                    ConnectedSystemObjectId = cso.Id,
                                    ExternalIdSnapshot = cso.ExternalIdAttributeValue?.StringValue,
                                    ObjectChangeType = ObjectChangeType.Updated,
                                    ErrorType = ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed,
                                    ErrorMessage = $"Export confirmation failed after maximum retries for {result.FailedChanges.Count} attribute(s): {failedAttrNames}. Manual intervention may be required.",
                                    DataSnapshot = $"Failed attributes: {failedAttrNames}"
                                };
                                _activityRunProfileExecutionItems.Add(executionItem);
                            }

                            // Create execution items for retry exports (temporary failures that will be retried)
                            if (result.RetryChanges.Count > 0)
                            {
                                var retryAttrNames = string.Join(", ", result.RetryChanges.Select(c => c.Attribute?.Name ?? "unknown"));
                                var executionItem = new ActivityRunProfileExecutionItem
                                {
                                    Activity = _activity,
                                    ConnectedSystemObject = cso,
                                    ConnectedSystemObjectId = cso.Id,
                                    ExternalIdSnapshot = cso.ExternalIdAttributeValue?.StringValue,
                                    ObjectChangeType = ObjectChangeType.Updated,
                                    ErrorType = ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed,
                                    ErrorMessage = $"Export not confirmed for {result.RetryChanges.Count} attribute(s): {retryAttrNames}. Will retry on next export run.",
                                    DataSnapshot = $"Unconfirmed attributes: {retryAttrNames}"
                                };
                                _activityRunProfileExecutionItems.Add(executionItem);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "ReconcilePendingExportsAsync: Error reconciling pending exports for CSO {CsoId}", cso.Id);
                    }
                }
            }

            // Batch persist pending export changes for this page
            using (Diagnostics.Sync.StartSpan("FlushPendingExportChanges")
                .SetTag("deleteCount", pendingExportsToDelete.Count)
                .SetTag("updateCount", pendingExportsToUpdate.Count))
            {
                if (pendingExportsToDelete.Count > 0)
                {
                    await _jim.Repository.ConnectedSystems.DeletePendingExportsAsync(pendingExportsToDelete);
                    Log.Verbose("ReconcilePendingExportsAsync: Page {Page}: Batch deleted {Count} confirmed pending exports", page + 1, pendingExportsToDelete.Count);
                }

                if (pendingExportsToUpdate.Count > 0)
                {
                    await _jim.Repository.ConnectedSystems.UpdatePendingExportsAsync(pendingExportsToUpdate);
                    Log.Verbose("ReconcilePendingExportsAsync: Page {Page}: Batch updated {Count} pending exports", page + 1, pendingExportsToUpdate.Count);
                }
            }

            // Update activity progress after each page
            await _jim.Activities.UpdateActivityAsync(_activity);
        }

        if (totalConfirmed > 0 || totalRetry > 0 || totalFailed > 0)
        {
            Log.Information("ReconcilePendingExportsAsync: Reconciliation complete. Confirmed: {Confirmed}, Retry: {Retry}, Failed: {Failed}, Exports deleted: {Deleted}",
                totalConfirmed, totalRetry, totalFailed, exportsDeleted);
        }
    }

    /// <summary>
    /// Updates the connected system with the appropriate initiator (MetaverseObject or ApiKey).
    /// </summary>
    private async Task UpdateConnectedSystemWithInitiatorAsync()
    {
        if (_initiatedByType == ActivityInitiatorType.ApiKey && _initiatedByApiKey != null)
        {
            await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem, _initiatedByApiKey, _activity);
        }
        else
        {
            await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem, _initiatedByMetaverseObject, _activity);
        }
    }
}
