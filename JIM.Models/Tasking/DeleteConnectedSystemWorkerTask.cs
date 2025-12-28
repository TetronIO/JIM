using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
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

    public DeleteConnectedSystemWorkerTask()
    {
        // For use by EntityFramework to construct db-sourced objects.
    }

    public DeleteConnectedSystemWorkerTask(int connectedSystemId, bool evaluateMvoDeletionRules = false)
    {
        ConnectedSystemId = connectedSystemId;
        EvaluateMvoDeletionRules = evaluateMvoDeletionRules;
    }

    /// <summary>
    /// When deletion is triggered by a user, this overload should be used to attribute the action to the user.
    /// </summary>
    public DeleteConnectedSystemWorkerTask(int connectedSystemId, MetaverseObject initiatedBy, bool evaluateMvoDeletionRules = false)
    {
        ConnectedSystemId = connectedSystemId;
        EvaluateMvoDeletionRules = evaluateMvoDeletionRules;
        InitiatedByType = ActivityInitiatorType.User;
        InitiatedById = initiatedBy.Id;
        InitiatedByMetaverseObject = initiatedBy;
        InitiatedByName = initiatedBy.DisplayName;
    }

    /// <summary>
    /// When deletion is triggered by an API key, this overload should be used to attribute the action to the API key.
    /// </summary>
    public DeleteConnectedSystemWorkerTask(int connectedSystemId, ApiKey apiKey, bool evaluateMvoDeletionRules = false)
    {
        ConnectedSystemId = connectedSystemId;
        EvaluateMvoDeletionRules = evaluateMvoDeletionRules;
        InitiatedByType = ActivityInitiatorType.ApiKey;
        InitiatedById = apiKey.Id;
        InitiatedByApiKey = apiKey;
        InitiatedByName = apiKey.Name;
    }
}
