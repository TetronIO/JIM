// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Staging;
namespace JIM.Models.Activities;

/// <summary>
/// Enables all activities being performed in JIM, whether user or system initiated to be tracked and logged.
/// This enables areas of JIM to filter the activities view to the relevant objects, i.e. to view all sync runs being
/// run or about to be run, then the relevant page can filter for
/// those activities, and the same for say Metaverse Object updates to see when a group membership was updated, or a
/// user created, or Synchronisation Rules changed, etc.
/// </summary>
public class Activity
{
    /// <summary>
    /// The <see cref="TargetName"/> prefix used by Attribute Flow mapping activities (which carry
    /// <see cref="ActivityTargetType.SynchronisationRule"/> as a mapping has no target type of its own). Shared so the UI can
    /// recognise a mapping activity and deep-link its target to the rule's Attribute Flow tab.
    /// </summary>
    public const string SyncRuleMappingTargetNamePrefix = "Mapping to ";

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
    /// Connector-level warning message describing a non-fatal operational note about the activity.
    /// For example, when a delta import falls back to a full import because the watermark was unavailable.
    /// This is displayed on the activity detail page and causes the activity to complete with warning status.
    /// Unlike ErrorMessage, this does not indicate a failure — the activity still succeeded, just with caveats.
    /// </summary>
    public string? WarningMessage { get; set; }

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
    /// For an ObjectMatchingRule activity, this would contain the Synchronisation Rule name.
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
    // granular summary stats for Run Profile executions (for list view display)
    // populated when activity completes by CalculateActivitySummaryStats() in Worker.cs
    // -----------------------------------------------------------------------------------------------------------------

    #region Import Stats
    /// <summary>Count of new CSOs added to the connector space during import.</summary>
    public int TotalAdded { get; set; }

    /// <summary>Count of existing CSOs updated during import.</summary>
    public int TotalUpdated { get; set; }

    /// <summary>Count of CSOs marked as deleted (source system deletion detected).</summary>
    public int TotalDeleted { get; set; }
    #endregion

    #region Sync Stats
    /// <summary>Count of new MVOs created via projection.</summary>
    public int TotalProjected { get; set; }

    /// <summary>Count of CSOs joined to existing MVOs.</summary>
    public int TotalJoined { get; set; }

    /// <summary>
    /// Count of Attribute Flow operations. Includes both standalone Attribute Flows
    /// (to already-joined CSOs) and absorbed flows that occurred alongside joins, projections,
    /// or disconnections (tracked via RPEI.AttributeFlowCount).
    /// </summary>
    public int TotalAttributeFlows { get; set; }

    /// <summary>Count of CSOs disconnected from MVOs.</summary>
    public int TotalDisconnected { get; set; }

    /// <summary>Count of CSOs disconnected because they fell out of scope of import Synchronisation Rule scoping criteria.</summary>
    public int TotalDisconnectedOutOfScope { get; set; }

    /// <summary>Count of CSOs that fell out of scope but remained joined (InboundOutOfScopeAction = RemainJoined).</summary>
    public int TotalOutOfScopeRetainJoin { get; set; }

    /// <summary>Count of CSOs where drift was detected and corrective Pending Exports were created.</summary>
    public int TotalDriftCorrections { get; set; }

    /// <summary>Count of CSOs provisioned to target Connected Systems during sync.</summary>
    public int TotalProvisioned { get; set; }
    #endregion

    #region Export Stats
    /// <summary>Count of objects exported to target systems (includes both initial creation and subsequent updates).</summary>
    public int TotalExported { get; set; }

    /// <summary>Count of objects deprovisioned from target systems.</summary>
    public int TotalDeprovisioned { get; set; }
    #endregion

    #region Direct Creation Stats
    /// <summary>Count of MVOs created directly (data generation, admin UI) rather than via projection/sync.</summary>
    public int TotalCreated { get; set; }
    #endregion

    #region Pending Export Stats
    /// <summary>Count of Pending Exports staged for the next export run (surfaced during sync).</summary>
    public int TotalPendingExports { get; set; }
    #endregion

    #region Shared Stats
    /// <summary>Count of RPEIs with errors. Populated when activity completes.</summary>
    public int TotalErrors { get; set; }
    #endregion

