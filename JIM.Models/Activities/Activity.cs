using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Staging;
namespace JIM.Models.Activities;

/// <summary>
/// Enables all activities being performed in JIM, whether user or system initiated to be tracked and logged.
/// This enables areas of JIM to filter the activities view to the relevant objects, i.e. to view all sync runs being
/// run or about to be run, then the relevant page can filter for
/// those activities, and the same for say metaverse object updates to see when a group membership was updated, or a
/// user created, or sync rules changed, etc.
/// </summary>
public class Activity
{
    public Guid Id { get; set; }

    /// <summary>
    /// If this activity was created by another, i.e. a workflow in response to a user action, then it'll be
    /// referenced here.
    /// </summary>
    public Guid? ParentActivityId { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Activities that are not executed in real-time, such as those initiated by JIM.Service processing a queue to get
    /// to a task for the activity will have an Executed time.
    /// noticeably later than the created time for the Activity. This enables you to see what the overall,
    /// user-experienced activity completion time is,
    /// and the actual system execution time.
    /// </summary>
    public DateTime Executed { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Initiator tracking - all activities MUST be attributed to a security principal for audit compliance
    // Uses the standard triad pattern (Type + Id + Name) to survive principal deletion.
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The type of security principal that initiated this activity.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

    /// <summary>
    /// The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this activity.
    /// Retained even if the principal is deleted to support audit investigations.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The display name of the security principal at the time of the activity.
    /// Retained even if the principal is deleted to maintain audit trail readability.
    /// </summary>
    public string? InitiatedByName { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// When the activity is complete, a value for how long the activity took to complete should be stored here.
    /// This may be a noticeably smaller value than the total activity time, as some activities take a while before
    /// they are executed, i.e. those processed by JIM.Service which
    /// employs a queue and may take time to get round to executing the task the activity is for.
    /// </summary>
    public TimeSpan? ExecutionTime { get; set; }

    /// <summary>
    /// How long did this activity take to complete from start to finish?
    /// </summary>
    public TimeSpan? TotalActivityTime { get; set; }

    public ActivityStatus Status { get; set; } = ActivityStatus.NotSet;

    /// <summary>
    /// Enables the user to be kept abreast of what is going on as part of this Activity.
    /// </summary>
    public string? Message { get; set; }

    public ActivityTargetType TargetType { get; init; } = ActivityTargetType.NotSet;

    public ActivityTargetOperationType TargetOperationType { get; set; }

    /// <summary>
    /// The name of the target object. The name is copied here from the object to enable it make identifying it easier
    /// if/when the target object is deleted and cannot be referenced
    /// any more. The value is not kept up to date with the target object, it's just a point in time copy.
    /// Note: Not all objects will have to support a name, so it's optional.
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// Additional context for the target, providing hierarchical information such as the parent entity name.
    /// For example, for a ConnectedSystemRunProfile activity, this would contain the Connected System name.
    /// For a SyncRule activity, this would contain the Connected System name.
    /// For an ObjectMatchingRule activity, this would contain the Sync Rule name.
    /// This is a point-in-time copy, not kept in sync with the referenced entity.
    /// </summary>
    public string? TargetContext { get; set; }

    /// <summary>
    /// Gets a formatted display name combining TargetContext and TargetName.
    /// Returns "Context → Name" if both are present, otherwise just the name, or "(no name)" if neither.
    /// </summary>
    [NotMapped]
    public string DisplayName
    {
        get
        {
            var name = !string.IsNullOrEmpty(TargetName) ? TargetName : "(no name)";
            return !string.IsNullOrEmpty(TargetContext) ? $"{TargetContext} → {name}" : name;
        }
    }

    /// <summary>
    /// Used to calculate a progress bar.
    /// </summary>
    public int ObjectsToProcess { get; set; }

    /// <summary>
    /// Used to calculate a progress bar.
    /// </summary>
    public int ObjectsProcessed { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // summary stats for run profile executions (for list view display)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Aggregate count of all "create" operations from RPEIs.
    /// Includes: Added (import), Projected (sync), Provisioned (export).
    /// Populated when activity completes.
    /// </summary>
    public int TotalObjectCreates { get; set; }

    /// <summary>
    /// Aggregate count of all "update" operations from RPEIs.
    /// Includes: Updated (import), Joined (sync), Exported (export).
    /// Populated when activity completes.
    /// </summary>
    public int TotalObjectUpdates { get; set; }

    /// <summary>
    /// Aggregate count of attribute flow operations from RPEIs.
    /// Only applies to sync runs - data flowing through existing connections.
    /// Populated when activity completes.
    /// </summary>
    public int TotalObjectFlows { get; set; }

    /// <summary>
    /// Aggregate count of all "delete" operations from RPEIs.
    /// Includes: Deleted (import), Disconnected (sync), Deprovisioned (export).
    /// Populated when activity completes.
    /// </summary>
    public int TotalObjectDeletes { get; set; }

    /// <summary>
    /// Count of RPEIs with errors.
    /// Populated when activity completes.
    /// </summary>
    public int TotalObjectErrors { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // pending export reconciliation stats (for confirming imports)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Count of pending exports that were fully confirmed and deleted during a confirming import.
    /// The exported attribute values matched the imported values.
    /// </summary>
    public int PendingExportsConfirmed { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // context specific properties
    // -----------------------------------------------------------------------------------------------------------------

    public int? DataGenerationTemplateId { get; set; }

    public int? ConnectedSystemId { get; set; }

    public int? SyncRuleId { get; set; }

    public Guid? MetaverseObjectId { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // run profile execution related...

    /// <summary>
    /// The run-profile that was created, updated, deleted or executed.
    /// </summary>
    public int? ConnectedSystemRunProfileId { get; set; }

    /// <summary>
    /// If the run profile has been deleted, the type of sync run this was can be accessed here still.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; } = Staging.ConnectedSystemRunType.NotSet;

    // -----------------------------------------------------------------------------------------------------------------
    // history retention cleanup stats (for HistoryRetentionCleanup activities)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// For HistoryRetentionCleanup activities: Count of CSO change records deleted.
    /// </summary>
    public int? DeletedCsoChangeCount { get; set; }

    /// <summary>
    /// For HistoryRetentionCleanup activities: Count of MVO change records deleted.
    /// </summary>
    public int? DeletedMvoChangeCount { get; set; }

    /// <summary>
    /// For HistoryRetentionCleanup activities: Count of Activity records deleted.
    /// </summary>
    public int? DeletedActivityCount { get; set; }

    /// <summary>
    /// For HistoryRetentionCleanup activities: Oldest deleted record timestamp.
    /// </summary>
    public DateTime? DeletedRecordsFromDate { get; set; }

    /// <summary>
    /// For HistoryRetentionCleanup activities: Newest deleted record timestamp.
    /// </summary>
    public DateTime? DeletedRecordsToDate { get; set; }

    // results:
    // what would be useful here is to capture two levels of stats, depending on system settings:
    // - result item with operation type (create/update/delete) and link to the Metaverse Object
    // - result item with operation type (create/update/delete) and link to the Metaverse Object and json snapshot
    //   of imported/exported object

    /// <summary>
    /// If the activity TargetType is ConnectedSystemRunProfile, then these items will provide information on the
    /// objects affected by a sync run.
    /// </summary>
    public List<ActivityRunProfileExecutionItem> RunProfileExecutionItems { get; init; } = new();

    // -----------------------------------------------------------------------------------------------------------------
    // object changes (created/update/delete)
    // this would apply to all object types, i.e. metaverse object, sync rules, connected systems, etc.
    // todo:
    // - json blob that contains object changes (might regret this later, but it seems quicker to get going this way)
    // - some kind of access control for sensitive attribute values being logged, i.e. should someone reviewing the
    //   audit log be able to see sensitive attribute values?

    public ActivityRunProfileExecutionItem AddRunProfileExecutionItem()
    {
        var activityRunProfileExecutionItem = PrepareRunProfileExecutionItem();
        RunProfileExecutionItems.Add(activityRunProfileExecutionItem);
        return activityRunProfileExecutionItem;
    }

    /// <summary>
    /// If you want to prepare ActivityRunProfileExecutionItems separate from the activity to be able to update the
    /// activity without creating a dependency on the item's dependencies
    /// then use this method to bulk add them to this Activity. It will make sure the items are associated with the
    /// activity.
    /// </summary>
    public void AddRunProfileExecutionItems(List<ActivityRunProfileExecutionItem> runProfileExecutionItems)
    {
        foreach (var item in runProfileExecutionItems)
        {
            item.Activity = this;
            item.ActivityId = Id;
            RunProfileExecutionItems.Add(item);
        }
    }

    /// <summary>
    /// Prepares a Run Profile Execution Item that relates to the Activity, but has not yet been added to it.
    /// This enables items to be prepared, but a decision on whether to persist it or not can come later at the
    /// caller's discretion.
    /// </summary>
    public ActivityRunProfileExecutionItem PrepareRunProfileExecutionItem()
    {
        var activityRunProfileExecutionItem = new ActivityRunProfileExecutionItem
        {
            Activity = this,
            ActivityId = Id
        };
        return activityRunProfileExecutionItem;
    }
}
