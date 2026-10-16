// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Header DTO for Activity in list views.
/// </summary>
public class ActivityHeader
{
    /// <summary>
    /// The unique identifier of the Activity.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// When the Activity was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the Activity started executing.
    /// </summary>
    public DateTime? Executed { get; set; }

    /// <summary>
    /// The current status of the Activity.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The type of target this Activity operates on.
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
    /// Additional context for the target, such as the parent entity name.
    /// For example, for a Run Profile Activity this would be the Connected System name.
    /// </summary>
    public string? TargetContext { get; set; }

    /// <summary>
    /// Progress message for the Activity.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The type of security principal that initiated this Activity.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the security principal that initiated this Activity.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// Name of the user or API key that initiated this Activity.
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
    /// How long the Activity took to execute (once complete).
    /// </summary>
    public TimeSpan? ExecutionTime { get; set; }

    /// <summary>
    /// Total time from creation to completion.
    /// </summary>
    public TimeSpan? TotalActivityTime { get; set; }

    /// <summary>
    /// The run type, for a Run Profile execution.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; }

    #region Import Stats
    /// <summary>
    /// Count of new Connected System Objects added to the Connector Space during import.
    /// </summary>
    public int TotalAdded { get; set; }

    /// <summary>
    /// Count of existing Connected System Objects updated during import.
    /// </summary>
    public int TotalUpdated { get; set; }

    /// <summary>
    /// Count of Connected System Objects marked as deleted (source system deletion detected).
    /// </summary>
    public int TotalDeleted { get; set; }
    #endregion

    #region Sync Stats
    /// <summary>
    /// Count of new Metaverse Objects created via projection.
    /// </summary>
    public int TotalProjected { get; set; }

    /// <summary>
    /// Count of Connected System Objects joined to existing Metaverse Objects.
    /// </summary>
    public int TotalJoined { get; set; }

    /// <summary>
    /// Count of Attribute Flow operations.
    /// </summary>
    public int TotalAttributeFlows { get; set; }

    /// <summary>
    /// Count of Connected System Objects disconnected from Metaverse Objects.
    /// </summary>
    public int TotalDisconnected { get; set; }

    /// <summary>
    /// Count of Connected System Objects disconnected because they fell out of scope of import Synchronisation Rule scoping criteria.
    /// </summary>
    public int TotalDisconnectedOutOfScope { get; set; }

    /// <summary>
    /// Count of Connected System Objects that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined).
    /// </summary>
    public int TotalOutOfScopeRetainJoin { get; set; }

    /// <summary>
    /// Count of Connected System Objects where drift was detected and corrective Pending Exports were created.
    /// </summary>
    public int TotalDriftCorrections { get; set; }

    /// <summary>
    /// Count of Connected System Objects provisioned to target Connected Systems during synchronisation.
    /// </summary>
    public int TotalProvisioned { get; set; }
    #endregion

    #region Export Stats
    /// <summary>
    /// Count of objects exported to target systems (includes both initial creation and subsequent updates).
    /// </summary>
    public int TotalExported { get; set; }

    /// <summary>
    /// Count of objects deprovisioned from target systems.
    /// </summary>
    public int TotalDeprovisioned { get; set; }
    #endregion

    #region Direct Creation Stats
    /// <summary>
    /// Count of Metaverse Objects created directly (by example data generation or in the portal) rather than by projection.
    /// </summary>
    public int TotalCreated { get; set; }
    #endregion

    #region Pending Export Stats
    /// <summary>
    /// Count of Pending Exports staged for the next export run.
    /// </summary>
    public int TotalPendingExports { get; set; }
    #endregion

    #region Shared Stats
    /// <summary>
    /// Count of Run Profile Execution Items with errors.
    /// </summary>
    public int TotalErrors { get; set; }
    #endregion

    /// <summary>
    /// The number of direct child Activities linked to this Activity.
    /// Zero for Activities with no children.
    /// </summary>
    public int ChildActivityCount { get; set; }

