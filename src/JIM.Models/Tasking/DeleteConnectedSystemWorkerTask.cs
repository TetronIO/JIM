using JIM.Models.Activities;
namespace JIM.Models.Tasking;

/// <summary>
/// Worker task for deleting a Connected System and all its related data.
/// This task is queued when a sync operation is running at the time deletion is requested,
/// allowing the sync to complete before deletion proceeds.
/// </summary>
public class DeleteConnectedSystemWorkerTask : WorkerTask
{
    /// <summary>
    /// The id for the Connected System to delete.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// Whether to evaluate MVO deletion rules after disconnecting CSOs.
    /// If true, MVOs with WhenLastConnectorDisconnected rule may be deleted.
    /// </summary>
    public bool EvaluateMvoDeletionRules { get; set; }

    /// <summary>
    /// Whether to delete change history for the deleted CSOs.
    /// Default: false (preserves audit trail).
    /// </summary>
    public bool DeleteChangeHistory { get; set; } = false;

    public DeleteConnectedSystemWorkerTask()
    {
        // For use by EntityFramework to construct db-sourced objects.
    }

    public DeleteConnectedSystemWorkerTask(int connectedSystemId, bool evaluateMvoDeletionRules = false, bool deleteChangeHistory = false)
    {
        ConnectedSystemId = connectedSystemId;
        EvaluateMvoDeletionRules = evaluateMvoDeletionRules;
        DeleteChangeHistory = deleteChangeHistory;
    }

    /// <summary>
    /// Factory method for creating a task triggered by a user.
    /// </summary>
    public static DeleteConnectedSystemWorkerTask ForUser(int connectedSystemId, Guid userId, string userName, bool evaluateMvoDeletionRules = false, bool deleteChangeHistory = false)
    {
        return new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules, deleteChangeHistory)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = userId,
            InitiatedByName = userName
        };
    }

    /// <summary>
    /// Factory method for creating a task triggered by an API key.
    /// </summary>
    public static DeleteConnectedSystemWorkerTask ForApiKey(int connectedSystemId, Guid apiKeyId, string apiKeyName, bool evaluateMvoDeletionRules = false, bool deleteChangeHistory = false)
    {
        return new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules, deleteChangeHistory)
        {
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedById = apiKeyId,
            InitiatedByName = apiKeyName
        };
    }
}
