// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Scheduling;
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
            activity.InitiatedByName = initiatedBy.Name;
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
        activity.ErrorDetail = ActivityErrorDetail.TryDescribe(exception);

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
    /// Gets a window of one Activity's direct child Activities addressed by absolute offset and count, for the
    /// virtualised (infinite-scroll) child-Activity grid. Ordered by creation date ascending, and shares its
    /// query core with <see cref="GetChildActivitiesAsync"/>. Pass <paramref name="includeTotalCount"/> as false
    /// to skip counting the whole match set when the caller already knows the total; the returned total is then
    /// null rather than zero.
    /// </summary>
    /// <param name="parentActivityId">The parent Activity whose direct children are wanted.</param>
    /// <param name="offset">The zero-based index of the first child wanted; negative values read as zero.</param>
    /// <param name="count">How many children are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window.</param>
    public async Task<RangeResultSet<Activity>> GetChildActivitiesRangeAsync(
        Guid parentActivityId,
        int offset,
        int count,
        string? searchQuery = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.Activity.GetChildActivitiesRangeAsync(
            parentActivityId, offset, count, searchQuery, includeTotalCount);
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
    /// What each Schedule Execution's tasks have left behind, keyed by execution (#1162), for views
    /// that show a Schedule mid-flight. A step's Worker Task is deleted the moment it finishes, so its
    /// Activity is the only remaining evidence that the step ran and how it ended.
    /// </summary>
    public async Task<Dictionary<Guid, List<ScheduleStepObservation>>> GetScheduleStepOutcomesAsync(IReadOnlyCollection<Guid> scheduleExecutionIds)
    {
        return await Application.Repository.Activity.GetScheduleStepOutcomesAsync(scheduleExecutionIds);
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
    /// <param name="connectedSystemFilter">Optional filter for Connected System names, matched against the activity's target context (additive/OR within filter).</param>
    /// <param name="runProfileFilter">Optional filter for Run Profile names, matched against the activity's target name (additive/OR within filter).</param>
    /// <param name="initiatedByFilter">Optional case-insensitive partial match on the initiator's name; distinct from <paramref name="initiatedById"/>, which matches an exact principal.</param>
    /// <param name="initiatedBySchedule">Optional filter: true = only activities a Schedule produced, false = only those no Schedule produced, null = all.</param>
    /// <param name="scheduleFilter">Optional filter for the ids of the Schedules that produced the activities (additive/OR within filter).</param>
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
        DateTime? createdTo = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null)
    {
        return await Application.Repository.Activity.GetActivitiesAsync(
            page, pageSize, searchQuery, sortBy, sortDescending, initiatedById,
            operationFilter, outcomeFilter, typeFilter, statusFilter, hasChildActivities,
            initiatorTypeFilter, createdFrom, createdTo,
            connectedSystemFilter, runProfileFilter, initiatedByFilter, initiatedBySchedule, scheduleFilter);
    }

    /// <summary>
    /// Retrieves a window of top-level Activities addressed by absolute offset and count, for virtualised
    /// (infinite-scroll) list views. Takes the same filters as <see cref="GetActivitiesAsync"/> and shares its
    /// query core. Pass <paramref name="includeTotalCount"/> as false to skip counting the whole match set when
    /// the caller already knows the total; the returned total is then null rather than zero.
    /// </summary>
    /// <param name="startIndex">The zero-based index of the first Activity wanted; negative values read as zero.</param>
    /// <param name="count">How many Activities are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional search query to filter by target name, target context or initiator name.</param>
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
    /// <param name="connectedSystemFilter">Optional filter for Connected System names, matched against the activity's target context (additive/OR within filter).</param>
    /// <param name="runProfileFilter">Optional filter for Run Profile names, matched against the activity's target name (additive/OR within filter).</param>
    /// <param name="initiatedByFilter">Optional case-insensitive partial match on the initiator's name; distinct from <paramref name="initiatedById"/>, which matches an exact principal.</param>
    /// <param name="initiatedBySchedule">Optional filter: true = only activities a Schedule produced, false = only those no Schedule produced, null = all.</param>
    /// <param name="scheduleFilter">Optional filter for the ids of the Schedules that produced the activities (additive/OR within filter).</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<Activity>> GetActivitiesRangeAsync(
        int startIndex,
        int count,
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
        DateTime? createdTo = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.Activity.GetActivitiesRangeAsync(
            startIndex, count, searchQuery, sortBy, sortDescending, initiatedById,
            operationFilter, outcomeFilter, typeFilter, statusFilter, hasChildActivities,
            initiatorTypeFilter, createdFrom, createdTo,
            connectedSystemFilter, runProfileFilter, initiatedByFilter, initiatedBySchedule, scheduleFilter,
            includeTotalCount);
    }

    /// <summary>
    /// Retrieves the distinct filter options available for worker task activities.
    /// </summary>
    public async Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync()
    {
        return await Application.Repository.Activity.GetWorkerTaskActivityFilterOptionsAsync();
    }

    /// <summary>
    /// Whether any Run Profile has ever been executed, in any state. Backs the home page's "Run your first
    /// synchronisation" setup step: an administrator who ran a Run Profile by hand has run their first
    /// synchronisation, whether or not a Schedule has ever fired.
    /// </summary>
    public async Task<bool> HasAnyRunProfileExecutionAsync()
    {
        return await Application.Repository.Activity.HasAnyRunProfileExecutionAsync();
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

    /// <summary>
    /// Retrieves a window of one Activity's Run Profile Execution Item headers addressed by absolute offset and
    /// count, for the virtualised (infinite-scroll) execution-item grid. Takes the same search, sort and filters
    /// as <see cref="GetActivityRunProfileExecutionItemHeadersAsync"/> and shares its query core. Pass
    /// <paramref name="includeTotalCount"/> as false to skip counting the whole match set when the caller
    /// already knows the total; the returned total is then null rather than zero.
    /// </summary>
    /// <param name="activityId">The Activity whose execution items are wanted.</param>
    /// <param name="offset">The zero-based index of the first item wanted; negative values read as zero.</param>
    /// <param name="count">How many items are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional case-insensitive search over the display name and external ID.</param>
    /// <param name="sortBy">Optional sort key: "externalid", "displayname"/"name", "type"/"objecttype",
    /// "errortype", or the item's id (the default).</param>
    /// <param name="sortDescending">Whether the sort is descending.</param>
    /// <param name="objectTypeFilter">Optional filter for Connected System Object Type names (additive/OR).</param>
    /// <param name="errorTypeFilter">Optional filter for error types (additive/OR).</param>
    /// <param name="outcomeTypeFilter">Optional filter for sync outcome types (additive/OR).</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersRangeAsync(
        Guid activityId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null,
        bool includeTotalCount = true)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionItemHeadersRangeAsync(
            activityId, offset, count, searchQuery, sortBy, sortDescending,
            objectTypeFilter, errorTypeFilter, outcomeTypeFilter, includeTotalCount);
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
    /// Gets the recorded phases of a Run Profile execution (#454), in run order: what each step
    /// was, how it turned out and how long it took. Empty for Activities that are not Run Profile
    /// executions, and for runs that predate phase recording.
    /// </summary>
    public async Task<List<ActivityPhase>> GetActivityPhasesAsync(Guid activityId)
    {
        return await Application.Repository.Activity.GetActivityPhasesAsync(activityId);
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

    /// <summary>
    /// Walks upward from a Run Profile Execution Item through the causal edges recorded against it, returning
    /// what caused the changes it describes as a tree of cohorts (#1223).
    /// </summary>
    /// <param name="runProfileExecutionItemId">The item to explain.</param>
    /// <param name="maxDepth">How many levels to follow. Bounded because a cascade can chain arbitrarily far,
    /// and hitting the bound is reported rather than presented as the end of the story.</param>
    /// <remarks>
    /// Two properties matter more than the shape of the result.
    ///
    /// <b>It costs one round trip per level, not one per cause.</b> Each level resolves every branch at once:
    /// a cohort can hold thousands of members, and a walk that queried per member would issue thousands of
    /// round trips to render one panel.
    ///
    /// <b>Every ending is named.</b> An unresolvable ancestor produces an explicit
    /// <see cref="CausalChainResolution"/>, never a gap and never an exception. "Nothing caused this" and "the
    /// cause is no longer retained" are indistinguishable as an absence and mean opposite things to the person
    /// reading; presenting the second as the first would tell them they had the whole story when the chain had
    /// actually been cut short by retention.
    /// </remarks>
    public async Task<CausalChain> GetCausalChainAsync(Guid runProfileExecutionItemId, int maxDepth = 5)
    {
        var rootEdges = await Application.Repository.Activity
            .GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync([runProfileExecutionItemId]);

        var cohorts = GroupIntoCohorts(rootEdges);

        // The viewed item's own timeline continues behind it too: a synchronisation item opened directly gets
        // its source-import hop at the root, exactly as it would nested under an export's chain.
        var rootSummaries = await Application.Repository.Activity
            .GetRunProfileExecutionItemCausalSummariesAsync([runProfileExecutionItemId]);
        if (rootSummaries.TryGetValue(runProfileExecutionItemId, out var rootSummary))
        {
            var rootSourceHop = await TryBuildSourceImportCohortAsync(rootSummary);
            if (rootSourceHop != null)
                cohorts.Add(rootSourceHop);
        }

        if (cohorts.Count == 0)
            return new CausalChain { RunProfileExecutionItemId = runProfileExecutionItemId };

        var truncatedByDepth = await ExpandLevelsAsync(cohorts, maxDepth);

        return new CausalChain
        {
            RunProfileExecutionItemId = runProfileExecutionItemId,
            Cohorts = cohorts,
            IsTruncatedByDepth = truncatedByDepth
        };
    }

    /// <summary>
    /// Resolves each member of <paramref name="cohorts"/> level by level, breadth-first, until the depth bound
    /// or until no member has anywhere further to go. Returns whether any branch stopped at the bound.
    /// </summary>
    private async Task<bool> ExpandLevelsAsync(List<CausalChainCohort> cohorts, int maxDepth)
    {
        // The frontier is every member at the current level that names a causing item. Members that name none
        // are already terminal and never enter it.
        var frontier = cohorts.SelectMany(c => c.Members)
            .Where(m => m.RunProfileExecutionItemId.HasValue)
            .ToList();

        for (var depth = 1; frontier.Count > 0; depth++)
        {
            if (depth >= maxDepth)
            {
                foreach (var member in frontier)
                    member.Resolution = CausalChainResolution.DepthLimitReached;

                return true;
            }

            // Distinct, because a cohort of many members routinely points at one causing item, and a level of
            // a large cascade would otherwise ask about the same item thousands of times.
            var causeItemIds = frontier.Select(m => m.RunProfileExecutionItemId!.Value).Distinct().ToList();
            var summaries = await Application.Repository.Activity.GetRunProfileExecutionItemCausalSummariesAsync(causeItemIds);
            var retainedIds = causeItemIds.Where(summaries.ContainsKey).ToList();

            var edgesByEffect = retainedIds.Count > 0
                ? (await Application.Repository.Activity.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(retainedIds))
                    .GroupBy(e => e.EffectRunProfileExecutionItemId)
                    .ToDictionary(g => g.Key, g => g.ToList())
                : [];

            var nextFrontier = new List<CausalChainMember>();
            foreach (var member in frontier)
            {
                var causeItemId = member.RunProfileExecutionItemId!.Value;

                if (!summaries.TryGetValue(causeItemId, out var summary))
                {
                    // Expected rather than exceptional: causes are always older than their effects, so past
                    // one retention window this is the normal end of a long chain.
                    member.Resolution = CausalChainResolution.CauseNotRetained;
                    continue;
                }

                var memberCohorts = edgesByEffect.TryGetValue(causeItemId, out var edges)
                    ? GroupIntoCohorts(edges)
                    : [];

                // The record's own timeline continues behind a synchronisation item to the import that fed
                // it, whether or not the item also carries edges: the source data's arrival is a cause in its
                // own right, and it is the hop that reaches the chain's true root.
                var sourceHop = await TryBuildSourceImportCohortAsync(summary);
                if (sourceHop != null)
                    memberCohorts.Add(sourceHop);

                if (memberCohorts.Count == 0)
                {
                    // A synchronisation-side item was fed by an import by definition, so where its record's
                    // deletion has nulled the id the timeline is walked on and the snapshot fallback reached
                    // no import either, the story was cut short rather than completed: report the loss, as
                    // the not-retained ending, instead of presenting it as the whole story. An import-side
                    // item, and a synchronisation whose record is still live with no retained import, keep
                    // the complete-story ending they always had (#1495).
                    member.Resolution = IsTimelineSevered(summary)
                        ? CausalChainResolution.CauseNotRetained
                        : CausalChainResolution.NoFurtherCauses;
                    continue;
                }

                member.Resolution = CausalChainResolution.Resolved;
                member.Causes.AddRange(memberCohorts);
                nextFrontier.AddRange(member.Causes.SelectMany(c => c.Members)
                    .Where(m => m.RunProfileExecutionItemId.HasValue));
            }

            frontier = nextFrontier;
        }

        return false;
    }

    /// <summary>
    /// Groups edges into cohorts on the attribution tuple, plus the effect outcome they explain.
    /// </summary>
    /// <remarks>
    /// The effect outcome is part of the key, not incidental: an item carrying several outcomes has several
    /// independent stories, and merging their causes would attribute a change on one outcome to a cause
    /// belonging to another. This is the PRD's edge-granularity resolution, applied.
    ///
    /// The reason element is the code, never the rendered sentence. Grouping on prose would be redundant with
    /// the Connected System name the tuple already carries, and would change behaviour silently whenever the
    /// wording changed.
    /// </remarks>
    /// <summary>
    /// The Run Profile Execution Item types behind which a record's own timeline continues to the import that
    /// fed it: a synchronisation processed the Connected System Object, so whatever import last changed that
    /// object is where its data (or its disappearance) came from. Import-side items are themselves the far
    /// end, and a <see cref="ObjectChangeType.PendingExport"/> staging item's causes are its recorded edges,
    /// not its object's timeline: its Connected System Object is the effect's, never the cause's.
    /// </summary>
    private static readonly HashSet<ObjectChangeType> SourceImportHopItemTypes =
    [
        ObjectChangeType.Projected,
        ObjectChangeType.Joined,
        ObjectChangeType.AttributeFlow,
        ObjectChangeType.Disconnected,
        ObjectChangeType.DisconnectedOutOfScope,
        ObjectChangeType.OutOfScopeRetainJoin,
        ObjectChangeType.DriftCorrection
    ];

    /// <summary>
    /// Builds the source-import hop behind a synchronisation item, or null where none applies: the item is not
    /// synchronisation-side, processed no Connected System Object, or no import on the record is retained.
    /// </summary>
    /// <remarks>
    /// Derived from the record's timeline rather than a stored edge, which is the PRD's requirement 4 applied:
    /// per-object timelines are the free join, used wherever "what else happened to this object" is the answer.
    /// The alternative, capturing an edge at synchronisation time, was rejected because it would put a
    /// change-history lookup per object on the full-synchronisation hot path to record what this join already
    /// yields at read time.
    ///
    /// The hop's member carries the import item's id, so the walk continues through it: an import that was
    /// itself an export confirmation resolves its own edges at the next level, and times move strictly
    /// backwards along the timeline, so the recursion terminates at the depth bound or the true root.
    /// </remarks>
    private async Task<CausalChainCohort?> TryBuildSourceImportCohortAsync(CausalChainItemSummary summary)
    {
        if (!SourceImportHopItemTypes.Contains(summary.ObjectChangeType))
            return null;

        var importEvent = await FindSourceImportEventAsync(summary);
        if (importEvent == null)
            return null;

        return new CausalChainCohort
        {
            SourceImportChangeType = importEvent.ChangeType,
            ConnectedSystemId = importEvent.ConnectedSystemId,
            ConnectedSystemName = importEvent.ConnectedSystemName,
            Members =
            [
                new CausalChainMember
                {
                    RunProfileExecutionItemId = importEvent.RunProfileExecutionItemId,
                    ConnectedSystemObjectId = summary.ConnectedSystemObjectId,
                    DisplayName = importEvent.DisplayName,
                    Resolution = CausalChainResolution.Resolved
                }
            ]
        };
    }

    /// <summary>
    /// Finds the import that last changed the item's record: by the record's id while it exists, and by the
    /// external ID snapshotted on the item once the record's deletion has nulled the id on every item that
    /// processed it (#1495). The degraded key is bounded exactly as the id is (latest at or before the
    /// synchronisation, within the Activity's Connected System) and reaches the same import, which matters
    /// most on precisely these chains: a deletion cascade is where the timeline used to go dark.
    /// </summary>
    private async Task<CausalSourceImportEvent?> FindSourceImportEventAsync(CausalChainItemSummary summary)
    {
        if (summary.ConnectedSystemObjectId is { } connectedSystemObjectId)
            return await Application.Repository.Activity.GetLatestImportItemForCsoAsync(
                connectedSystemObjectId, summary.ActivityExecuted, summary.Id);

        if (summary.ConnectedSystemId is { } connectedSystemId && !string.IsNullOrEmpty(summary.ExternalIdSnapshot))
            return await Application.Repository.Activity.GetLatestImportItemForExternalIdAsync(
                connectedSystemId, summary.ExternalIdSnapshot, summary.ActivityExecuted, summary.Id);

        return null;
    }

    /// <summary>
    /// Whether the record's timeline behind a synchronisation-side item is known to be lost rather than
    /// complete: the item was fed by an import by definition, but its record's deletion nulled the id the
    /// timeline is walked on. Only meaningful once hop-building has already failed; where the snapshot
    /// fallback reached the import, the member resolved and this is never asked.
    /// </summary>
    private static bool IsTimelineSevered(CausalChainItemSummary summary) =>
        SourceImportHopItemTypes.Contains(summary.ObjectChangeType) && !summary.ConnectedSystemObjectId.HasValue;

    private static List<CausalChainCohort> GroupIntoCohorts(List<CausalEdge> edges)
    {
        return edges
            // The object type and the attribute join the key so the rendered sentence is always correct for
            // every member: a cohort that mixed a User and a Contractor would have no right noun, and one
            // that mixed Static Members with Owners would name the wrong relationship for half its members.
            .GroupBy(e => (e.EffectSyncOutcomeId, e.EdgeType, e.ReasonCode, e.ConnectedSystemId, e.SyncRuleId,
                e.CauseObjectTypeName, e.EffectAttributeName))
            .Select(g => new CausalChainCohort
            {
                EffectSyncOutcomeId = g.Key.EffectSyncOutcomeId,
                EdgeType = g.Key.EdgeType,
                ReasonCode = g.Key.ReasonCode,
                ConnectedSystemId = g.Key.ConnectedSystemId,
                ObjectTypeName = g.Key.CauseObjectTypeName,
                ObjectTypePluralName = g.First().CauseObjectTypePluralName,
                AttributeName = g.Key.EffectAttributeName,
                // Names come off the first edge in the group: they are snapshots of the same system and rule,
                // so any member carries the same value, and taking one avoids implying they could differ.
                ConnectedSystemName = g.First().ConnectedSystemName,
                SyncRuleId = g.Key.SyncRuleId,
                SyncRuleName = g.First().SyncRuleName,
                Members = g.Select(e => new CausalChainMember
                {
                    MetaverseObjectId = e.CauseMetaverseObjectId,
                    ConnectedSystemObjectId = e.CauseConnectedSystemObjectId,
                    PendingExportId = e.CausePendingExportId,
                    RunProfileExecutionItemId = e.CauseRunProfileExecutionItemId,
                    SyncOutcomeId = e.CauseSyncOutcomeId,
                    DisplayName = e.CauseDisplayName,
                    Occurred = e.Created,
                    // A cause that named no item has nowhere to walk to, and nothing was lost in getting here,
                    // so it is a complete ending rather than a truncated one.
                    Resolution = e.CauseRunProfileExecutionItemId.HasValue
                        ? CausalChainResolution.Resolved
                        : CausalChainResolution.NoFurtherCauses
                }).ToList()
            })
            .ToList();
    }
    #endregion
}