// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Security;
using JIM.Models.Utility;
using Serilog;
namespace JIM.Application.Servers;

public class ActivityServer
{
    private JimApplication Application { get; }

    internal ActivityServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Creates and persists an Activity, attributing it to a user (MetaverseObject).
    /// All activities MUST be attributed to a security principal for audit compliance.
    /// </summary>
    /// <param name="activity">The Activity to create.</param>
    /// <param name="initiatedBy">The MetaverseObject representing the user who initiated the action. Can be null for system actions.</param>
    public async Task CreateActivityAsync(Activity activity, MetaverseObject? initiatedBy)
    {
        activity.Status = ActivityStatus.InProgress;
        activity.Executed = DateTime.UtcNow;

        if (initiatedBy != null)
        {
            activity.InitiatedByType = ActivityInitiatorType.User;
            activity.InitiatedById = initiatedBy.Id;
            activity.InitiatedByName = initiatedBy.DisplayName;
        }

        ValidateActivity(activity);
        await Application.Repository.Activity.CreateActivityAsync(activity);
    }

    /// <summary>
    /// Creates and persists an Activity, attributing it to an API key.
    /// All activities MUST be attributed to a security principal for audit compliance.
    /// </summary>
    /// <param name="activity">The Activity to create.</param>
    /// <param name="initiatedByApiKey">The ApiKey that initiated the action.</param>
    public async Task CreateActivityAsync(Activity activity, ApiKey initiatedByApiKey)
    {
        ArgumentNullException.ThrowIfNull(initiatedByApiKey);

        activity.Status = ActivityStatus.InProgress;
        activity.Executed = DateTime.UtcNow;
        activity.InitiatedByType = ActivityInitiatorType.ApiKey;
        activity.InitiatedById = initiatedByApiKey.Id;
        activity.InitiatedByName = initiatedByApiKey.Name;

        ValidateActivity(activity);
        await Application.Repository.Activity.CreateActivityAsync(activity);
    }

    /// <summary>
    /// Creates and persists a system-initiated Activity (seeding, scheduled maintenance, housekeeping).
    /// All activities MUST be attributed to a security principal for audit compliance.
    /// </summary>
    /// <param name="activity">The Activity to create.</param>
    public async Task CreateSystemActivityAsync(Activity activity)
    {
        activity.Status = ActivityStatus.InProgress;
        activity.Executed = DateTime.UtcNow;
        activity.InitiatedByType = ActivityInitiatorType.System;
        activity.InitiatedByName = "System";

        ValidateActivity(activity);
        await Application.Repository.Activity.CreateActivityAsync(activity);
    }

    /// <summary>
    /// Returns the next per-object configuration-change version (current maximum + 1) for a configuration object,
    /// identified by its activity target type and database id.
    /// </summary>
    public async Task<int> GetNextConfigurationChangeVersionAsync(ActivityTargetType targetType, int targetObjectId)
    {
        var max = await Application.Repository.Activity.GetMaxConfigurationChangeVersionAsync(targetType, targetObjectId);
        return max + 1;
    }

    /// <summary>
    /// Returns the next per-object configuration-change version (current maximum + 1) for a Guid-keyed configuration
    /// object (e.g. a Schedule), identified by its activity target type and Guid database id.
    /// </summary>
    public async Task<int> GetNextConfigurationChangeVersionAsync(ActivityTargetType targetType, Guid targetObjectId)
    {
        var max = await Application.Repository.Activity.GetMaxConfigurationChangeVersionAsync(targetType, targetObjectId);
        return max + 1;
    }

