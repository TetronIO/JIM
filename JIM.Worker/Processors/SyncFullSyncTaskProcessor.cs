using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Processors;

public class SyncFullSyncTaskProcessor
{
    private readonly JimApplication _jim;
    private readonly IConnector _connector;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly MetaverseObject _initiatedBy;
    private readonly Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private List<ConnectedSystemObjectType>? _objectTypes;
    private List<SyncRule>? _syncRules;
    private bool _haveSyncRules;
    
    public SyncFullSyncTaskProcessor(
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

    public async Task PerformFullSyncAsync()
    {
        Log.Verbose("PerformFullSyncAsync: Starting");
        
        // what needs to happen:
        // - confirm pending exports
        // - establish new joins to existing Metaverse Objects
        // - project CSO to the MV if there are no join matches and if a Sync Rule for this CS has Projection enabled.
        // - work out if we CAN update any Metaverse Objects (where there's attribute flow) and whether we SHOULD (where there's attribute flow priority).
        // - update the Metaverse Objects accordingly.
        // - work out if this requires other Connected System to be updated by way of creating new Pending Export Objects.

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Preparing");
        
        // how many objects are we processing? that's CSO count + Pending Export Object count.
        // update the activity with this info so a progress bar can be shown.
        var totalCsosToProcess = await _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalPendingExportObjectsToProcess = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        _activity.ObjectsToProcess = totalObjectsToProcess;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityAsync(_activity);
        
        // get all the active sync rules for this system
        _syncRules = await _jim.ConnectedSystems.GetSyncRulesAsync(_connectedSystem.Id, false);
        _haveSyncRules = _syncRules is { Count: > 0 };
        
        // get the schema for all object types upfront in this Connected System, so we can retrieve lightweight CSOs without this data.
        _objectTypes = await _jim.ConnectedSystems.GetObjectTypesAsync(_connectedSystem.Id);
        
        // process CSOs in batches. this enables us to respond to cancellation requests in a reasonable time-frame
        // and to update the Activity as we go, allowing the UI to be updated and users kept informed.

        const int pageSize = 200;
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");
        for (var i = 0; i < totalCsoPages; i++)
        {
            var csoPagedResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsAsync(_connectedSystem.Id, i, pageSize);
            foreach (var connectedSystemObject in csoPagedResult.Results)
            {
                // check for cancellation request, and stop work if cancelled.
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    Log.Information("PerformFullSyncAsync: O1 Cancellation requested. Stopping.");
                    return;
                }
                
                // what kind of result do we want? we want to see:
                // - mvo joins (list)
                // - mvo projections (list)
                // - cso deletions (list)
                // - mvo deletions (list)
                // - mvo updates (list)
                // - mvo objects not updated (count)
                
                // todo: record changes to MV objects and other Connected Systems via Pending Export objects on the Activity Run Profile Execution.
                // todo: work out how a preview would work. we don't want to repeat ourselves unnecessarily (D.R.Y).
                // thinking about creating a preview response object and passing it in below, and if present, then the code stack builds the preview,
                // so the same code stack can be used.
                
                await ProcessConnectedSystemObjectAsync(connectedSystemObject);
                
                _activity.ObjectsProcessed++;
                await _jim.Activities.UpdateActivityAsync(_activity);
            }
        }
    }

    private async Task ProcessConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessConnectedSystemObjectAsync: Performing a full sync on Connected System Object: {connectedSystemObject}.");
        
        // we'll track all results to MVO and CSOs, both good and bad using an Activity Run Profile Execution Item. 
        var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
        