    // -----------------------------------------------------------------------------------------------------------------
    // Pending Export reconciliation stats (for confirming imports)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Count of Pending Exports that were fully confirmed and deleted during a confirming import.
    /// The exported attribute values matched the imported values.
    /// </summary>
    public int PendingExportsConfirmed { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // security audit event fields (for Authentication activities: interactive sign-in and API key authentication)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The client IP address the request originated from, populated for Authentication activities (and harmless to
    /// leave null elsewhere). Truthful behind a reverse proxy only once ForwardedHeaders middleware is configured to
    /// rewrite <c>HttpContext.Connection.RemoteIpAddress</c>. An IP address is personal data; retention is governed
    /// by <see cref="JIM.Models.Core.Constants.SettingKeys.SecurityEventRetentionPeriod"/> for this target type.
    /// </summary>
    [MaxLength(45)]
    public string? ClientIpAddress { get; set; }

    /// <summary>
    /// The <c>jim_ak_XXXX</c> prefix of the API key involved in an authentication event. Never the full key or its
    /// hash. Null for interactive sign-in events. For aggregated failure rows, a null (unknown) prefix is normalised
    /// to an empty string so the aggregation-window unique index dedupes consistently (Postgres unique indexes treat
    /// NULLs as distinct from one another, which would otherwise defeat the aggregation upsert for the bad-format
    /// failure path, where no prefix is available).
    /// </summary>
    [MaxLength(16)]
    public string? ApiKeyPrefix { get; set; }

    /// <summary>
    /// The reason an authentication attempt failed (e.g. "Invalid API key format", "API key not found", "API key is
    /// disabled", "API key has expired", "Authentication error", or a short sanitised OIDC failure category). Null
    /// for successful sign-in events.
    /// </summary>
    [MaxLength(200)]
    public string? SecurityEventReason { get; set; }

    /// <summary>
    /// For aggregated failed-authentication Activities: the floor of the 15-minute UTC window this row aggregates.
    /// Null for non-aggregated Authentication activities (e.g. sign-in success) and all other target types. Part of
    /// the partial unique index that makes the aggregation upsert race-safe.
    /// </summary>
    public DateTime? AggregationWindowStart { get; set; }

    /// <summary>
    /// For aggregated failed-authentication Activities: the timestamp of the first attempt recorded in this window.
    /// Fixed at creation; never updated by subsequent increments.
    /// </summary>
    public DateTime? FirstSeen { get; set; }

    /// <summary>
    /// For aggregated failed-authentication Activities: the timestamp of the most recent attempt recorded in this
    /// window. Advances on every increment.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// For aggregated failed-authentication Activities: the number of failed attempts matching this
    /// (API key prefix, client IP, failure reason, window) combination. Null for non-aggregated activities.
    /// </summary>
    public int? AttemptCount { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // context specific properties
    // -----------------------------------------------------------------------------------------------------------------

    public int? ExampleDataTemplateId { get; set; }

    public int? ConnectedSystemId { get; set; }

    public int? SyncRuleId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Schedule, the Schedule's id is recorded here. Schedules are
    /// Guid-keyed (unlike the integer-keyed Connected System and Synchronisation Rule), so the configuration-change
    /// version query keys off this column for Schedule history. Null for all other activities.
    /// </summary>
    public Guid? ScheduleId { get; set; }

    public Guid? MetaverseObjectId { get; set; }

    // The remaining configuration target columns below follow the ScheduleId precedent: plain scalar columns with no
    // foreign-key constraint or navigation, so an object's versioned change activities (and their deep-links) survive
    // the object's later deletion. Each is null for all activities except configuration changes to that type.
    // Activity.SetConfigurationTargetId maps a target type to its column; the repository's ConfigurationChangeQuery
    // performs the reverse mapping for retrieval. Keep the two in step.

    /// <summary>
    /// If this activity records a configuration change to a Service Setting, the setting's key (its string primary
    /// key, e.g. "History.RetentionPeriod") is recorded here.
    /// </summary>
    [MaxLength(100)]
    public string? ServiceSettingKey { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Metaverse Attribute, its id is recorded here.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Metaverse Object Type, its id is recorded here.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Trusted Certificate, its id is recorded here.
    /// </summary>
    public Guid? TrustedCertificateId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to an API Key, its id is recorded here.
    /// </summary>
    public Guid? ApiKeyId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Role (its definition or its membership), the Role's id
    /// is recorded here.
    /// </summary>
    public int? RoleId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Predefined Search (including its criteria groups and
    /// criteria, which roll up into the owning search's history), the search's id is recorded here.
    /// </summary>
    public int? PredefinedSearchId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to a Connector Definition, its id is recorded here.
    /// </summary>
    public int? ConnectorDefinitionId { get; set; }

    /// <summary>
    /// If this activity records a configuration change to an Example Data Set, its id is recorded here.
    /// </summary>
    public int? ExampleDataSetId { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Run Profile execution related...

    /// <summary>
    /// The run-profile that was created, updated, deleted or executed.
    /// </summary>
    public int? ConnectedSystemRunProfileId { get; set; }

    /// <summary>
    /// If the Run Profile has been deleted, the type of sync run this was can be accessed here still.
    /// </summary>
    public ConnectedSystemRunType? ConnectedSystemRunType { get; set; } = Staging.ConnectedSystemRunType.NotSet;

    // -----------------------------------------------------------------------------------------------------------------
    // schedule execution context
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// If this activity was created as part of a schedule execution, the execution ID is recorded here.
    /// This enables the scheduler to query activity outcomes directly (since worker tasks are ephemeral
    /// and deleted upon completion) and supports drill-down from schedule execution steps to activities.
    /// </summary>
    public Guid? ScheduleExecutionId { get; set; }

    /// <summary>
    /// The step index within the schedule execution that this activity corresponds to.
    /// Used together with ScheduleExecutionId to identify which step produced this activity.
    /// </summary>
    public int? ScheduleStepIndex { get; set; }

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

    // -----------------------------------------------------------------------------------------------------------------
    // clear Connected System stats (for ClearConnectedSystem activities)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// For ClearConnectedSystem activities: Count of Pending Exports removed.
    /// </summary>
    public int? ClearedPendingExportCount { get; set; }

    /// <summary>
    /// For ClearConnectedSystem activities: Count of Connected System Objects removed.
    /// </summary>
    public int? ClearedConnectedSystemObjectCount { get; set; }

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
    // configuration object changes (create/update/delete)
    // Applies to configuration objects such as Synchronisation Rules and Connected Systems. For a configuration-change
    // activity, a complete, redaction-aware, versioned snapshot of the object's state is captured here as a JSON (jsonb)
    // document. These columns are null for the high-volume sync/identity activities, so the common path is unaffected.
    // Sensitive values (e.g. encrypted Connected System settings) are never stored; a keyed hash is recorded instead so
    // a credential change can be shown without disclosing the value. See ConfigurationSnapshotService.
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The scoped, redacted configuration snapshot for this activity, stored as a JSON (jsonb) document.
    /// Null for non-configuration activities. Deserialises to <see cref="ConfigurationSnapshot"/>.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? ConfigurationChangeSnapshot { get; set; }

    /// <summary>
    /// An optional, free-text reason supplied by the initiator describing why the change was made (the change's
    /// "commit message"). Kept general: any activity may carry a reason. Null when not supplied.
    /// </summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// The per-object configuration-change version number (v1, v2, ...). Assigned as (max existing version for the
    /// object) + 1 at capture time, so it does not renumber when older entries are removed by retention.
    /// Null for non-configuration activities.
    /// </summary>
    public int? ConfigurationChangeVersion { get; set; }

    /// <summary>
    /// Records which integer-keyed configuration object this activity's configuration change belongs to, by setting
    /// the target type's own column (see the target-column block above). A column already set at activity-creation
    /// time (e.g. by a granular sub-entity endpoint) is preserved. This is the single forward mapping of target type
    /// to column; the repository's ConfigurationChangeQuery is the reverse mapping. Keep the two in step.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target type is not an integer-keyed configuration type.</exception>
    public void SetConfigurationTargetId(ActivityTargetType targetType, int targetObjectId)
    {
        switch (targetType)
        {
            case ActivityTargetType.ConnectedSystem: ConnectedSystemId ??= targetObjectId; break;
            case ActivityTargetType.SynchronisationRule: SyncRuleId ??= targetObjectId; break;
            case ActivityTargetType.MetaverseAttribute: MetaverseAttributeId ??= targetObjectId; break;
            case ActivityTargetType.MetaverseObjectType: MetaverseObjectTypeId ??= targetObjectId; break;
            case ActivityTargetType.PredefinedSearch: PredefinedSearchId ??= targetObjectId; break;
            case ActivityTargetType.Role: RoleId ??= targetObjectId; break;
            case ActivityTargetType.ConnectorDefinition: ConnectorDefinitionId ??= targetObjectId; break;
            case ActivityTargetType.ExampleDataTemplate: ExampleDataTemplateId ??= targetObjectId; break;
            case ActivityTargetType.ExampleDataSet: ExampleDataSetId ??= targetObjectId; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
                    "Not an integer-keyed configuration target type.");
        }
    }

    /// <summary>
    /// Guid-keyed counterpart of <see cref="SetConfigurationTargetId(ActivityTargetType,int)"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target type is not a Guid-keyed configuration type.</exception>
    public void SetConfigurationTargetId(ActivityTargetType targetType, Guid targetObjectId)
    {
        switch (targetType)
        {
            case ActivityTargetType.Schedule: ScheduleId ??= targetObjectId; break;
            case ActivityTargetType.TrustedCertificate: TrustedCertificateId ??= targetObjectId; break;
            case ActivityTargetType.ApiKey: ApiKeyId ??= targetObjectId; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
                    "Not a Guid-keyed configuration target type.");
        }
    }

    /// <summary>
    /// String-keyed counterpart of <see cref="SetConfigurationTargetId(ActivityTargetType,int)"/>, for configuration
    /// objects whose primary key is a string (Service Settings, keyed by their setting key).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target type is not a string-keyed configuration type.</exception>
    public void SetConfigurationTargetId(ActivityTargetType targetType, string targetObjectKey)
    {
        switch (targetType)
        {
            case ActivityTargetType.ServiceSetting: ServiceSettingKey ??= targetObjectKey; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
                    "Not a string-keyed configuration target type.");
        }
    }

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