    /// <summary>
    /// If a Schedule Execution produced this Activity, the execution's unique identifier. Null otherwise.
    /// </summary>
    public Guid? ScheduleExecutionId { get; set; }

    /// <summary>
    /// The step within the Schedule Execution that produced this Activity (0-based). Read with
    /// <c>ScheduleExecutionId</c> to identify exactly which step ran. Null when no Schedule produced it.
    /// </summary>
    public int? ScheduleStepIndex { get; set; }

    /// <summary>
    /// If a Schedule produced this Activity, the Schedule's unique identifier. Null otherwise.
    /// </summary>
    public Guid? ScheduledByScheduleId { get; set; }

    /// <summary>
    /// The producing Schedule's name, as a snapshot taken when the Activity was created, so history still reads
    /// correctly after the Schedule has been renamed or deleted. Null when no Schedule produced this Activity.
    /// </summary>
    public string? ScheduledByScheduleName { get; set; }

    /// <summary>
    /// Security audit events only: why the authentication failed, as a short sanitised reason such as
    /// "Access token expired" or "API key not found". Null for every other Activity, and for a successful sign-in.
    /// </summary>
    public string? SecurityEventReason { get; set; }

    /// <summary>
    /// Security audit events only: the client IP address the attempt came from, where one was available.
    /// </summary>
    public string? ClientIpAddress { get; set; }

    /// <summary>
    /// Security audit events only: the identifying prefix of the API key involved, for example <c>jim_ak_7f3a</c>.
    /// Never the key itself. Null when no API key was involved.
    /// </summary>
    public string? ApiKeyPrefix { get; set; }

    /// <summary>
    /// Failed authentication events only: how many matching attempts (same reason, client IP and API key prefix)
    /// this Activity counts within its 15-minute window.
    /// </summary>
    public int? AttemptCount { get; set; }

    /// <summary>
    /// Failed authentication events only: when the first attempt this Activity counts was made (UTC).
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>
    /// Failed authentication events only: when the most recent attempt this Activity counts was made (UTC). It
    /// advances as further attempts arrive, so a poller can pick up a changed count by watching it.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Failed authentication events only: the start of the 15-minute UTC window this Activity aggregates. Attempts
    /// after the window ends are counted on a new Activity, so once the window has passed this one no longer changes.
    /// </summary>
    public DateTime? AggregationWindowStart { get; set; }

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
            TargetContext = activity.TargetContext,
            Message = activity.Message,
            InitiatedByType = activity.InitiatedByType,
            InitiatedById = activity.InitiatedById,
            InitiatedByName = activity.InitiatedByName,
            ObjectsToProcess = activity.ObjectsToProcess,
            ObjectsProcessed = activity.ObjectsProcessed,
            ExecutionTime = activity.ExecutionTime,
            TotalActivityTime = activity.TotalActivityTime,
            ConnectedSystemRunType = activity.ConnectedSystemRunType,

            // Import
            TotalAdded = activity.TotalAdded,
            TotalUpdated = activity.TotalUpdated,
            TotalDeleted = activity.TotalDeleted,

            // Sync
            TotalProjected = activity.TotalProjected,
            TotalJoined = activity.TotalJoined,
            TotalAttributeFlows = activity.TotalAttributeFlows,
            TotalDisconnected = activity.TotalDisconnected,
            TotalDisconnectedOutOfScope = activity.TotalDisconnectedOutOfScope,
            TotalOutOfScopeRetainJoin = activity.TotalOutOfScopeRetainJoin,
            TotalDriftCorrections = activity.TotalDriftCorrections,
            TotalProvisioned = activity.TotalProvisioned,

            // Export
            TotalExported = activity.TotalExported,
            TotalDeprovisioned = activity.TotalDeprovisioned,

            // Direct Creation
            TotalCreated = activity.TotalCreated,

            // Pending Exports
            TotalPendingExports = activity.TotalPendingExports,

            // Shared
            TotalErrors = activity.TotalErrors,

