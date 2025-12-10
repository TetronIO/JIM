using JIM.Models.Activities;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Header DTO for Activity in list views.
/// </summary>
public class ActivityHeader
{
    /// <summary>
    /// The unique identifier of the activity.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// When the activity was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the activity started executing.
    /// </summary>
    public DateTime? Executed { get; set; }

    /// <summary>
    /// The current status of the activity.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The type of target this activity operates on.
    /// </summary>
    public ActivityTargetType TargetType { get; set; }

    /// <summary>
    /// The operation being performed.
    /// </summary>
    public ActivityTargetOperationType TargetOperationType { get; set; }

    /// <summary>
    /// The name of the target object.
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// Progress message for the activity.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Name of the user who initiated this activity.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// Number of objects to process (for progress tracking).
    /// </summary>
    public int ObjectsToProcess { get; set; }

    /// <summary>
    /// Number of objects processed so far (for progress tracking).
    /// </summary>
    public int ObjectsProcessed { get; set; }

    /// <summary>
    /// How long the activity took to execute (once complete).
    /// </summary>
    public TimeSpan? ExecutionTime { get; set; }

    /// <summary>
    /// Total time from creation to completion.
    /// </summary>
    public TimeSpan? TotalActivityTime { get; set; }

    /// <summary>
    /// The run type if this is a sync activity.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; }

    /// <summary>
    /// Creates a header DTO from an Activity entity.
    /// </summary>
    public static ActivityHeader FromEntity(Activity activity)
    {
        return new ActivityHeader
        {
            Id = activity.Id,
            Created = activity.Created,
            Executed = activity.Executed == default ? null : activity.Executed,
            Status = activity.Status,
            TargetType = activity.TargetType,
            TargetOperationType = activity.TargetOperationType,
            TargetName = activity.TargetName,
            Message = activity.Message,
            InitiatedByName = activity.InitiatedByName,
            ObjectsToProcess = activity.ObjectsToProcess,
            ObjectsProcessed = activity.ObjectsProcessed,
            ExecutionTime = activity.ExecutionTime,
            TotalActivityTime = activity.TotalActivityTime,
            ConnectedSystemRunType = activity.ConnectedSystemRunType
        };
    }
}

/// <summary>
/// Detailed DTO for a single Activity including error information and execution stats.
/// </summary>
public class ActivityDetailDto
{
    /// <summary>
    /// The unique identifier of the activity.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Parent activity ID if this was triggered by another activity.
    /// </summary>
    public Guid? ParentActivityId { get; set; }

    /// <summary>
    /// When the activity was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the activity started executing.
    /// </summary>
    public DateTime? Executed { get; set; }

    /// <summary>
    /// The current status of the activity.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The type of target this activity operates on.
    /// </summary>
    public ActivityTargetType TargetType { get; set; }

    /// <summary>
    /// The operation being performed.
    /// </summary>
    public ActivityTargetOperationType TargetOperationType { get; set; }

    /// <summary>
    /// The name of the target object.
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// Progress message for the activity.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Name of the user who initiated this activity.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// Number of objects to process (for progress tracking).
    /// </summary>
    public int ObjectsToProcess { get; set; }

    /// <summary>
    /// Number of objects processed so far (for progress tracking).
    /// </summary>
    public int ObjectsProcessed { get; set; }

    /// <summary>
    /// How long the activity took to execute (once complete).
    /// </summary>
    public TimeSpan? ExecutionTime { get; set; }

    /// <summary>
    /// Total time from creation to completion.
    /// </summary>
    public TimeSpan? TotalActivityTime { get; set; }

    /// <summary>
    /// Error message if the activity failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Error stack trace if the activity failed.
    /// </summary>
    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// The run type if this is a sync activity.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; }

    /// <summary>
    /// The connected system ID if applicable.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The run profile ID if applicable.
    /// </summary>
    public int? ConnectedSystemRunProfileId { get; set; }

    /// <summary>
    /// The sync rule ID if applicable.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The metaverse object ID if applicable.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The data generation template ID if applicable.
    /// </summary>
    public int? DataGenerationTemplateId { get; set; }

    /// <summary>
    /// Execution statistics for run profile activities.
    /// </summary>
    public ActivityRunProfileExecutionStatsDto? ExecutionStats { get; set; }

    /// <summary>
    /// Creates a detail DTO from an Activity entity.
    /// </summary>
    public static ActivityDetailDto FromEntity(Activity activity, ActivityRunProfileExecutionStats? stats = null)
    {
        return new ActivityDetailDto
        {
            Id = activity.Id,
            ParentActivityId = activity.ParentActivityId,
            Created = activity.Created,
            Executed = activity.Executed == default ? null : activity.Executed,
            Status = activity.Status,
            TargetType = activity.TargetType,
            TargetOperationType = activity.TargetOperationType,
            TargetName = activity.TargetName,
            Message = activity.Message,
            InitiatedByName = activity.InitiatedByName,
            ObjectsToProcess = activity.ObjectsToProcess,
            ObjectsProcessed = activity.ObjectsProcessed,
            ExecutionTime = activity.ExecutionTime,
            TotalActivityTime = activity.TotalActivityTime,
            ErrorMessage = activity.ErrorMessage,
            ErrorStackTrace = activity.ErrorStackTrace,
            ConnectedSystemRunType = activity.ConnectedSystemRunType,
            ConnectedSystemId = activity.ConnectedSystemId,
            ConnectedSystemRunProfileId = activity.ConnectedSystemRunProfileId,
            SyncRuleId = activity.SyncRuleId,
            MetaverseObjectId = activity.MetaverseObjectId,
            DataGenerationTemplateId = activity.DataGenerationTemplateId,
            ExecutionStats = stats != null ? ActivityRunProfileExecutionStatsDto.FromEntity(stats) : null
        };
    }
}

/// <summary>
/// DTO for run profile execution statistics.
/// </summary>
public class ActivityRunProfileExecutionStatsDto
{
    /// <summary>
    /// Total number of object changes.
    /// </summary>
    public int TotalObjectChangeCount { get; set; }

    /// <summary>
    /// Number of objects created.
    /// </summary>
    public int TotalObjectCreates { get; set; }

    /// <summary>
    /// Number of objects updated.
    /// </summary>
    public int TotalObjectUpdates { get; set; }

    /// <summary>
    /// Number of objects deleted.
    /// </summary>
    public int TotalObjectDeletes { get; set; }

    /// <summary>
    /// Number of objects with errors.
    /// </summary>
    public int TotalObjectErrors { get; set; }

    /// <summary>
    /// Number of distinct object types affected.
    /// </summary>
    public int TotalObjectTypes { get; set; }

    /// <summary>
    /// Creates a DTO from the stats entity.
    /// </summary>
    public static ActivityRunProfileExecutionStatsDto FromEntity(ActivityRunProfileExecutionStats stats)
    {
        return new ActivityRunProfileExecutionStatsDto
        {
            TotalObjectChangeCount = stats.TotalObjectChangeCount,
            TotalObjectCreates = stats.TotalObjectCreates,
            TotalObjectUpdates = stats.TotalObjectUpdates,
            TotalObjectDeletes = stats.TotalObjectDeletes,
            TotalObjectErrors = stats.TotalObjectErrors,
            TotalObjectTypes = stats.TotalObjectTypes
        };
    }
}
