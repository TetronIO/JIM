// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

public interface IActivityRepository
{

    public Task CreateActivityAsync(Activity activity);

    /// <summary>
    /// Persists Run Profile Execution Items (including their sync outcome trees and any Connected System Object
    /// change snapshots carried on the outcomes) for an Activity that has already been persisted via
    /// <see cref="CreateActivityAsync"/> in the same unit of work. Items must reference related entities
    /// (Connected System Objects, Pending Exports) by scalar foreign key only; the implementation severs any
    /// navigation references to pre-existing entities so they cannot be re-inserted.
    /// Intended for small batches recorded outside sync task processing (for example Metaverse Object
    /// Housekeeping); sync processors use the bulk insert path on ISyncRepository instead.
    /// </summary>
    public Task CreateActivityRunProfileExecutionItemsAsync(IReadOnlyCollection<ActivityRunProfileExecutionItem> items);

    public Task UpdateActivityAsync(Activity activity);

    public Task DeleteActivityAsync(Activity activity);

    public Task<Activity?> GetActivityAsync(Guid id);

    /// <summary>
    /// Gets a page's worth of direct child activities for a given parent activity ID,
    /// ordered by creation date ascending.
    /// </summary>
    public Task<PagedResultSet<Activity>> GetChildActivitiesAsync(Guid parentActivityId, int page, int pageSize);

    /// <summary>
    /// Returns a dictionary mapping each activity ID (from the provided set) to its direct child activity count.
    /// IDs with no children are omitted from the result.
    /// </summary>
    public Task<Dictionary<Guid, int>> GetChildActivityCountsAsync(IEnumerable<Guid> activityIds);