            // Schedule attribution
            ScheduleExecutionId = activity.ScheduleExecutionId,
            ScheduleStepIndex = activity.ScheduleStepIndex,
            ScheduledByScheduleId = activity.ScheduledByScheduleId,
            ScheduledByScheduleName = activity.ScheduledByScheduleName,

            // Security audit events; an aggregated failure stores "no API key" as "" so its index can dedupe
            SecurityEventReason = activity.SecurityEventReason,
            ClientIpAddress = activity.ClientIpAddress,
            ApiKeyPrefix = string.IsNullOrEmpty(activity.ApiKeyPrefix) ? null : activity.ApiKeyPrefix,
            AttemptCount = activity.AttemptCount,
            FirstSeen = activity.FirstSeen,
            LastSeen = activity.LastSeen,
            AggregationWindowStart = activity.AggregationWindowStart
        };
    }

    /// <summary>
    /// Creates a header DTO from an Activity entity with a known child Activity count.
    /// </summary>
    public static ActivityHeader FromEntity(Activity activity, int childActivityCount)
    {
        var header = FromEntity(activity);
        header.ChildActivityCount = childActivityCount;
        return header;
    }
}

/// <summary>
/// Detailed DTO for a single Activity including error information and execution stats.
/// </summary>
public class ActivityDetailDto
{
    /// <summary>
    /// The unique identifier of the Activity.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Parent Activity ID if this was triggered by another Activity.
    /// </summary>
    public Guid? ParentActivityId { get; set; }

    /// <summary>
    /// When the Activity was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the Activity started executing.
    /// </summary>
    public DateTime? Executed { get; set; }

    /// <summary>
    /// The current status of the Activity.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The type of target this Activity operates on.
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
    /// Additional context for the target, such as the parent entity name.
    /// For example, for a Run Profile Activity this would be the Connected System name.
    /// </summary>
    public string? TargetContext { get; set; }

    /// <summary>
    /// Progress message for the Activity.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The type of security principal that initiated this Activity.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the security principal that initiated this Activity.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// Name of the user or API key that initiated this Activity.
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
    /// How long the Activity took to execute (once complete).
    /// </summary>
    public TimeSpan? ExecutionTime { get; set; }

    /// <summary>
    /// Total time from creation to completion.
    /// </summary>
    public TimeSpan? TotalActivityTime { get; set; }

    /// <summary>
    /// A warning that did not stop the Activity, for example that a Delta Import fell back to a Full Import.
    /// </summary>
    public string? WarningMessage { get; set; }

    /// <summary>
    /// Error message if the Activity failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Error stack trace if the Activity failed.
    /// </summary>
    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// Structured detail about the failure, as JSON, where there is something specific worth acting on beyond the
    /// message. An LDAPS connection refused because of the directory server's certificate records that certificate
    /// here: its subject, issuer, the names it was issued for, its validity dates, its thumbprint, and which check
    /// it failed. Null for failures with no structured detail.
    /// </summary>
    public string? ErrorDetail { get; set; }

    /// <summary>
    /// The run type, for a Run Profile execution.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; }

    /// <summary>
    /// The Connected System ID if applicable.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The Run Profile ID if applicable.
    /// </summary>
    public int? ConnectedSystemRunProfileId { get; set; }

    /// <summary>
    /// The Synchronisation Rule ID if applicable.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The Metaverse Object ID if applicable.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The data generation template ID if applicable.
    /// </summary>
    public int? ExampleDataTemplateId { get; set; }

    /// <summary>
    /// Execution statistics for Run Profile Activities.
    /// </summary>
    public ActivityRunProfileExecutionStatsDto? ExecutionStats { get; set; }

    /// <summary>
    /// The steps the Run Profile execution moved through, in run order, each with how it
    /// turned out and how long it took. This is what answers "where did the four hours go?" on a
    /// run that finished days ago. Empty for other Activity types, and for runs that predate phase
    /// recording.
    /// </summary>
    /// <remarks>Run Profile phases, #454.</remarks>
    public List<ActivityPhaseDto> Phases { get; set; } = [];

