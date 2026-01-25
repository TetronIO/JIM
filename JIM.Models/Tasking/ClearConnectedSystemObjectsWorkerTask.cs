using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
namespace JIM.Models.Tasking;

public class ClearConnectedSystemObjectsWorkerTask : WorkerTask
{
    /// <summary>
    /// The id for the connected system the run profile relates to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// Whether to delete change history for the cleared CSOs.
    /// Default: true (recommended for re-import scenarios).
    /// </summary>
    public bool DeleteChangeHistory { get; set; } = true;

    public ClearConnectedSystemObjectsWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    public ClearConnectedSystemObjectsWorkerTask(int connectedSystemId, bool deleteChangeHistory = true)
    {
        ConnectedSystemId = connectedSystemId;
        DeleteChangeHistory = deleteChangeHistory;
    }

    /// <summary>
    /// When a clear connected system objects task is triggered by a user, this overload should be used to attribute the action to the user.
    /// </summary>
    public ClearConnectedSystemObjectsWorkerTask(int connectedSystemId, MetaverseObject initiatedBy, bool deleteChangeHistory = true)
    {
        ConnectedSystemId = connectedSystemId;
        DeleteChangeHistory = deleteChangeHistory;
        InitiatedByType = ActivityInitiatorType.User;
        InitiatedById = initiatedBy.Id;
        InitiatedByMetaverseObject = initiatedBy;
        InitiatedByName = initiatedBy.DisplayName;
    }

    /// <summary>
    /// When a clear connected system objects task is triggered by an API key, this overload should be used to attribute the action to the API key.
    /// </summary>
    public ClearConnectedSystemObjectsWorkerTask(int connectedSystemId, ApiKey apiKey, bool deleteChangeHistory = true)
    {
        ConnectedSystemId = connectedSystemId;
        DeleteChangeHistory = deleteChangeHistory;
        InitiatedByType = ActivityInitiatorType.ApiKey;
        InitiatedById = apiKey.Id;
        InitiatedByApiKey = apiKey;
        InitiatedByName = apiKey.Name;
    }
}