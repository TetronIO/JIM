using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Security;
using JIM.Models.Utility;
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
    /// Creates and persists an Activity using an initiator triad (Type, Id, Name).
    /// Used when the full principal object is not available (e.g., from WorkerTask).
    /// </summary>
    public async Task CreateActivityWithTriadAsync(Activity activity, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName)
    {
        activity.Status = ActivityStatus.InProgress;
        activity.Executed = DateTime.UtcNow;
        activity.InitiatedByType = initiatorType;
        activity.InitiatedById = initiatorId;
        activity.InitiatedByName = initiatorName ?? (initiatorType == ActivityInitiatorType.System ? "System" : "Unknown");

        ValidateActivity(activity);
        await Application.Repository.Activity.CreateActivityAsync(activity);
    }

    private void ValidateActivity(Activity activity)
    {
        // All activities MUST be attributed to a security principal for audit compliance.
        // This is a critical requirement - no exceptions.
        if (activity.InitiatedByType == ActivityInitiatorType.NotSet)
            throw new InvalidOperationException("Activity must be attributed to a security principal. InitiatedByType has not been set.");

        // System activities have no principal entity, so InitiatedById is allowed to be null.
        // User and ApiKey activities must have an InitiatedById.
        if (activity.InitiatedByType != ActivityInitiatorType.System && activity.InitiatedById == null)
            throw new InvalidOperationException("Activity must be attributed to a security principal. InitiatedById has not been set.");

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

    public async Task CompleteActivityAsync(Activity activity)
    {
        var now = DateTime.UtcNow;
        activity.Status = ActivityStatus.Complete;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task CompleteActivityWithWarningAsync(Activity activity)
    {
        var now = DateTime.UtcNow;
        activity.ExecutionTime = DateTime.UtcNow - activity.Executed;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        activity.Status = ActivityStatus.CompleteWithWarning;
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
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    public async Task UpdateActivityAsync(Activity activity)
    {
        await Application.Repository.Activity.UpdateActivityAsync(activity);
    }

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItems directly via raw SQL,
    /// bypassing the EF change tracker for performance during large sync runs.
    /// Returns true if raw SQL was used (RPEIs persisted outside EF), false if EF fallback was used.
    /// </summary>
    public async Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        return await Application.Repository.Activity.BulkInsertRpeisAsync(rpeis);
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
    /// Retrieves a page's worth of top-level activities, i.e. those that do not have a parent activity.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="searchQuery">Optional search query to filter by TargetName or TargetType.</param>
    /// <param name="sortBy">Optional column to sort by (e.g., "type", "target", "created", "status").</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true).</param>
    /// <param name="initiatedById">Optional filter to only show activities initiated by a specific user.</param>
    public async Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null)
    {
        return await Application.Repository.Activity.GetActivitiesAsync(page, pageSize, searchQuery, sortBy, sortDescending, initiatedById);
    }

    /// <summary>
    /// Retrieves a page's worth of worker task activities (run profile executions, data generation, system operations).
    /// Filtered to show only activities related to worker tasks for the Operations page History tab.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="connectedSystemFilter">Optional filter for connected system names (additive/OR within filter).</param>
    /// <param name="runProfileFilter">Optional filter for run profile names (additive/OR within filter).</param>
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
        bool sortDescending = true)
    {
        return await Application.Repository.Activity.GetWorkerTaskActivitiesAsync(
            page, pageSize, connectedSystemFilter, runProfileFilter, statusFilter, initiatedByFilter, sortBy, sortDescending);
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
    /// Supports server-side search, sorting, and filtering by change type, object type, and error type.
    /// </summary>
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page = 1,
        int pageSize = 20,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<ObjectChangeType>? changeTypeFilter = null,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionItemHeadersAsync(
            activityId, page, pageSize, searchQuery, sortBy, sortDescending, changeTypeFilter, objectTypeFilter, errorTypeFilter);
    }

    public async Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionStatsAsync(activityId);
    }

    public async Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id)
    {
        return await Application.Repository.Activity.GetActivityRunProfileExecutionItemAsync(id);
    }
    #endregion
}