    public Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page,
        int pageSize,
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
        DateTime? createdTo = null);

    public Task<PagedResultSet<Activity>> GetWorkerTaskActivitiesAsync(
        int page,
        int pageSize,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        string? initiatedByFilter = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool? hasChildActivities = null);

    public Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync();

    public Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null);

    public Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId);

    /// <summary>
    /// Gets a lightweight progress snapshot for an Activity (#202): a scalar projection of the
    /// progress fields plus an operation-type breakdown from the Activity's stat counter rows.
    /// Cheap enough to serve at a high read frequency while a run is executing; never
    /// materialises Run Profile Execution Items. Returns null when the Activity does not exist.
    /// </summary>
    public Task<ActivityProgress?> GetActivityProgressAsync(Guid activityId);

    /// <summary>
    /// Finalises the Activity's Run Profile execution stat counters: recomputes the stats exactly
    /// from the persisted Run Profile Execution Items and Sync Outcomes, replaces the incremental
    /// counter rows with the exact values, and sets
    /// <see cref="Activity.RunProfileExecutionStatsFinalised"/> on the passed entity (the caller's
    /// subsequent <see cref="UpdateActivityAsync"/> persists the flag alongside the terminal
    /// status). Called by the completion paths so completed Activities serve stats from stored
    /// counters instead of re-aggregating; safe to call for Activities with no execution items.
    /// </summary>
    public Task FinaliseActivityRunProfileExecutionStatsAsync(Activity activity);

    public Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id);

    /// <summary>
    /// Gets all activities associated with a schedule execution.
    /// Used by the scheduler to determine step outcomes after worker tasks have been deleted.
    /// </summary>
    public Task<List<Activity>> GetActivitiesByScheduleExecutionAsync(Guid scheduleExecutionId);

    /// <summary>
    /// Gets all activities for a specific step within a schedule execution.
    /// A step may have multiple activities if it runs multiple Run Profiles in parallel.
    /// </summary>
    public Task<List<Activity>> GetActivitiesByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex);

    /// <summary>
    /// Gets the creation time of the most recent HistoryRetentionCleanup activity.
    /// Used by the worker to determine whether the cleanup interval has elapsed since the last run,
    /// preventing immediate re-execution after worker restarts.
    /// </summary>
    public Task<DateTime?> GetLastHistoryCleanupTimeAsync();

    /// <summary>
    /// Gets the highest configuration-change version recorded for a configuration object, identified by its activity
    /// target type (<see cref="ActivityTargetType.ConnectedSystem"/> or <see cref="ActivityTargetType.SynchronisationRule"/>) and
    /// database id, or 0 if none exist yet. Used to assign the next per-object version when capturing a configuration
    /// snapshot; version numbers never renumber, so retention removing older entries does not affect this.
    /// </summary>
    public Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, int targetObjectId);

    /// <summary>
    /// Gets the highest configuration-change version recorded for a Guid-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.Schedule"/>), identified by its activity target type and Guid database id, or 0 if
    /// none exist yet. The Guid-keyed counterpart of <see cref="GetMaxConfigurationChangeVersionAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, Guid targetObjectId);

    /// <summary>
    /// Gets the snapshot JSON of the highest configuration-change version recorded for a configuration object, or null
    /// if none exists yet. Used by the idempotent capture guard: a new capture whose snapshot is identical to the
    /// latest stored one is skipped rather than recorded as a no-change version.
    /// </summary>
    public Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, int targetObjectId);

    /// <summary>
    /// Gets the snapshot JSON of the highest configuration-change version recorded for a Guid-keyed configuration
    /// object (e.g. a <see cref="ActivityTargetType.Schedule"/>), or null if none exists yet. The Guid-keyed
    /// counterpart of <see cref="GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, Guid targetObjectId);

    /// <summary>
    /// Counts the versioned configuration-change activities recorded for a configuration object.
    /// </summary>
    public Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, int targetObjectId);

    /// <summary>
    /// Counts the versioned configuration-change activities recorded for a Guid-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.Schedule"/>).
    /// </summary>
    public Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, Guid targetObjectId);

    /// <summary>
    /// Returns a page of versioned configuration-change activities for a configuration object, newest version first,
    /// each including the raw snapshot JSON so the application layer can build summaries and diffs.
    /// </summary>
    public Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, int targetObjectId, int skip, int take);

    /// <summary>
    /// Returns a page of versioned configuration-change activities for a Guid-keyed configuration object, newest
    /// version first. The Guid-keyed counterpart of
    /// <see cref="GetConfigurationChangeActivitiesAsync(ActivityTargetType,int,int,int)"/>.
    /// </summary>
    public Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, Guid targetObjectId, int skip, int take);

    /// <summary>
    /// Returns the configuration-change activity for a specific version of a configuration object, or null if absent.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, int targetObjectId, int version);

    /// <summary>
    /// Returns the configuration-change activity for a specific version of a Guid-keyed configuration object, or null
    /// if absent.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, Guid targetObjectId, int version);

    /// <summary>
    /// Returns the configuration-change activity for the highest version below <paramref name="version"/> (the
    /// immediate predecessor), or null if none exists. Used to diff a version against the one before it.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, int targetObjectId, int version);

    /// <summary>
    /// Returns the configuration-change activity for the highest version below <paramref name="version"/> of a
    /// Guid-keyed configuration object, or null if none exists.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, Guid targetObjectId, int version);

    /// <summary>
    /// Gets the highest configuration-change version recorded for a string-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.ServiceSetting"/>, keyed by its setting key), or 0 if none exist yet. The
    /// string-keyed counterpart of <see cref="GetMaxConfigurationChangeVersionAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, string targetObjectKey);

    /// <summary>
    /// Gets the snapshot JSON of the highest configuration-change version recorded for a string-keyed configuration
    /// object (e.g. a <see cref="ActivityTargetType.ServiceSetting"/>), or null if none exists yet. The string-keyed
    /// counterpart of <see cref="GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType,int)"/>.
    /// </summary>
    public Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, string targetObjectKey);

    /// <summary>
    /// Counts the versioned configuration-change activities recorded for a string-keyed configuration object (e.g. a
    /// <see cref="ActivityTargetType.ServiceSetting"/>).
    /// </summary>
    public Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, string targetObjectKey);

    /// <summary>
    /// Returns a page of versioned configuration-change activities for a string-keyed configuration object, newest
    /// version first. The string-keyed counterpart of
    /// <see cref="GetConfigurationChangeActivitiesAsync(ActivityTargetType,int,int,int)"/>.
    /// </summary>
    public Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, string targetObjectKey, int skip, int take);

    /// <summary>
    /// Returns the configuration-change activity for a specific version of a string-keyed configuration object, or
    /// null if absent.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, string targetObjectKey, int version);

    /// <summary>
    /// Returns the configuration-change activity for the highest version below <paramref name="version"/> of a
    /// string-keyed configuration object, or null if none exists.
    /// </summary>
    public Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, string targetObjectKey, int version);

    /// <summary>
    /// Queries the database for RPEI error counts for an activity, returning the total number of
    /// RPEIs with errors, the total number of RPEIs, and the number of UnhandledError RPEIs.
    /// Used to determine activity completion status (success/warning/failure) without loading
    /// RPEIs into memory.
    /// </summary>
    public Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId);

    /// <summary>
    /// Atomically increments <c>AttemptCount</c> and advances <c>LastSeen</c> on the aggregated failed-authentication
    /// Activity row matching (TargetType Authentication, <paramref name="apiKeyPrefix"/>, <paramref name="clientIp"/>,
    /// <paramref name="reason"/>, <paramref name="windowStart"/>). Callers must normalise a null API key prefix or
    /// client IP to <see cref="string.Empty"/> before calling, matching the partial unique index's dedup contract
    /// (Postgres unique indexes treat NULLs as distinct from one another).
    /// </summary>
    /// <returns>True if a matching row was found and incremented; false if no row exists yet for this window bucket
    /// (the caller must then create one).</returns>
    public Task<bool> IncrementAggregatedFailedAuthenticationAsync(string apiKeyPrefix, string clientIp, string reason, DateTime windowStart, DateTime lastSeen);
}