    /// <summary>
    /// If a Schedule Execution produced this Activity, the execution's unique identifier. Null otherwise.
    /// </summary>
    public Guid? ScheduleExecutionId { get; set; }

    /// <summary>
    /// The step within the Schedule Execution that produced this Activity (0-based). Read with
    /// <c>ScheduleExecutionId</c> to identify exactly which step ran. Null when no Schedule produced it.
    /// </summary>
    public int? ScheduleStepIndex { get; set; }

    /// <summary>
    /// If a Schedule produced this Activity, the Schedule's unique identifier. Null otherwise.
    /// </summary>
    public Guid? ScheduledByScheduleId { get; set; }

    /// <summary>
    /// The producing Schedule's name, as a snapshot taken when the Activity was created, so history still reads
    /// correctly after the Schedule has been renamed or deleted. Null when no Schedule produced this Activity.
    /// </summary>
    public string? ScheduledByScheduleName { get; set; }

    /// <summary>
    /// For a configuration-change Activity, the optional reason supplied for the change.
    /// </summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// For a configuration-change Activity, the per-object version number this change produced.
    /// </summary>
    public int? ConfigurationChangeVersion { get; set; }

    /// <summary>
    /// For a configuration-change Activity, the redacted snapshot of the object's state at this change; null otherwise.
    /// </summary>
    public ConfigurationSnapshot? ConfigurationChangeSnapshot { get; set; }

    /// <summary>
    /// For a change made after reading a Configuration Change Preview, the preview's Activity id, as recorded by the
    /// change's <c>previewActivityId</c>; read it with <c>GET /api/v1/previews/{activityId}</c>. Null when no preview
    /// informed the change, and for every other kind of Activity.
    /// </summary>
    public Guid? PreviewActivityId { get; set; }

    /// <summary>
    /// When the preview in <c>PreviewActivityId</c> had been overtaken by the time the change was made: the latest
    /// run or configuration change recorded after the preview started that could change its answer. Null when the
    /// preview was current, or when no preview informed the change.
    /// </summary>
    public DateTime? PreviewOvertakenAt { get; set; }

    /// <summary>
    /// What had overtaken the preview in <c>PreviewActivityId</c> by the time the change was made, for example
    /// "Run Profile 'Delta Import' ran on Connected System 'HR Import'". Null when <c>PreviewOvertakenAt</c> is.
    /// </summary>
    public string? PreviewOvertakenBy { get; set; }

    /// <summary>
    /// For a Connector Space clear, how many Pending Exports to the Connected System the clear discarded;
    /// null for every other kind of Activity.
    /// </summary>
    public int? ClearedPendingExportCount { get; set; }

    /// <summary>
    /// For a Connector Space clear, how many Connected System Objects the clear deleted; null for every
    /// other kind of Activity.
    /// </summary>
    public int? ClearedConnectedSystemObjectCount { get; set; }

    /// <summary>
    /// For a Connector Space clear, how many Metaverse Objects were joined at the moment of the clear and
    /// so recorded for the post-clear reconciliation to expect back; null for every other kind of
    /// Activity. Not a removal: these objects are what the next Full Synchronisation compares against once
    /// a Full Import has rebuilt the Connector Space.
    /// </summary>
    /// <remarks>#1605.</remarks>
    public int? ClearedJoinRecordCount { get; set; }

    /// <summary>
    /// How many creates the Run Profile's Max creates limit withheld this run; they stay pending for a later Export
    /// run. Populated (zero when nothing was withheld) on every Export Activity; null otherwise.
    /// </summary>
    /// <remarks>Run Profile Safeguards, #1618.</remarks>
    public int? ExportCreatesWithheld { get; set; }

