// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
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

    /// <summary>
    /// Whether the deletion runs as Synchronised Deprovisioning (#809): each of the system's Connected
    /// System Objects is processed through the synchronisation engine's obsoletion semantics (attribute
    /// recall with surviving-contributor re-election, Metaverse Object deletion-rule evaluation, Pending
    /// Export staging), then a by-provenance residue pass per import Synchronisation Rule, then the
    /// existing deletion as the final step. False keeps today's immediate deletion bit-for-bit.
    /// </summary>
    public bool SynchronisedDeprovisioning { get; set; } = false;

    /// <summary>
    /// Resumability checkpoint (#809): the pass the deprovisioning run last completed a batch in. Null until
    /// the first batch completes; a worker restart resumes from here rather than reprocessing committed
    /// work. Only meaningful when <see cref="SynchronisedDeprovisioning"/> is true.
    /// </summary>
    public SynchronisedDeprovisioningPhase? CheckpointPhase { get; set; }

    /// <summary>
    /// Resumability checkpoint (#809): the last Connected System Object id whose per-object batch was fully
    /// persisted (objects are processed in ascending id order). On resume, the per-object pass skips objects
    /// at or before this id.
    /// </summary>
    public Guid? CheckpointConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Resumability checkpoint (#809): the last import Synchronisation Rule id whose residue recall
    /// completed (rules are processed in ascending id order). On resume, the residue pass skips rules at or
    /// before this id.
    /// </summary>
    public int? CheckpointSyncRuleId { get; set; }

    /// <summary>
    /// Optional reason for the deletion, entered at request time. Transient (never persisted on the task): it is
    /// copied onto the task's delete Activity when the task is created, so it survives to when the worker runs
    /// without needing a column of its own. Null when no reason was supplied.
    /// </summary>
    [NotMapped]
    public string? ChangeReason { get; set; }

    public DeleteConnectedSystemWorkerTask()
    {
        // For use by EntityFramework to construct db-sourced objects.
    }

    public DeleteConnectedSystemWorkerTask(int connectedSystemId, bool evaluateMvoDeletionRules = false, bool deleteChangeHistory = false, bool synchronisedDeprovisioning = false)
    {
        ConnectedSystemId = connectedSystemId;
        EvaluateMvoDeletionRules = evaluateMvoDeletionRules;
        DeleteChangeHistory = deleteChangeHistory;
        SynchronisedDeprovisioning = synchronisedDeprovisioning;
    }

    /// <summary>
    /// Factory method for creating a task triggered by a user.
    /// </summary>
    public static DeleteConnectedSystemWorkerTask ForUser(int connectedSystemId, Guid userId, string userName, bool evaluateMvoDeletionRules = false, bool deleteChangeHistory = false, bool synchronisedDeprovisioning = false)
    {
        return new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules, deleteChangeHistory, synchronisedDeprovisioning)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = userId,
            InitiatedByName = userName
        };
    }

    /// <summary>
    /// Factory method for creating a task triggered by an API key.
    /// </summary>
    public static DeleteConnectedSystemWorkerTask ForApiKey(int connectedSystemId, Guid apiKeyId, string apiKeyName, bool evaluateMvoDeletionRules = false, bool deleteChangeHistory = false, bool synchronisedDeprovisioning = false)
    {
        return new DeleteConnectedSystemWorkerTask(connectedSystemId, evaluateMvoDeletionRules, deleteChangeHistory, synchronisedDeprovisioning)
        {
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedById = apiKeyId,
            InitiatedByName = apiKeyName
        };
    }
}
