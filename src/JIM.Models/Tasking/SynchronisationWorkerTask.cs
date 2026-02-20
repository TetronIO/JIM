using JIM.Models.Activities;
namespace JIM.Models.Tasking;

public class SynchronisationWorkerTask : WorkerTask
{
    /// <summary>
    /// The id for the connected system the run profile relates to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The id for the connected system run profile to execute via this task.
    /// </summary>
    public int ConnectedSystemRunProfileId { get; set; }

    public SynchronisationWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    public SynchronisationWorkerTask(int connectedSystemId, int connectedSystemRunProfileId)
    {
        ConnectedSystemId = connectedSystemId;
        ConnectedSystemRunProfileId = connectedSystemRunProfileId;
    }

    /// <summary>
    /// When a synchronisation service task is triggered by a user, this overload should be used to attribute the action to the user.
    /// </summary>
    public SynchronisationWorkerTask(int connectedSystemId, int connectedSystemRunProfileId, Guid initiatedById, string initiatedByName)
    {
        ConnectedSystemId = connectedSystemId;
        ConnectedSystemRunProfileId = connectedSystemRunProfileId;
        InitiatedByType = ActivityInitiatorType.User;
        InitiatedById = initiatedById;
        InitiatedByName = initiatedByName;
    }

    /// <summary>
    /// Factory method for creating a task triggered by a user.
    /// </summary>
    public static SynchronisationWorkerTask ForUser(int connectedSystemId, int connectedSystemRunProfileId, Guid userId, string userName)
    {
        return new SynchronisationWorkerTask(connectedSystemId, connectedSystemRunProfileId, userId, userName);
    }

    /// <summary>
    /// Factory method for creating a task triggered by an API key.
    /// </summary>
    public static SynchronisationWorkerTask ForApiKey(int connectedSystemId, int connectedSystemRunProfileId, Guid apiKeyId, string apiKeyName)
    {
        return new SynchronisationWorkerTask(connectedSystemId, connectedSystemRunProfileId)
        {
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedById = apiKeyId,
            InitiatedByName = apiKeyName
        };
    }
}