    /// <summary>
    /// How many updates the Run Profile's Max updates limit withheld this run; they stay pending for a later Export
    /// run. Populated (zero when nothing was withheld) on every Export Activity; null otherwise.
    /// </summary>
    /// <remarks>Run Profile Safeguards, #1618.</remarks>
    public int? ExportUpdatesWithheld { get; set; }

    /// <summary>
    /// How many deletes the Run Profile's Max deletes limit withheld this run; they stay pending for a later Export
    /// run. Populated (zero when nothing was withheld) on every Export Activity; null otherwise.
    /// </summary>
    /// <remarks>Run Profile Safeguards, #1618.</remarks>
    public int? ExportDeletesWithheld { get; set; }

    /// <summary>
    /// How many Connected System Objects the Run Profile's Max detected deletions and Max detected deletions
    /// percent limits withheld this run; those objects are left as they are, not marked as deleted. Populated (zero
    /// when nothing was withheld) on every Full Import Activity that ran deletion detection; null on every other Activity, including a Full Import that imported zero
    /// objects and so skipped detection entirely.
    /// </summary>
    /// <remarks>Run Profile Safeguards (#1618), Layer 2.</remarks>
    public int? DetectedDeletionsWithheld { get; set; }

    /// <summary>
    /// Security audit events only: why the authentication failed, as a short sanitised reason such as
    /// "Access token expired" or "API key not found". Null for every other Activity, and for a successful sign-in.
    /// </summary>
    public string? SecurityEventReason { get; set; }

    /// <summary>
    /// Security audit events only: the client IP address the attempt came from, where one was available.
    /// </summary>
    public string? ClientIpAddress { get; set; }

    /// <summary>
    /// Security audit events only: the identifying prefix of the API key involved, for example <c>jim_ak_7f3a</c>.
    /// Never the key itself. Null when no API key was involved.
    /// </summary>
    public string? ApiKeyPrefix { get; set; }

    /// <summary>
    /// Failed authentication events only: how many matching attempts (same reason, client IP and API key prefix)
    /// this Activity counts within its 15-minute window.
    /// </summary>
    public int? AttemptCount { get; set; }

    /// <summary>
    /// Failed authentication events only: when the first attempt this Activity counts was made (UTC).
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>
    /// Failed authentication events only: when the most recent attempt this Activity counts was made (UTC). It
    /// advances as further attempts arrive, so a poller can pick up a changed count by watching it.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Failed authentication events only: the start of the 15-minute UTC window this Activity aggregates. Attempts
    /// after the window ends are counted on a new Activity, so once the window has passed this one no longer changes.
    /// </summary>
    public DateTime? AggregationWindowStart { get; set; }

