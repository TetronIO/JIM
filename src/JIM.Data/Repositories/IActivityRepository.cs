using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

public interface IActivityRepository
{

    public Task CreateActivityAsync(Activity activity);

    public Task UpdateActivityAsync(Activity activity);

    /// <summary>
    /// Updates only the progress fields (ObjectsProcessed, ObjectsToProcess, Message) on an Activity
    /// using an independent database connection, bypassing any in-flight transaction on the main DbContext.
    /// Use this when progress updates need to be immediately visible to other sessions (e.g., the UI)
    /// whilst a long-running transaction is in progress.
    /// </summary>
    public Task UpdateActivityProgressOutOfBandAsync(Activity activity);

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
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null);

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

    /// <summary>
    /// Bulk updates OutcomeSummary and error fields on already-persisted RPEIs,
    /// and inserts any new SyncOutcomes that were added after initial persistence.
    /// Used by confirming imports to merge reconciliation outcomes onto existing import RPEIs.
    /// </summary>
    public Task BulkUpdateRpeiOutcomesAsync(
        List<ActivityRunProfileExecutionItem> rpeis,
        List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes);

    /// <summary>
    /// Detaches RPEIs from the EF change tracker so they are not persisted by subsequent
    /// SaveChangesAsync calls. Call this after raw SQL bulk insert has persisted them.
    /// </summary>
    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis);

    /// <summary>
    /// Queries the database for RPEI error counts for an activity, returning the total number of
    /// RPEIs with errors and the total number of RPEIs. Used by the worker to determine activity
    /// completion status (success/warning/failure) without loading RPEIs into memory.
    /// </summary>
    public Task<(int TotalWithErrors, int TotalRpeis)> GetActivityRpeiErrorCountsAsync(Guid activityId);

    /// <summary>
    /// Persists ConnectedSystemObjectChange records that are attached to RPEIs.
    /// Used by the export processor to persist export change history records after
    /// RPEIs have been bulk-inserted via raw SQL (which only inserts RPEI scalar columns).
    /// The change records and their attribute/value children are added to the DbContext
    /// and saved in a single operation.
    /// </summary>
    public Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis);
}