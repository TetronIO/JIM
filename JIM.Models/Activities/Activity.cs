using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Models.Activities;

/// <summary>
/// Enables all activities being performed in JIM, whether user or system initiated to be tracked and logged.
/// This enables areas of JIM to filter the activities view to the relevant objects, i.e. to view all sync runs being
/// run or about to be run, then the relevant page can filter for those activities, and the same for say metaverse
/// object updates to see when a group membership was updated, or a user created, or sync rules changed, etc.
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
    /// Activities that are not executed in real-time, such as those initiated by JIM.Service processing a queue to
    /// get to a task for the activity will have an Executed time.
    /// noticeably later than the created time for the Activity. This enables you to see what the overall,
    /// user-experienced activity completion time is, and the actual system execution time.
    /// </summary>
    public DateTime Executed {  get; set; }

    /// <summary>
    /// A link to the Metaverse Object for a user if this activity was initiated by a person.
    /// </summary>
    public MetaverseObject? InitiatedBy { get; set; }

    public string? InitiatedByName { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// When the activity is complete, a value for how long the activity took to complete should be stored here. 
    /// This may be a noticeably smaller value than the total activity time, as some activities take a while before they
    /// are executed, i.e. those processed by JIM.Service which employs a queue and may take time to get round to 
    /// executing the task the activity is for.
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
    /// The name of the target object. The name is copied here from the object to enable it make identifying
    /// it easier if/when the target object is deleted and cannot be referenced anymore. The value is not kept
    /// up to date with the target object, it's just a point in time copy. 
    /// Note: Not all objects will have to support a name, so it's optional.
    /// </summary>
    public string? TargetName { get; set; }
    
    /// <summary>
    /// Used to calculate a progress bar.
    /// </summary>
    public int ObjectsToProcess { get; set; }
    
    /// <summary>
    /// Used to calculate a progress bar.
    /// </summary>
    public int ObjectsProcessed { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // context specific properties
    // -----------------------------------------------------------------------------------------------------------------

    public int? DataGenerationTemplateId { get; set; }

    public int? ConnectedSystemId { get; set; }

    public int? SyncRuleId { get; set; }

    public Guid? MetaverseObjectId {  get; set; }

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

    // results:
    // what would be useful here is to capture two levels of stats, depending on system settings:
    // - result item with operation type (create/update/delete) and link to the Metaverse Object
    // - result item with operation type (create/update/delete) and link to the Metaverse Object and json snapshot
    //   of imported/exported object

     /// <summary>
    /// If the activity TargetType is ConnectedSystemRunProfile, then these items will provide information on the objects affected by a sync run.
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
    /// Prepares a Run Profile Execution Item that relates to the Activity, but has not yet been added to it.
    /// This enables items to be prepared, but a decision on whether to persist it or not can come later at the
    /// callers' discretion.
    /// </summary>
    public ActivityRunProfileExecutionItem PrepareRunProfileExecutionItem()
    {
        var activityRunProfileExecutionItem = new ActivityRunProfileExecutionItem {
            Activity = this,
            ActivityId = Id
        };
        return activityRunProfileExecutionItem;
    }
}
