using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
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
    /// <param name="initiatedBy">The MetaverseObject representing the user who initiated the action.</param>
    public async Task CreateActivityAsync(Activity activity, MetaverseObject? initiatedBy)
    {
        activity.Status = ActivityStatus.InProgress;
        activity.Executed = DateTime.UtcNow;

        if (initiatedBy != null)
        {
            activity.InitiatedByType = ActivityInitiatorType.User;
            activity.InitiatedById = initiatedBy.Id;
            activity.InitiatedByMetaverseObject = initiatedBy;
            activity.InitiatedByName = initiatedBy.DisplayName;
        }

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
        activity.InitiatedByApiKey = initiatedByApiKey;
        activity.InitiatedByName = initiatedByApiKey.Name;

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

        // Validate that the correct reference is set based on the initiator type
        if (activity.InitiatedByType == ActivityInitiatorType.User && activity.InitiatedByMetaverseObject == null)
            throw new InvalidOperationException("Activity initiated by a user must have InitiatedByMetaverseObject set.");

        if (activity.InitiatedByType == ActivityInitiatorType.ApiKey && activity.InitiatedByApiKey == null)
            throw new InvalidOperationException("Activity initiated by an API key must have InitiatedByApiKey set.");

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
        activity.ErrorMessage = exception.Message;
        activity.ErrorStackTrace = exception.StackTrace;
        activity.Status = ActivityStatus.FailedWithError;
        activity.ExecutionTime = now - activity.Executed;
        activity.TotalActivityTime = now - activity.Created;
        await Application.Repository.Activity.UpdateActivityAsync(activity);
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