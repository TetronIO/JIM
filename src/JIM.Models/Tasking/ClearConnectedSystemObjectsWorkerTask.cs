using JIM.Models.Activities;
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
    /// Factory method for creating a task triggered by a user.
    /// </summary>
    public static ClearConnectedSystemObjectsWorkerTask ForUser(int connectedSystemId, Guid userId, string userName, bool deleteChangeHistory = true)
    {
        return new ClearConnectedSystemObjectsWorkerTask(connectedSystemId, deleteChangeHistory)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = userId,
            InitiatedByName = userName
        };
    }

    /// <summary>
    /// Factory method for creating a task triggered by an API key.
    /// </summary>
    public static ClearConnectedSystemObjectsWorkerTask ForApiKey(int connectedSystemId, Guid apiKeyId, string apiKeyName, bool deleteChangeHistory = true)
    {
        return new ClearConnectedSystemObjectsWorkerTask(connectedSystemId, deleteChangeHistory)
        {
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedById = apiKeyId,
            InitiatedByName = apiKeyName
        };
    }
}