    /// <summary>
    /// Creates a detail DTO from an Activity entity.
    /// </summary>
    public static ActivityDetailDto FromEntity(
        Activity activity,
        ActivityRunProfileExecutionStats? stats = null,
        IReadOnlyList<ActivityPhase>? phases = null)
    {
        return new ActivityDetailDto
        {
            Phases = (phases ?? []).OrderBy(p => p.Order).Select(ActivityPhaseDto.FromEntity).ToList(),
            Id = activity.Id,
            ParentActivityId = activity.ParentActivityId,
            Created = activity.Created,
            Executed = activity.Executed == default ? null : activity.Executed,
            Status = activity.Status,
            TargetType = activity.TargetType,
            TargetOperationType = activity.TargetOperationType,
            TargetName = activity.TargetName,
            TargetContext = activity.TargetContext,
            Message = activity.Message,
            InitiatedByType = activity.InitiatedByType,
            InitiatedById = activity.InitiatedById,
            InitiatedByName = activity.InitiatedByName,
            ObjectsToProcess = activity.ObjectsToProcess,
            ObjectsProcessed = activity.ObjectsProcessed,
            ExecutionTime = activity.ExecutionTime,
            TotalActivityTime = activity.TotalActivityTime,
            WarningMessage = activity.WarningMessage,
            ErrorMessage = activity.ErrorMessage,
            ErrorStackTrace = activity.ErrorStackTrace,
            ErrorDetail = activity.ErrorDetail,
            ConnectedSystemRunType = activity.ConnectedSystemRunType,
            ConnectedSystemId = activity.ConnectedSystemId,
            ConnectedSystemRunProfileId = activity.ConnectedSystemRunProfileId,
            SyncRuleId = activity.SyncRuleId,
            MetaverseObjectId = activity.MetaverseObjectId,
            ExampleDataTemplateId = activity.ExampleDataTemplateId,
            ExecutionStats = stats != null ? ActivityRunProfileExecutionStatsDto.FromEntity(stats) : null,
            ScheduleExecutionId = activity.ScheduleExecutionId,
            ScheduleStepIndex = activity.ScheduleStepIndex,
            ScheduledByScheduleId = activity.ScheduledByScheduleId,
            ScheduledByScheduleName = activity.ScheduledByScheduleName,
            ChangeReason = activity.ChangeReason,
            ConfigurationChangeVersion = activity.ConfigurationChangeVersion,
            PreviewActivityId = activity.PreviewActivityId,
            PreviewOvertakenAt = activity.PreviewOvertakenAt,
            PreviewOvertakenBy = activity.PreviewOvertakenBy,
            ConfigurationChangeSnapshot = ConfigurationSnapshotService.Deserialise(activity.ConfigurationChangeSnapshot),
            ClearedPendingExportCount = activity.ClearedPendingExportCount,
            ClearedConnectedSystemObjectCount = activity.ClearedConnectedSystemObjectCount,
            ClearedJoinRecordCount = activity.ClearedJoinRecordCount,
            ExportCreatesWithheld = activity.ExportCreatesWithheld,
            ExportUpdatesWithheld = activity.ExportUpdatesWithheld,
            ExportDeletesWithheld = activity.ExportDeletesWithheld,
            DetectedDeletionsWithheld = activity.DetectedDeletionsWithheld,

            // Security audit events; an aggregated failure stores "no API key" as "" so its index can dedupe
            SecurityEventReason = activity.SecurityEventReason,
            ClientIpAddress = activity.ClientIpAddress,
            ApiKeyPrefix = string.IsNullOrEmpty(activity.ApiKeyPrefix) ? null : activity.ApiKeyPrefix,
            AttemptCount = activity.AttemptCount,
            FirstSeen = activity.FirstSeen,
            LastSeen = activity.LastSeen,
            AggregationWindowStart = activity.AggregationWindowStart
        };
    }
}

/// <summary>
/// DTO for Run Profile execution statistics with granular change type counts.
/// </summary>
public class ActivityRunProfileExecutionStatsDto
{
    #region Shared Stats
    /// <summary>
    /// Total number of objects that were in scope for processing.
    /// </summary>
    public int TotalObjectsProcessed { get; set; }

    /// <summary>
    /// Total number of object changes (Run Profile Execution Items created).
    /// </summary>
    public int TotalObjectChangeCount { get; set; }

    /// <summary>
    /// Number of objects that were unchanged.
    /// Calculated as TotalObjectsProcessed - TotalObjectChangeCount.
    /// </summary>
    public int TotalUnchanged { get; set; }

    /// <summary>
    /// Number of objects with errors.
    /// </summary>
    public int TotalObjectErrors { get; set; }

    /// <summary>
    /// Number of distinct object types affected.
    /// </summary>
    public int TotalObjectTypes { get; set; }
    #endregion

    #region Import Stats (CSO operations)
    /// <summary>
    /// Number of Connected System Objects added to the Connector Space during import.
    /// </summary>
    public int TotalCsoAdds { get; set; }

    /// <summary>
    /// Number of existing Connected System Objects updated during import.
    /// </summary>
    public int TotalCsoUpdates { get; set; }

    /// <summary>
    /// Number of Connected System Objects marked as deleted.
    /// </summary>
    public int TotalCsoDeletes { get; set; }
    #endregion

