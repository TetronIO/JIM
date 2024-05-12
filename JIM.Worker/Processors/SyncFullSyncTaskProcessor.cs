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
        
        // how many objects are we processing? that's CSO count + Pending Export Object count.
        var totalCsosToProcess = await _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalPendingExportObjectsToProcess = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        
        // todo: update the Activity with progress info.
        
        // process CSOs in batches. this enables us to respond to cancellation requests in a reasonable time-frame
        // and to update the Activity as we go, allowing the UI to be updated and users kept informed.

        const int pageSize = 200;
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        for (var i = 0; i < totalCsoPages; i++)
        {
            var csoPagedResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsAsync(_connectedSystem.Id, i, pageSize);
            foreach (var connectedSystemObject in csoPagedResult.Results)
            {
                // what kind of result do we want? we want to see:
                // - mvo joins (list)
                // - mvo projections (list)
                // - cso deletions (list)
                // - mvo deletions (list)
                // - mvo updates (list)
                // - mvo objects not updated (count)
                
                // how do we want to record this info?
                // - as properties on the activity?
                // - dynamically generated from run profile execution items (if possible?)
            }
        }
        

        
        
        await ProcessPendingExportsAsync();
        await ProcessObsoleteConnectedSystemObjectsAsync();
        await ProcessMetaverseObjectChangesAsync();

    }

    /// <summary>
    /// See if any Pending Export Objects can be deleted as they're no longer required. This can happen when the 
    /// </summary>
    private async Task ProcessPendingExportsAsync()
    {
        // todo: all of it! skipping for now.
    }

    /// <summary>
    /// Enumerate CSOs marked as Obsolete and delete them, applying any joined Metaverse Object changes as necessary.
    /// </summary>
    private async Task ProcessObsoleteConnectedSystemObjectsAsync()
    {
    }

    /// <summary>
    /// Enumerate CSOs not marked as Obsolete and determine if Metaverse Objects need creating, or updating as a result
    /// of Sync Rules and any existing relationships to Metaverse Objects.
    /// </summary>
    private async Task ProcessMetaverseObjectChangesAsync()
    {
    }
}