    /// <summary>
    /// Returns the snapshot JSON of the latest configuration-change version recorded for a configuration object, or
    /// null if none exists yet. Used by the idempotent capture guard to skip no-change captures.
    /// </summary>
    public async Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, int targetObjectId)
    {
        return await Application.Repository.Activity.GetLatestConfigurationChangeSnapshotAsync(targetType, targetObjectId);
    }

    /// <summary>
    /// Returns the snapshot JSON of the latest configuration-change version recorded for a Guid-keyed configuration
    /// object (e.g. a Schedule), or null if none exists yet.
    /// </summary>
    public async Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, Guid targetObjectId)
    {
        return await Application.Repository.Activity.GetLatestConfigurationChangeSnapshotAsync(targetType, targetObjectId);
    }

    /// <summary>
    /// Returns the next per-object configuration-change version (current maximum + 1) for a string-keyed configuration
    /// object (e.g. a Service Setting, keyed by its setting key).
    /// </summary>
    public async Task<int> GetNextConfigurationChangeVersionAsync(ActivityTargetType targetType, string targetObjectKey)
    {
        var max = await Application.Repository.Activity.GetMaxConfigurationChangeVersionAsync(targetType, targetObjectKey);
        return max + 1;
    }

    /// <summary>
    /// Returns the snapshot JSON of the latest configuration-change version recorded for a string-keyed configuration
    /// object (e.g. a Service Setting), or null if none exists yet.
    /// </summary>
    public async Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, string targetObjectKey)
    {
        return await Application.Repository.Activity.GetLatestConfigurationChangeSnapshotAsync(targetType, targetObjectKey);
    }

    /// <summary>
    /// Creates and persists an Activity using an initiator triad (Type, Id, Name).
    /// Used when the full principal object is not available (e.g., from WorkerTask).
    /// </summary>
    public async Task CreateActivityWithTriadAsync(Activity activity, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName)
    {
        activity.Status = ActivityStatus.InProgress;
        activity.Executed = DateTime.UtcNow;
        activity.InitiatedByType = initiatorType;
        activity.InitiatedById = initiatorId;
        activity.InitiatedByName = initiatorName ?? initiatorType switch
        {
            ActivityInitiatorType.System => "System",
            ActivityInitiatorType.Anonymous => "Anonymous",
            _ => "Unknown"
        };

        ValidateActivity(activity);
        await Application.Repository.Activity.CreateActivityAsync(activity);
    }

    /// <summary>
    /// Creates and persists an Activity already in its terminal Complete state, as a single insert, attributed via
    /// an explicit initiator triad. For point-in-time audit records (for example security audit events) that
    /// represent an instantaneous fact rather than a long-running operation. Such records MUST NOT use the usual
    /// create-then-complete lifecycle: completing performs a second, full-row update from the caller's in-memory
    /// Activity, which silently overwrites any concurrent in-place changes made to the row between the two writes
    /// (a lost update; SecurityAuditServer's aggregated AttemptCount increments were erased this way under a
    /// concurrent authentication spray).
    /// </summary>
    public async Task CreateCompletedActivityWithTriadAsync(Activity activity, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName)
    {
        var now = DateTime.UtcNow;
        activity.Status = ActivityStatus.Complete;
        activity.Executed = now;
        activity.ExecutionTime = TimeSpan.Zero;
        activity.TotalActivityTime = now - activity.Created;
        activity.InitiatedByType = initiatorType;
        activity.InitiatedById = initiatorId;
        activity.InitiatedByName = initiatorName ?? initiatorType switch
        {
            ActivityInitiatorType.System => "System",
            ActivityInitiatorType.Anonymous => "Anonymous",
            _ => "Unknown"
        };

        ValidateActivity(activity);
        await Application.Repository.Activity.CreateActivityAsync(activity);
    }

    private void ValidateActivity(Activity activity)
    {
        // All activities MUST be attributed to a security principal for audit compliance.
        // This is a critical requirement - no exceptions.
        if (activity.InitiatedByType == ActivityInitiatorType.NotSet)
            throw new InvalidOperationException("Activity must be attributed to a security principal. InitiatedByType has not been set.");

        // System activities have no principal entity, so InitiatedById is allowed to be null. Anonymous activities
        // (an unidentified, unauthenticated caller) likewise have no principal entity: InitiatedById MUST be null,
        // enforced below. User and ApiKey activities must have an InitiatedById.
        if (activity.InitiatedByType != ActivityInitiatorType.System
            && activity.InitiatedByType != ActivityInitiatorType.Anonymous
            && activity.InitiatedById == null)
            throw new InvalidOperationException("Activity must be attributed to a security principal. InitiatedById has not been set.");

        if (activity.InitiatedByType == ActivityInitiatorType.Anonymous && activity.InitiatedById != null)
            throw new InvalidOperationException("Activity attributed to Anonymous must not carry an InitiatedById.");

        if (string.IsNullOrWhiteSpace(activity.InitiatedByName))
            throw new InvalidOperationException("Activity must be attributed to a security principal. InitiatedByName has not been set.");

        if (activity.TargetType == ActivityTargetType.ConnectedSystem)
        {
            // ConnectedSystemId is required for UPDATE operations, but not for DELETE
            // because the Connected System will be deleted before the activity completes
            if (activity.ConnectedSystemId == null &&
                activity.TargetOperationType != ActivityTargetOperationType.Create &&
                activity.TargetOperationType != ActivityTargetOperationType.Delete)
                throw new InvalidDataException("Activity.ConnectedSystemId has not been set and must be for UPDATE operations. Cannot continue.");
        }
    }

    /// <summary>
    /// Attaches Run Profile Execution Items to an Activity that has already been persisted (via one of the
    /// CreateActivity methods on this server) and persists them, including their sync outcome trees and any
    /// Connected System Object change snapshots carried on the outcomes. Items must reference related entities
    /// (Connected System Objects, Pending Exports) by scalar foreign key only. Intended for small batches
    /// recorded outside sync task processing (for example Metaverse Object Housekeeping); sync processors use
    /// the bulk insert path on ISyncRepository instead.
    /// </summary>
    public async Task AddRunProfileExecutionItemsAsync(Activity activity, IReadOnlyCollection<ActivityRunProfileExecutionItem> items)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            if (item.Id == Guid.Empty)
                item.Id = Guid.NewGuid();
            item.Activity = activity;
            item.ActivityId = activity.Id;
            activity.RunProfileExecutionItems.Add(item);
        }

        await Application.Repository.Activity.CreateActivityRunProfileExecutionItemsAsync(items);
    }

    /// <summary>
    /// Finalises the Activity's Run Profile execution stat counters (#1078) ahead of persisting a
    /// terminal status, so completed Activities serve their stats from exact stored counters. A
    /// finalisation failure must not leave the Activity stuck InProgress: the counters stay
    /// advisory and the lazy finalise-on-first-read path repairs them, so the error is logged and
    /// completion proceeds.
    /// </summary>
    private async Task TryFinaliseRunProfileExecutionStatsAsync(Activity activity)
    {
        try
        {
            await Application.Repository.Activity.FinaliseActivityRunProfileExecutionStatsAsync(activity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "TryFinaliseRunProfileExecutionStatsAsync: Failed to finalise stat counters for Activity {ActivityId}; stats will be finalised lazily on first read", activity.Id);
        }
    }

    public async Task CompleteActivityAsync(Activity activity)
    {
        var now = DateTime.UtcNow;
        activity.Status = ActivityStatus.Complete;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task CompleteActivityWithWarningAsync(Activity activity)
    {
        var now = DateTime.UtcNow;
        activity.ExecutionTime = DateTime.UtcNow - activity.Executed;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        activity.Status = ActivityStatus.CompleteWithWarning;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task CompleteActivityWithErrorAsync(Activity activity, Exception exception)
    {
        var now = DateTime.UtcNow;
        activity.ExecutionTime = DateTime.UtcNow - activity.Executed;
        activity.ErrorMessage = exception.Message;

        // Only persist stack traces for unexpected errors (bugs), not for operational errors
        // that have clear, user-actionable messages
        if (exception is not OperationalException)
            activity.ErrorStackTrace = exception.StackTrace;

        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        activity.Status = ActivityStatus.CompleteWithError;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task CompleteActivityWithErrorAsync(Activity activity, string errorMessage)
    {
        var now = DateTime.UtcNow;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        activity.ErrorMessage = errorMessage;
        activity.Status = ActivityStatus.CompleteWithError;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task FailActivityWithErrorAsync(Activity activity, string errorMessage)
    {
        var now = DateTime.UtcNow;
        activity.ExecutionTime = DateTime.UtcNow - activity.Executed;
        activity.ErrorMessage = errorMessage;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        activity.Status = ActivityStatus.FailedWithError;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task FailActivityWithErrorAsync(Activity activity, Exception exception)
    {
        var now = DateTime.UtcNow;
        activity.ErrorMessage = GetFullExceptionMessage(exception);

        // Only persist stack traces for unexpected errors (bugs), not for operational errors
        // that have clear, user-actionable messages
        if (exception is not OperationalException)
            activity.ErrorStackTrace = exception.ToString();

        activity.Status = ActivityStatus.FailedWithError;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    /// <summary>
    /// Builds a complete error message by unwrapping inner exceptions.
    /// Many exceptions (e.g. DbUpdateException) have generic messages with details in InnerException.
    /// </summary>
    private static string GetFullExceptionMessage(Exception exception)
    {
        if (exception.InnerException == null)
            return exception.Message;

        return $"{exception.Message} --> {GetFullExceptionMessage(exception.InnerException)}";
    }

    public async Task CancelActivityAsync(Activity activity)
    {
        if (activity.Status == ActivityStatus.Cancelled)
            return;
            
        var now = DateTime.UtcNow;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        activity.Status = ActivityStatus.Cancelled;
        await TryFinaliseRunProfileExecutionStatsAsync(activity);
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task UpdateActivityAsync(Activity activity)
    {
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    /// <summary>
    /// Updates the message on an Activity.
    /// </summary>
    public async Task UpdateActivityMessageAsync(Activity activity, string message)
    {
        activity.Message = message;
        await UpdateActivityAsync(activity);
    }

    public async Task DeleteActivityAsync(Activity activity)
    {
        await Application.Repository.Activity.DeleteActivityAsync(activity);
    }

    public async Task<Activity?> GetActivityAsync(Guid id)
    {
        return await Application.Repository.Activity.GetActivityAsync(id);
    }

    /// <summary>
    /// Gets a page's worth of direct child activities for a given parent activity,
    /// ordered by creation date ascending.
    /// </summary>
    public async Task<PagedResultSet<Activity>> GetChildActivitiesAsync(Guid parentActivityId, int page, int pageSize)
    {
        return await Application.Repository.Activity.GetChildActivitiesAsync(parentActivityId, page, pageSize);
    }

    /// <summary>
    /// Returns a dictionary mapping each activity ID to its direct child activity count.
    /// IDs with no children are omitted from the result.
    /// </summary>
    public async Task<Dictionary<Guid, int>> GetChildActivityCountsAsync(IEnumerable<Guid> activityIds)
    {
        return await Application.Repository.Activity.GetChildActivityCountsAsync(activityIds);
    }

    /// <summary>
    /// Gets all activities associated with a schedule execution.
    /// Used by the scheduler to determine step outcomes after worker tasks have been deleted.
    /// </summary>
    public async Task<List<Activity>> GetActivitiesByScheduleExecutionAsync(Guid scheduleExecutionId)
    {
        return await Application.Repository.Activity.GetActivitiesByScheduleExecutionAsync(scheduleExecutionId);
    }

    /// <summary>
    /// Retrieves a page's worth of top-level activities, i.e. those that do not have a parent activity.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="searchQuery">Optional search query to filter by TargetName or TargetType.</param>
    /// <param name="sortBy">Optional column to sort by (e.g., "type", "target", "created", "status").</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true).</param>
    /// <param name="initiatedById">Optional filter to only show activities initiated by a specific user.</param>
    /// <param name="operationFilter">Optional filter for operation types (additive/OR within filter).</param>
    /// <param name="outcomeFilter">Optional filter for outcome stat types (additive/OR within filter).</param>
    /// <param name="typeFilter">Optional filter for target types (additive/OR within filter).</param>
    /// <param name="statusFilter">Optional filter for activity statuses (additive/OR within filter).</param>
    /// <param name="hasChildActivities">Optional filter: true = only activities with children, false = only without, null = all.</param>
    /// <param name="initiatorTypeFilter">Optional filter for initiator types (user / API key / system; additive/OR within filter).</param>
    /// <param name="createdFrom">Optional inclusive lower bound on the activity's Created time (UTC).</param>
    /// <param name="createdTo">Optional inclusive upper bound on the activity's Created time (UTC).</param>
    public async Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null,
        IEnumerable<ActivityTargetOperationType>? operationFilter = null,
        IEnumerable<ActivityOutcomeType>? outcomeFilter = null,
        IEnumerable<ActivityTargetType>? typeFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null,
        IEnumerable<ActivityInitiatorType>? initiatorTypeFilter = null,
        DateTime? createdFrom = null,
        DateTime? createdTo = null)
    {
        return await Application.Repository.Activity.GetActivitiesAsync(
            page, pageSize, searchQuery, sortBy, sortDescending, initiatedById,
            operationFilter, outcomeFilter, typeFilter, statusFilter, hasChildActivities,
            initiatorTypeFilter, createdFrom, createdTo);
    }

    /// <summary>
    /// Retrieves a page's worth of worker task activities (Run Profile executions, data generation, system operations).
    /// Filtered to show only activities related to worker tasks for the Operations page History tab.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="connectedSystemFilter">Optional filter for Connected System names (additive/OR within filter).</param>
    /// <param name="runProfileFilter">Optional filter for Run Profile names (additive/OR within filter).</param>
    /// <param name="statusFilter">Optional filter for activity statuses (additive/OR within filter).</param>
    /// <param name="initiatedByFilter">Optional text search on initiator name.</param>
    /// <param name="sortBy">Optional column to sort by.</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true).</param>
    public async Task<PagedResultSet<Activity>> GetWorkerTaskActivitiesAsync(
        int page = 1,
        int pageSize = 20,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        string? initiatedByFilter = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool? hasChildActivities = null)
    {
        return await Application.Repository.Activity.GetWorkerTaskActivitiesAsync(
            page, pageSize, connectedSystemFilter, runProfileFilter, statusFilter, initiatedByFilter, sortBy, sortDescending, hasChildActivities);
    }

    /// <summary>
    /// Retrieves the distinct filter options available for worker task activities.
    /// </summary>
    public async Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync()
    {
        return await Application.Repository.Activity.GetWorkerTaskActivityFilterOptionsAsync();
    }

    #region synchronisation related
    /// <summary>
    /// Retrieves a page's worth of sync execution item headers for a specific activity.
    /// Supports server-side search, sorting, and filtering by object type, error type, and outcome type.
    /// </summary>
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionItemHeadersAsync(
            activityId, page, pageSize, searchQuery, sortBy, sortDescending, objectTypeFilter, errorTypeFilter, outcomeTypeFilter);
    }

    public async Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionStatsAsync(activityId);
    }

    /// <summary>
    /// Gets a lightweight progress snapshot for an Activity (#202), or null when the Activity
    /// does not exist. See <see cref="ActivityProgress"/> for the shape.
    /// </summary>
    public async Task<ActivityProgress?> GetActivityProgressAsync(Guid activityId)
    {
        return await Application.Repository.Activity.GetActivityProgressAsync(activityId);
    }

    /// <summary>
    /// Queries the database for RPEI error counts for an activity. Returns the total number of
    /// RPEIs with errors, the total number of RPEIs, and the number of UnhandledError RPEIs,
    /// enabling precise activity completion status determination without loading RPEIs into memory.
    /// </summary>
    public async Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId)
    {
        return await Application.Repository.Activity.GetActivityRpeiErrorCountsAsync(activityId);
    }

    public async Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionItemAsync(id);
    }
    #endregion
}