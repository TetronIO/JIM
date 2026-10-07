// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Activities;
namespace JIM.Models.Tasking;

public class SynchronisationWorkerTask : WorkerTask
{
    /// <summary>
    /// The id for the Connected System the Run Profile relates to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The id for the Connected System Run Profile to execute via this task.
    /// </summary>
    public int ConnectedSystemRunProfileId { get; set; }

    /// <summary>
    /// The Full Synchronisation preview the administrator read before starting this run, if any (#1530). Transient
    /// (never persisted on the task): it is checked and copied onto the run's Activity when the task is created, so the
    /// audit trail records what the person starting the run was told it would do. Null when no preview informed it.
    /// </summary>
    [NotMapped]
    public Guid? PreviewActivityId { get; set; }

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