        try
        {
            await ProcessPendingExportAsync(connectedSystemObject, runProfileExecutionItem);
            await ProcessObsoleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
            
            // look for Metaverse Object updates. requires we have sync rules.
            if (_haveSyncRules)
                await ProcessMetaverseObjectChangesAsync(connectedSystemObject, runProfileExecutionItem);
        }
        catch (Exception e)
        {
            // log the unhandled exception to the run profile execution item, so admins can see the error via a client.
            runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
            runProfileExecutionItem.ErrorMessage = e.Message;
            runProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
            
            // still perform system logging.
            Log.Error(e, $"ProcessConnectedSystemObjectAsync: Unhandled {_connectedSystemRunProfile} sync error whilst processing {connectedSystemObject}.");
        }
    }

    /// <summary>
    /// See if a Pending Export Object for a Connected System Object can be invalidated and deleted.
    /// This would occur when the Pending Export changes are visible on the Connected System Object after a confirming import.
    /// </summary>
    private async Task ProcessPendingExportAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        // todo: all of it! skipping for now.
        Log.Verbose($"ProcessPendingExportAsync: Executing for: {connectedSystemObject}.");
    }

    /// <summary>
    /// Check if a CSO has been obsoleted and delete it, applying any joined Metaverse Object changes as necessary.
    /// Deleting a Metaverse Object can have downstream impacts on other Connected System objects.
    /// </summary>
    private async Task ProcessObsoleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        if (connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            return;
        
        Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Executing for: {connectedSystemObject}.");

        // - if not joined, delete the cso
        // - if joined:
        //   - if the metaverse object should be deleted (determined by mv object deletion rules):
        //     - should any other connected system objects be deleted?
        //   - if the metaverse object shouldn't be deleted:
        //     - should any cso-contributed mvo attributes be removed?

        if (connectedSystemObject.MetaverseObject == null)
        {
            // not a joiner, delete the CSO.
            await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
        }
        else
        {
            // todo: joiner, determine Metaverse and onward CSO impact.
            throw new NotImplementedException("Deleting joined CSOs is not yet supported.");
        }
    }

    /// <summary>
    /// Checks if the not-Obsolete CSO is joined to a Metaverse Object and updates it per any sync rules,
    /// or checks to see if a Metaverse Object needs creating (projecting the CSO) according to any sync rules.
    /// Changes to Metaverse Objects can have downstream impacts on other Connected System objects.
    /// </summary>
    private async Task ProcessMetaverseObjectChangesAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        Log.Verbose($"ProcessMetaverseObjectChangesAsync: Executing for: {connectedSystemObject}.");
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
            return;
        
        if (_syncRules == null || _syncRules.Count == 0)
            return;
        
        // do we need to join, or project the CSO to the Metaverse?
        if (connectedSystemObject.MetaverseObject == null)
        {
            // CSO is not joined to a Metaverse Object.
            // inspect sync rules to determine if we have any join or projection requirements.
            // try to join first, then project. the aim is to ensure we don't end up with duplicate Identities in the Metaverse.
            var wasJoinSuccessful = await AttemptJoinAsync(connectedSystemObject, runProfileExecutionItem);
            
            // did we encounter an error whilst attempting a join? stop processing the CSO if so.
            if (runProfileExecutionItem.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                return;

            if (!wasJoinSuccessful)
            {
                // try and project the CSO to the Metaverse
                // this may cause onward sync operations, so may take time.
                await AttemptProjectionAsync(connectedSystemObject, runProfileExecutionItem);
            }
        }
        else
        {
            // CSO is already joined to a Metaverse Object
            // inspect sync rules for any necessary attribute flow updates.
        }
    }

    /// <summary>
    /// Attempts to find a Metaverse Object that matches the CSO using Object Matching Rules on any applicable Sync Rules for this system and object type.
    /// </summary>
    /// <param name="connectedSystemObject">The Connected System Object to try and find a matching Metaverse Object for.</param>
    /// <param name="runProfileExecutionItem">The Run Profile Execution Item we're tracking changes/errors against for the CSO.</param>
    /// <returns>True if a join was established, otherwise False.</returns>
    /// <exception cref="InvalidDataException">Will be thrown if an unsupported join state is found preventing processing.</exception>
    private async Task<bool> AttemptJoinAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        if (_syncRules == null)
            return false;
        
        // enumerate all sync rules that have matching rules. first to match wins. 
        // for more deterministic results, admins should make sure an object only matches to a single sync rule
        // at any given moment to ensure the single sync rule matching rule priority order is law.
        foreach (var matchingSyncRule in _syncRules.Where(sr => sr.ObjectMatchingRules.Count > 0 && sr.ConnectedSystemObjectType.Id == connectedSystemObject.Type.Id))
        {
            // object matching rules are ordered. respect the ordering. 
            foreach (var matchingRule in matchingSyncRule.ObjectMatchingRules.OrderBy(q => q.Order))
            {
                // use this rule to see if we have a matching MVO to join with.
                var mvo = await _jim.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, matchingSyncRule.MetaverseObjectType, matchingRule);
                if (mvo == null) 
                    continue;
                
                // mvo must not already be joined to a connected system object in this connected system. joins are 1:1.
                var existingCsoJoins = mvo.ConnectedSystemObjects
                    .Where(q => q.ConnectedSystemId == _connectedSystem.Id).ToList();

                if (existingCsoJoins.Count > 1)
                    throw new InvalidDataException($"More than one CSO is already joined to the MVO {mvo} we found that matches the matching rules. This is not good!");
                    
                if (existingCsoJoins.Count == 1)
                {
                    runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.CouldNotJoinDueToExistingJoin;
                    runProfileExecutionItem.ErrorMessage = $"Would have joined this Connector Space Object to a Metaverse Object ({mvo}), but that already has a join to CSO {existingCsoJoins[0]}. Check the attributes on this object are not duplicated, and/or check  your Object Matching Rules for uniqueness.";
                    return false;
                }
                    
                // establish join! then return as first rule to match, wins.
                connectedSystemObject.MetaverseObject = mvo;
                connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Joined;
                return true;
            }
            
            // have we joined yet? stop enumerating if so.
            if (connectedSystemObject.MetaverseObject != null)
                return true;
        }

        // no join could be established.
        return false;
    }

    /// <summary>
    /// Attempts to create a Metaverse Object from the Connected System Object using the first Sync Rule for the object type that has Projection enabled.
    /// </summary>
    /// <param name="connectedSystemObject">The Connected System Object to attempt to project to the Metaverse.</param>
    /// <param name="runProfileExecutionItem">The Run Profile Execution Item we're tracking changes/errors against for the CSO.</param>
    /// <exception cref="InvalidDataException">Will be thrown if not all required properties are populated on the Sync Rule.</exception>
    /// <exception cref="NotImplementedException">Will be thrown if a Sync Rule attempts to use a Function as a source.</exception>
    private async Task AttemptProjectionAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        // see if there are any sync rules for this object type where projection is enabled.
        // note: first to project wins, so it probably makes sense to just have a single sync rule for each object type
        // with projection enabled to keep things easy to understand.
        var projectionSyncRule = _syncRules?.FirstOrDefault(sr =>
            sr.ProjectToMetaverse.HasValue && sr.ProjectToMetaverse.Value &&
            sr.ConnectedSystemObjectType.Id == connectedSystemObject.Type.Id);

        if (projectionSyncRule != null)
        {
            // create the MVO using type and attribute flow from this Sync Rule.
            var mvo = new MetaverseObject();
            mvo.ConnectedSystemObjects.Add(connectedSystemObject);
            mvo.Type = projectionSyncRule.MetaverseObjectType;
            connectedSystemObject.MetaverseObject = mvo;
            
            // now build the MVO attributes from the CSO.
            AssignMetaverseObjectAttributeValues(mvo, connectedSystemObject, projectionSyncRule, runProfileExecutionItem);
            
            // persist the mvo
            await _jim.Metaverse.CreateMetaverseObjectAsync(mvo);
        }
    }

    private void AssignMetaverseObjectAttributeValues(MetaverseObject metaverseObject, ConnectedSystemObject connectedSystemObject, SyncRule syncRule, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        foreach (var mapping in syncRule.AttributeFlowRules.OrderBy(q => q.Order))
        {
            if (mapping.TargetMetaverseAttribute == null)
                throw new InvalidDataException("SyncRuleMapping.TargetMetaverseAttribute must not be null.");
            
            foreach (var source in mapping.Sources.OrderBy(q => q.Order))
            {
                if (source.ConnectedSystemAttribute != null)
                {
                    // CSOs and MVOs have slightly different ways of representing multiple-values due to how
                    // they need to be used/populated in their respective scenarios.
                    // CSOs store all values under a single attribute value object.
                    // MVOs store each value under their own attribute value object.
                    // so we need to handle MVAs differently when populating the MVO attribute value.

                    if (source.ConnectedSystemAttribute.AttributePlurality == AttributePlurality.SingleValued)
                    {
                        var csoAttributeValue = connectedSystemObject.GetAttributeValue(source.ConnectedSystemAttribute.Name);
                        if (csoAttributeValue != null)
                            SetMetaverseObjectAttributeValue(metaverseObject, mapping.TargetMetaverseAttribute, csoAttributeValue); 
                        else
                            Log.Verbose($"AttemptProjectionAsync: Skipping CSO SVA {source.ConnectedSystemAttribute.Name} as it has no value.");
                    }
                    else
                    {
                        // multi-valued attribute
                        var csoAttributeValues = connectedSystemObject.GetAttributeValues(source.ConnectedSystemAttribute.Name);
                        foreach (var csoAttributeValue in csoAttributeValues)
                            SetMetaverseObjectAttributeValue(metaverseObject, mapping.TargetMetaverseAttribute, csoAttributeValue); 
                    }
                }
                else if (source.Function != null)
                {
                    throw new NotImplementedException("Functions have not been implemented yet.");
                }
                else if (source.MetaverseAttribute != null)
                {
                    throw new InvalidDataException("SyncRuleMappingSource.MetaverseAttribute being populated is not supported for synchronisation operations. This operation is focused on import flow, so Connected System to Metaverse Object.");
                }
                else
                {
                    throw new InvalidDataException("Expected ConnectedSystemAttribute or Function to be populated in a SyncRuleMappingSource object.");
                }
            }
        }
    }

    /// <summary>
    /// Builds a Metaverse Object Attribute Value using values from a Connected System Object Attribute Value and assigns it to a Metaverse Object.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to add the Attribute Value to.</param>
    /// <param name="metaverseAttribute">The Metaverse Attribute the Attribute Value will be for.</param>
    /// <param name="connectedSystemObjectAttributeValue">The source for the values on the Metaverse Object Attribute Value.</param>
    private void SetMetaverseObjectAttributeValue(MetaverseObject metaverseObject, MetaverseAttribute metaverseAttribute, ConnectedSystemObjectAttributeValue connectedSystemObjectAttributeValue)
    {    
        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = metaverseObject,
            Attribute = metaverseAttribute,
            ContributedBySystem = _connectedSystem,
            StringValue = connectedSystemObjectAttributeValue.StringValue,
            BoolValue = connectedSystemObjectAttributeValue.BoolValue,
            ByteValue = connectedSystemObjectAttributeValue.ByteValue,
            GuidValue = connectedSystemObjectAttributeValue.GuidValue,
            IntValue = connectedSystemObjectAttributeValue.IntValue,
            DateTimeValue = connectedSystemObjectAttributeValue.DateTimeValue,
            UnresolvedReferenceValue = connectedSystemObjectAttributeValue.ConnectedSystemObject,
            UnresolvedReferenceValueId = connectedSystemObjectAttributeValue.ConnectedSystemObject.Id
        });
    }
}
