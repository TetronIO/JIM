// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Activities;
namespace JIM.Models.Tasking;

/// <summary>
/// Worker task for deleting a Synchronisation Rule whose contributed Metaverse attribute values are being
/// recalled first (#1537). Queued when the administrator chooses recall (the default) and the rule still
/// contributes values: the rule is disabled at queue time, the task withdraws the values by provenance
/// (re-electing surviving contributors and staging Pending Exports), and its final step deletes the rule via
/// the ordinary delete path. Keep, or a rule with no contributed values, deletes synchronously and never
/// queues this task.
/// </summary>
public class DeleteSyncRuleWorkerTask : WorkerTask
{
    /// <summary>
    /// The id of the Synchronisation Rule to recall values for and then delete.
    /// </summary>
    public int SyncRuleId { get; set; }

    /// <summary>
    /// Whether the rule's contributed Metaverse attribute values are recalled before the deletion. True in
    /// practice (a keep choice deletes synchronously without queueing); carried so the executor's behaviour
    /// is explicit on the task itself.
    /// </summary>
    public bool RecallContributedValues { get; set; } = true;

    /// <summary>
    /// Optional reason for the deletion, entered at request time. Transient (never persisted on the task): it is
    /// copied onto the task's Activity when the task is created, so it survives to when the worker runs
    /// without needing a column of its own. Null when no reason was supplied.
    /// </summary>
    [NotMapped]
    public string? ChangeReason { get; set; }

    public DeleteSyncRuleWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    public DeleteSyncRuleWorkerTask(int syncRuleId, bool recallContributedValues = true)
    {
        SyncRuleId = syncRuleId;
        RecallContributedValues = recallContributedValues;
    }

    /// <summary>
    /// Factory method for creating a task triggered by a user.
    /// </summary>
    public static DeleteSyncRuleWorkerTask ForUser(int syncRuleId, Guid userId, string userName, bool recallContributedValues = true)
    {
        return new DeleteSyncRuleWorkerTask(syncRuleId, recallContributedValues)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = userId,
            InitiatedByName = userName
        };
    }

    /// <summary>
    /// Factory method for creating a task triggered by an API key.
    /// </summary>
    public static DeleteSyncRuleWorkerTask ForApiKey(int syncRuleId, Guid apiKeyId, string apiKeyName, bool recallContributedValues = true)
    {
        return new DeleteSyncRuleWorkerTask(syncRuleId, recallContributedValues)
        {
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedById = apiKeyId,
            InitiatedByName = apiKeyName
        };
    }
}
