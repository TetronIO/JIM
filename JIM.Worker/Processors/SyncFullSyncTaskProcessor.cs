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
        
        // exports: todo
        
        // imports: confirm pending exports
        // imports: establish new joins to existing Metaverse Objects
        // imports: project CSO to the MV if there are no join matches and if a Sync Rule for this CS has Projection enabled.
        // imports: work out if we CAN update any Metaverse Objects (where there's attribute flow) and whether we SHOULD (where there's attribute flow priority).
        // imports: update the Metaverse Objects accordingly.
        // imports: work out if this requires other Connected System to be updated by way of creating new Pending Export Objects.
        
        // process CSOs in batches. this enables us to respond to cancellation requests in a reasonable time-frame
        // and to update the Activity as we go, allowing the UI to be updated and users kept informed.
        
        

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