    #region Sync Stats (MVO operations)
    /// <summary>
    /// Number of new Metaverse Objects created via projection.
    /// </summary>
    public int TotalProjections { get; set; }

    /// <summary>
    /// Number of Connected System Objects joined to existing Metaverse Objects.
    /// </summary>
    public int TotalJoins { get; set; }

    /// <summary>
    /// Number of Attribute Flows between Connected System Objects and Metaverse Objects.
    /// </summary>
    public int TotalAttributeFlows { get; set; }

    /// <summary>
    /// Number of Connected System Objects disconnected from Metaverse Objects.
    /// </summary>
    public int TotalDisconnections { get; set; }

    /// <summary>
    /// Number of Connected System Objects disconnected from Metaverse Objects because they fell out of scope of import Synchronisation Rule scoping criteria.
    /// </summary>
    public int TotalDisconnectedOutOfScope { get; set; }

    /// <summary>
    /// Number of Connected System Objects that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined).
    /// </summary>
    public int TotalOutOfScopeRetainJoin { get; set; }

    /// <summary>
    /// Number of Connected System Objects where drift was detected and corrective Pending Exports were created.
    /// </summary>
    public int TotalDriftCorrections { get; set; }

    /// <summary>
    /// Number of Connected System Objects provisioned to target Connected Systems during synchronisation.
    /// </summary>
    public int TotalProvisioned { get; set; }

    /// <summary>
    /// Number of values JIM generated for a target attribute during synchronisation.
    /// </summary>
    /// <remarks>Unique Value Generation, #242.</remarks>
    public int TotalGeneratedValues { get; set; }

    /// <summary>
    /// Number of Run Profile Execution Items whose error type is a Unique Value Generation failure
    /// (exhausted, width exceeded, or an unresolved collision).
    /// </summary>
    public int TotalGeneratedValueFailures { get; set; }

    /// <summary>
    /// Number of generated values Collision Remediation corrected after a target rejected them as already in use.
    /// </summary>
    /// <remarks>Unique Value Generation, #242, release 4.</remarks>
    public int TotalGeneratedValuesRemediated { get; set; }

    /// <summary>
    /// Number of rejected generated values that could not safely be corrected and wait on an administrator's
    /// decision.
    /// </summary>
    /// <remarks>Unique Value Generation, #242, release 4.</remarks>
    public int TotalGeneratedValuesNeedingDecision { get; set; }

    /// <summary>
    /// Number of generated values held back because a value built from them needs another input the object has no
    /// value for yet. Not a failure: a later synchronisation generates the value once the input arrives.
    /// </summary>
    /// <remarks>Generated Value Forms, #2023.</remarks>
    public int TotalGeneratedValuesWaiting { get; set; }
    #endregion

    #region Export Stats
    /// <summary>
    /// Number of objects exported to target systems (includes both initial creation and subsequent updates).
    /// </summary>
    public int TotalExported { get; set; }

    /// <summary>
    /// Number of objects deprovisioned from target systems.
    /// </summary>
    public int TotalDeprovisioned { get; set; }
    #endregion

    #region Pending Export Stats
    /// <summary>
    /// Number of Pending Exports staged for the next export run.
    /// These are exports that were previously executed but not yet confirmed,
    /// giving operators visibility into what changes will be made to Connected Systems.
    /// </summary>
    public int TotalPendingExports { get; set; }
    #endregion

    #region Pending Export Reconciliation Stats
    /// <summary>
    /// Number of Pending Exports that were fully confirmed and deleted.
    /// The exported attribute values matched the imported values.
    /// </summary>
    public int TotalPendingExportsConfirmed { get; set; }

    /// <summary>
    /// Number of Pending Exports with unconfirmed attributes that will be retried.
    /// Some attribute values did not match; they will be re-exported on the next export run.
    /// </summary>
    public int TotalPendingExportsRetrying { get; set; }

