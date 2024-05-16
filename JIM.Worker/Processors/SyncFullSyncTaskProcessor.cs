using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Interfaces;
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
    private readonly JIM.Models.Activities.Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private List<ConnectedSystemObjectType>? _objectTypes;
    
    public SyncFullSyncTaskProcessor(
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

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Preparing...");
        
        // how many objects are we processing? that's CSO count + Pending Export Object count.
        var totalCsosToProcess = await _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalPendingExportObjectsToProcess = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        
        // get the schema for all object types upfront in this Connected System, so we can retrieve lightweight CSOs without this data.
        _objectTypes = await _jim.ConnectedSystems.GetObjectTypesAsync(_connectedSystem.Id);
        
        // process CSOs in batches. this enables us to respond to cancellation requests in a reasonable time-frame
        // and to update the Activity as we go, allowing the UI to be updated and users kept informed.

        const int pageSize = 200;
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects...");
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
            }
        }
    }

    private async Task ProcessConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessConnectedSystemObjectAsync: Performing a full sync on Connected System Object: {connectedSystemObject}.");
        
        await ProcessPendingExportAsync(connectedSystemObject);
        await ProcessObsoleteConnectedSystemObjectAsync(connectedSystemObject);
        await ProcessMetaverseObjectChangesAsync(connectedSystemObject);
    }

    /// <summary>
    /// See if a Pending Export Object for a Connected System Object can be invalidated and deleted.
    /// This would occur when the Pending Export changes are visible on the Connected System Object after a confirming import.
    /// </summary>
    private async Task ProcessPendingExportAsync(ConnectedSystemObject connectedSystemObject)
    {
        // todo: all of it! skipping for now.
        Log.Verbose($"ProcessPendingExportAsync: Executing for: {connectedSystemObject}.");
    }

    /// <summary>
    /// Check if a CSO has been obsoleted and delete it, applying any joined Metaverse Object changes as necessary.
    /// Deleting a Metaverse Object can have downstream impacts on other Connected System objects.
    /// </summary>
    private async Task ProcessObsoleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
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
        
        //if (connectedSystemObject.JoinType == )
    }

    /// <summary>
    /// Checks if the not-Obsolete CSO is joined to a Metaverse Object and updates it per any sync rules,
    /// or checks to see if a Metaverse Object needs creating (projecting the CSO) according to any sync rules.
    /// Changes to Metaverse Objects can have downstream impacts on other Connected System objects.
    /// </summary>
    private async Task ProcessMetaverseObjectChangesAsync(ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessMetaverseObjectChangesAsync: Executing for: {connectedSystemObject}.");
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
        {
            Log.Warning($"ProcessMetaverseObjectChangesAsync: {connectedSystemObject} is Obsoleted. This method shouldn't have been called.");
            return;
        }
        
        // todo: the rest!
    }
}
