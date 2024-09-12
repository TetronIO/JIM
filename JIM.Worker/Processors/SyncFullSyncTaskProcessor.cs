using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Processors;

public class SyncFullSyncTaskProcessor
{
    private readonly JimApplication _jim;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private List<ConnectedSystemObjectType>? _objectTypes;
    
    public SyncFullSyncTaskProcessor(
        JimApplication jimApplication,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        Activity activity,
        CancellationTokenSource cancellationTokenSource)
    {
        _jim = jimApplication;
        _connectedSystem = connectedSystem;
        _connectedSystemRunProfile = connectedSystemRunProfile;
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
        
        // how many objects are we processing? that = CSO count + Pending Export Object count.
        // update the activity with this info so a progress bar can be shown.
        var totalCsosToProcess = await _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalPendingExportObjectsToProcess = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        _activity.ObjectsToProcess = totalObjectsToProcess;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityAsync(_activity);
        
        // get all the active sync rules for this system
        var activeSyncRules = await _jim.ConnectedSystems.GetSyncRulesAsync(_connectedSystem.Id, false);
        
        // get the schema for all object types upfront in this Connected System, so we can retrieve lightweight CSOs without this data.
        _objectTypes = await _jim.ConnectedSystems.GetObjectTypesAsync(_connectedSystem.Id);
        
        // process CSOs in batches. this enables us to respond to cancellation requests in a reasonable timeframe.
        // it also enables us to update the Activity with progress info as we go, allowing the UI to be updated and keep users informed.
        const int pageSize = 200;
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");
        for (var i = 0; i < totalCsoPages; i++)
        {
            var csoPagedResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsAsync(_connectedSystem.Id, i, pageSize, returnAttributes: false);
            foreach (var connectedSystemObject in csoPagedResult.Results)
            {
                // check for cancellation request, and stop work if cancelled.
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    Log.Information("PerformFullSyncAsync: Cancellation requested. Stopping CSO enumeration.");
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
                
                await ProcessConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);
                
                _activity.ObjectsProcessed++;
                await _jim.Activities.UpdateActivityAsync(_activity);
            }
        }
        