    /// <summary>
    /// Number of Pending Exports that failed after their last retry. JIM does not attempt one again; it clears once an
    /// import shows its values in place, for example after the change is made in the Connected System directly.
    /// </summary>
    public int TotalPendingExportsFailed { get; set; }
    #endregion

    #region Direct Creation Stats
    /// <summary>
    /// Number of Metaverse Objects created directly (by example data generation or in the portal) rather than by projection.
    /// </summary>
    public int TotalCreated { get; set; }
    #endregion

    #region Container Exclusion Stats
    /// <summary>
    /// How many entries this import read from the Connected System and discarded because an excluded Container
    /// carved them out, keyed by that Container's id.
    /// </summary>
    /// <remarks>
    /// A directory cannot express "this subtree except that branch" in one search, so an exclusion is honoured by
    /// reading the entries it covers and throwing them away. Empty for every run that discarded nothing, which is
    /// every run on a Connected System carrying no exclusions. Keyed by id because the Container can be renamed
    /// or removed after the run; resolve the name through
    /// <c>GET connected-systems/{id}/partitions</c> when displaying it.
    /// </remarks>
    public Dictionary<int, int> EntriesDiscardedByExcludedContainer { get; set; } = new();

    /// <summary>
    /// Entries discarded across every exclusion, which is the figure that answers "why was this import slow?".
    /// </summary>
    public int TotalEntriesDiscardedByExclusion { get; set; }
    #endregion

    /// <summary>
    /// Creates a DTO from the stats entity.
    /// </summary>
    public static ActivityRunProfileExecutionStatsDto FromEntity(ActivityRunProfileExecutionStats stats)
    {
        return new ActivityRunProfileExecutionStatsDto
        {
            // Shared
            TotalObjectsProcessed = stats.TotalObjectsProcessed,
            TotalObjectChangeCount = stats.TotalObjectChangeCount,
            TotalUnchanged = stats.TotalUnchanged,
            TotalObjectErrors = stats.TotalObjectErrors,
            TotalObjectTypes = stats.TotalObjectTypes,

            // Import
            TotalCsoAdds = stats.TotalCsoAdds,
            TotalCsoUpdates = stats.TotalCsoUpdates,
            TotalCsoDeletes = stats.TotalCsoDeletes,

            // Sync
            TotalProjections = stats.TotalProjections,
            TotalJoins = stats.TotalJoins,
            TotalAttributeFlows = stats.TotalAttributeFlows,
            TotalDisconnections = stats.TotalDisconnections,
            TotalDisconnectedOutOfScope = stats.TotalDisconnectedOutOfScope,
            TotalOutOfScopeRetainJoin = stats.TotalOutOfScopeRetainJoin,
            TotalDriftCorrections = stats.TotalDriftCorrections,
            TotalProvisioned = stats.TotalProvisioned,
            TotalGeneratedValues = stats.TotalGeneratedValues,
            TotalGeneratedValueFailures = stats.TotalGeneratedValueFailures,
            TotalGeneratedValuesRemediated = stats.TotalGeneratedValuesRemediated,
            TotalGeneratedValuesNeedingDecision = stats.TotalGeneratedValuesNeedingDecision,
            TotalGeneratedValuesWaiting = stats.TotalGeneratedValuesWaiting,

            // Export
            TotalExported = stats.TotalExported,
            TotalDeprovisioned = stats.TotalDeprovisioned,

            // Pending Exports
            TotalPendingExports = stats.TotalPendingExports,

            // Pending Export Reconciliation
            TotalPendingExportsConfirmed = stats.TotalPendingExportsConfirmed,
            TotalPendingExportsRetrying = stats.TotalPendingExportsRetrying,
            TotalPendingExportsFailed = stats.TotalPendingExportsFailed,

            // Direct Creation
            TotalCreated = stats.TotalCreated,

            // Container exclusions
            EntriesDiscardedByExcludedContainer = stats.EntriesDiscardedByExcludedContainer,
            TotalEntriesDiscardedByExclusion = stats.TotalEntriesDiscardedByExclusion
        };
    }
}
