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
    /// The type of security principal that initiated this activity.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the security principal that initiated this activity.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// Name of the user or API key that initiated this activity.
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
            InitiatedByType = activity.InitiatedByType,
            InitiatedById = activity.InitiatedById,
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
    /// The type of security principal that initiated this activity.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the security principal that initiated this activity.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// Name of the user or API key that initiated this activity.
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
            InitiatedByType = activity.InitiatedByType,
            InitiatedById = activity.InitiatedById,
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
/// DTO for run profile execution statistics with granular change type counts.
/// </summary>
public class ActivityRunProfileExecutionStatsDto
{
    #region Shared Stats
    /// <summary>
    /// Total number of object changes.
    /// </summary>
    public int TotalObjectChangeCount { get; set; }

    /// <summary>
    /// Number of objects with errors.
    /// </summary>
    public int TotalObjectErrors { get; set; }

    /// <summary>
    /// Number of distinct object types affected.
    /// </summary>
    public int TotalObjectTypes { get; set; }

    /// <summary>
    /// Number of objects with no changes.
    /// </summary>
    public int TotalNoChanges { get; set; }
    #endregion

    #region Import Stats (CSO operations)
    /// <summary>
    /// Number of CSOs added to staging during import.
    /// </summary>
    public int TotalCsoAdds { get; set; }

    /// <summary>
    /// Number of existing CSOs updated during import.
    /// </summary>
    public int TotalCsoUpdates { get; set; }

    /// <summary>
    /// Number of CSOs marked as deleted.
    /// </summary>
    public int TotalCsoDeletes { get; set; }
    #endregion

    #region Sync Stats (MVO operations)
    /// <summary>
    /// Number of new MVOs created via projection.
    /// </summary>
    public int TotalProjections { get; set; }

    /// <summary>
    /// Number of CSOs joined to existing MVOs.
    /// </summary>
    public int TotalJoins { get; set; }

    /// <summary>
    /// Number of attribute flows between CSOs and MVOs.
    /// </summary>
    public int TotalAttributeFlows { get; set; }

    /// <summary>
    /// Number of CSOs disconnected from MVOs.
    /// </summary>
    public int TotalDisconnections { get; set; }
    #endregion

    #region Export Stats
    /// <summary>
    /// Number of new objects provisioned to target systems.
    /// </summary>
    public int TotalProvisioned { get; set; }

    /// <summary>
    /// Number of existing objects exported with updated attributes.
    /// </summary>
    public int TotalExported { get; set; }

    /// <summary>
    /// Number of objects deprovisioned from target systems.
    /// </summary>
    public int TotalDeprovisioned { get; set; }
    #endregion

    #region Aggregate Stats (for backward compatibility)
    /// <summary>
    /// Aggregate count of all "create" operations (CSO adds, projections, provisioning).
    /// </summary>
    public int TotalObjectCreates { get; set; }

    /// <summary>
    /// Aggregate count of all "update" operations (CSO updates, joins, attribute flows, exports).
    /// </summary>
    public int TotalObjectUpdates { get; set; }

    /// <summary>
    /// Aggregate count of all "delete" operations (CSO deletes, disconnections, deprovisioning).
    /// </summary>
    public int TotalObjectDeletes { get; set; }
    #endregion

    /// <summary>
    /// Creates a DTO from the stats entity.
    /// </summary>
    public static ActivityRunProfileExecutionStatsDto FromEntity(ActivityRunProfileExecutionStats stats)
    {
        return new ActivityRunProfileExecutionStatsDto
        {
            // Shared
            TotalObjectChangeCount = stats.TotalObjectChangeCount,
            TotalObjectErrors = stats.TotalObjectErrors,
            TotalObjectTypes = stats.TotalObjectTypes,
            TotalNoChanges = stats.TotalNoChanges,

            // Import
            TotalCsoAdds = stats.TotalCsoAdds,
            TotalCsoUpdates = stats.TotalCsoUpdates,
            TotalCsoDeletes = stats.TotalCsoDeletes,

            // Sync
            TotalProjections = stats.TotalProjections,
            TotalJoins = stats.TotalJoins,
            TotalAttributeFlows = stats.TotalAttributeFlows,
            TotalDisconnections = stats.TotalDisconnections,

            // Export
            TotalProvisioned = stats.TotalProvisioned,
            TotalExported = stats.TotalExported,
            TotalDeprovisioned = stats.TotalDeprovisioned,

            // Aggregates (computed from model)
            TotalObjectCreates = stats.TotalObjectCreates,
            TotalObjectUpdates = stats.TotalObjectUpdates,
            TotalObjectDeletes = stats.TotalObjectDeletes
        };
    }
}
