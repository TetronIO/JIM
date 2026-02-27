using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

public interface IActivityRepository
{

    public Task CreateActivityAsync(Activity activity);

    public Task UpdateActivityAsync(Activity activity);

    public Task DeleteActivityAsync(Activity activity);

    public Task<Activity?> GetActivityAsync(Guid id);

    public Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null);

    public Task<PagedResultSet<Activity>> GetWorkerTaskActivitiesAsync(
        int page,
        int pageSize,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        string? initiatedByFilter = null,
        string? sortBy = null,
        bool sortDescending = true);

    public Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync();

    public Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<ObjectChangeType>? changeTypeFilter = null,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null);

    public Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId);

    public Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id);

    /// <summary>
    /// Gets all activities associated with a schedule execution.
    /// Used by the scheduler to determine step outcomes after worker tasks have been deleted.
    /// </summary>
    public Task<List<Activity>> GetActivitiesByScheduleExecutionAsync(Guid scheduleExecutionId);

    /// <summary>
    /// Gets all activities for a specific step within a schedule execution.
    /// A step may have multiple activities if it runs multiple run profiles in parallel.
    /// </summary>
    public Task<List<Activity>> GetActivitiesByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex);

    /// <summary>
    /// Gets the creation time of the most recent HistoryRetentionCleanup activity.
    /// Used by the worker to determine whether the cleanup interval has elapsed since the last run,
    /// preventing immediate re-execution after worker restarts.
    /// </summary>
    public Task<DateTime?> GetLastHistoryCleanupTimeAsync();

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItems directly via raw SQL,
    /// bypassing the EF change tracker for performance during large sync runs.
    /// RPEIs must have ActivityId set before calling. IDs are pre-generated if empty.
    /// Returns true if raw SQL was used (RPEIs are persisted outside EF's change tracker),
    /// false if the EF fallback was used (RPEIs remain tracked by EF).
    /// </summary>
    public Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis);
}