        // all objects processed.
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Resolving references");
        await ResolveReferencesAsync();
    }

    /// <summary>
    /// Attempts to join/project/delete/flow attributes to the Metaverse for a single Connected System Object.
    /// </summary>
    private async Task ProcessConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessConnectedSystemObjectAsync: Performing a full sync on Connected System Object: {connectedSystemObject}.");
        
        // we'll track all results to MVO and CSOs, both good and bad, using an Activity Run Profile Execution Item. 
        var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
        
        try
        {
            await ProcessPendingExportAsync(connectedSystemObject, runProfileExecutionItem);
            await ProcessObsoleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
            
            // if the CSO isn't marked as obsolete (it might just have been), look to see if we need to make any related Metaverse Object changes.
            // this requires that we have sync rules defined.
            if (activeSyncRules.Count > 0 && connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
                await ProcessMetaverseObjectChangesAsync(activeSyncRules, connectedSystemObject, runProfileExecutionItem);
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
            return;
        }
        
        // todo: joiner, determine Metaverse and onward CSO impact.
        throw new NotImplementedException("Deleting joined CSOs is not yet supported.");
    }

    /// <summary>
    /// Checks if the not-Obsolete CSO is joined to a Metaverse Object and updates it per any sync rules,
    /// or checks to see if a Metaverse Object needs creating (projecting the CSO) according to any sync rules.
    /// Changes to Metaverse Objects can have downstream impacts on other Connected System objects.
    /// </summary>
    private async Task ProcessMetaverseObjectChangesAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        Log.Verbose($"ProcessMetaverseObjectChangesAsync: Executing for: {connectedSystemObject}.");
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
            return;
        
        if (activeSyncRules.Count == 0)
            return;
        
        // do we need to join, or project the CSO to the Metaverse?
        if (connectedSystemObject.MetaverseObject == null)
        {
            // CSO is not joined to a Metaverse Object.
            // inspect sync rules to determine if we have any join or projection requirements.
            // try to join first, then project. the aim is to ensure we don't end up with duplicate Identities in the Metaverse.
            await AttemptJoinAsync(activeSyncRules, connectedSystemObject, runProfileExecutionItem);
            
            // did we encounter an error whilst attempting a join? stop processing the CSO if so.
            if (runProfileExecutionItem.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                return;

            // were we able to join to an existing MVO?
            if (connectedSystemObject.MetaverseObject == null)
            {
                // try and project the CSO to the Metaverse.
                // this may cause onward sync operations, so may take time.
                AttemptProjection(activeSyncRules, connectedSystemObject);
            }
        }

        // are we joined yet?
        if (connectedSystemObject.MetaverseObject != null)
        {
            // process sync rules to see if we need to flow any attribute updates from the CSO to the MVO.
            foreach (var inboundSyncRule in activeSyncRules.Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectType.Id == connectedSystemObject.Type.Id))
            {
                // evaluate inbound attribute flow rules
                ProcessInboundAttributeFlow(connectedSystemObject, inboundSyncRule);
            }
            
            // have we created a new MVO that needs persisting?
            if (connectedSystemObject.MetaverseObject.Id == Guid.Empty)
                await _jim.Metaverse.CreateMetaverseObjectAsync(connectedSystemObject.MetaverseObject);
            
            // should we persist MVO changes before moving on to onwards CSO updates?
            // is there value in working out if we need to persist the MVO? i.e. is there a performance hit in letting EF work it out for every MVO?
            // todo: process onward-CSO updates
        }
    }

    /// <summary>
    /// Attempts to find a Metaverse Object that matches the CSO using Object Matching Rules on any applicable Sync Rules for this system and object type.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain all possible join rules to be evaluated.</param>
    /// <param name="connectedSystemObject">The Connected System Object to try and find a matching Metaverse Object for.</param>
    /// <param name="runProfileExecutionItem">The Run Profile Execution Item we're tracking changes/errors against for the CSO.</param>
    /// <returns>True if a join was established, otherwise False.</returns>
    /// <exception cref="InvalidDataException">Will be thrown if an unsupported join state is found preventing processing.</exception>
    private async Task AttemptJoinAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        // enumerate all sync rules that have matching rules. first to match wins. 
        // for more deterministic results, admins should make sure an object only matches to a single sync rule
        // at any given moment to ensure the single sync rule matching rule priority order is law.
        foreach (var matchingSyncRule in activeSyncRules.Where(sr => sr.ObjectMatchingRules.Count > 0 && sr.ConnectedSystemObjectType.Id == connectedSystemObject.Type.Id))
        {
            // object matching rules are ordered. respect the ordering. 
            foreach (var matchingRule in matchingSyncRule.ObjectMatchingRules.OrderBy(q => q.Order))
            {
                // use this rule to see if we have a matching MVO to join with.
                var mvo = await _jim.Metaverse.FindMetaverseObjectUsingMatchingRuleAsync(connectedSystemObject, matchingSyncRule.MetaverseObjectType, matchingRule);
                if (mvo == null) 
                    continue;
                
                // mvo must not already be joined to a connected system object in this connected system. joins are 1:1.
                var existingCsoJoins = mvo.ConnectedSystemObjects.Where(q => q.ConnectedSystemId == _connectedSystem.Id).ToList();

                if (existingCsoJoins.Count > 1)
                    throw new InvalidDataException($"More than one CSO is already joined to the MVO {mvo} we found that matches the matching rules. This is not good!");
                    
                if (existingCsoJoins.Count == 1)
                {
                    runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.CouldNotJoinDueToExistingJoin;
                    runProfileExecutionItem.ErrorMessage = $"Would have joined this Connector Space Object to a Metaverse Object ({mvo}), but that already has a join to CSO " +
                                                           $"{existingCsoJoins[0]}. Check the attributes on this object are not duplicated, and/or check  your " +
                                                           $"Object Matching Rules for uniqueness.";
                    return;
                }
                    
                // establish join! then return as first rule to match, wins.
                connectedSystemObject.MetaverseObject = mvo;
                connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Joined;
                return;
            }
            
            // have we joined yet? stop enumerating if so.
            if (connectedSystemObject.MetaverseObject != null)
                return;
        }

        // no join could be established.
    }

    /// <summary>
    /// Attempts to create a Metaverse Object from the Connected System Object using the first Sync Rule for the object type that has Projection enabled.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain projection and attribute flow information.</param>
    /// <param name="connectedSystemObject">The Connected System Object to attempt to project to the Metaverse.</param>
    /// <exception cref="InvalidDataException">Will be thrown if not all required properties are populated on the Sync Rule.</exception>
    /// <exception cref="NotImplementedException">Will be thrown if a Sync Rule attempts to use a Function as a source.</exception>
    private static void AttemptProjection(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        // see if there are any sync rules for this object type where projection is enabled. first to project, wins.
        var projectionSyncRule = activeSyncRules?.FirstOrDefault(sr =>
            sr.ProjectToMetaverse.HasValue && sr.ProjectToMetaverse.Value &&
            sr.ConnectedSystemObjectType.Id == connectedSystemObject.TypeId);

        if (projectionSyncRule == null) 
            return;
        
        // create the MVO using type from the Sync Rule.
        var mvo = new MetaverseObject();
        mvo.ConnectedSystemObjects.Add(connectedSystemObject);
        mvo.Type = projectionSyncRule.MetaverseObjectType;
        connectedSystemObject.MetaverseObject = mvo;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Projected;
        
        // do not flow attributes at this point. let that happen separately, so we don't re-process sync rules later.
    }

    /// <summary>
    /// Assigns values to a Metaverse Object, from a Connected System Object using a Sync Rule.
    /// Does not perform any delta processing. This is for MVO create scenarios where there are not MVO attribute values already.
    /// </summary>
    /// <param name="connectedSystemObject">The source Connected System Object to map values from.</param>
    /// <param name="syncRule">The Sync Rule to use to determine which attributes, and how should be assigned to the Metaverse Object.</param>
    /// <exception cref="InvalidDataException">Can be thrown if a Sync Rule Mapping Source is not properly formed.</exception>
    /// <exception cref="NotImplementedException">Will be thrown whilst Functions have not been implemented, but are being used in the Sync Rule.</exception>
    private void ProcessInboundAttributeFlow(ConnectedSystemObject connectedSystemObject, SyncRule syncRule)
    {
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Error($"AssignMetaverseObjectAttributeValues: CSO ({connectedSystemObject}) has no MVO!");
            return;
        }

        if (_objectTypes == null)
            throw new MissingMemberException("_objectTypes is null!");
        
        foreach (var syncRuleMapping in syncRule.AttributeFlowRules.OrderBy(q => q.Order))
        {
            if (syncRuleMapping.TargetMetaverseAttribute == null)
                throw new InvalidDataException("SyncRuleMapping.TargetMetaverseAttribute must not be null.");
            
            SyncRuleMappingProcessor.Process(connectedSystemObject, syncRuleMapping, _objectTypes);
        }
    }

    /// <summary>
    /// Builds a Metaverse Object Attribute Value using values from a Connected System Object Attribute Value and assigns it to a Metaverse Object.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to add the Attribute Value to.</param>
    /// <param name="metaverseAttribute">The Metaverse Attribute the Attribute Value will be for.</param>
    /// <param name="connectedSystemObjectAttributeValue">The source for the values on the Metaverse Object Attribute Value.</param>
    private void SetMetaverseObjectAttributeValue(
        MetaverseObject metaverseObject, MetaverseAttribute metaverseAttribute, ConnectedSystemObjectAttributeValue connectedSystemObjectAttributeValue)
    {    
        // TODO: review for evolution to handle update/delete scenarios
        
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

    /// <summary>
    /// As part of updating or creating reference Metaverse Attribute Values from Connected System Object Attribute Values, references would have been staged
    /// as unresolved, pointing to the Connected System Object. This converts those CSO unresolved references to MVO references. 
    /// </summary>
    private async Task ResolveReferencesAsync()
    {
        // find all Metaverse Attribute Values with unresolved reference values
        // get the joined Metaverse Object and add it to the Metaverse Object Attribute Value
        // remove the unresolved reference value.
        // update the Metaverse Object Attribute Value.

        throw new NotImplementedException("Is this still needed? We're assigning MVO references from CSO reference values on sync rule processing.");
    }
}