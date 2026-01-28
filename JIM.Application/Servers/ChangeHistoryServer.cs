using JIM.Models.Activities;
using JIM.Models.Security;
using Serilog;

namespace JIM.Application.Servers;

public class ChangeHistoryServer
{
    private readonly JimApplication _application;

    public ChangeHistoryServer(JimApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    /// <summary>
    /// Result object for change history cleanup operations.
    /// </summary>
    public class ChangeHistoryCleanupResult
    {
        public int CsoChangesDeleted { get; set; }
        public int MvoChangesDeleted { get; set; }
        public int ActivitiesDeleted { get; set; }
        public DateTime? OldestRecordDeleted { get; set; }
        public DateTime? NewestRecordDeleted { get; set; }
    }

    /// <summary>
    /// Deletes expired change history and activity records based on retention policy.
    /// Creates a system-initiated Activity record to audit the cleanup operation.
    /// Use this overload for automated/scheduled cleanup (worker housekeeping).
    /// </summary>
    public async Task<ChangeHistoryCleanupResult> DeleteExpiredChangeHistoryAsync(
        DateTime olderThan,
        int maxRecordsPerType)
    {
        var activity = CreateCleanupActivity();
        await _application.Activities.CreateSystemActivityAsync(activity);
        return await ExecuteCleanupAsync(activity, olderThan, maxRecordsPerType);
    }

    /// <summary>
    /// Deletes expired change history and activity records based on retention policy.
    /// Creates an Activity record attributed to the specified API key.
    /// Use this overload for API-initiated cleanup.
    /// </summary>
    public async Task<ChangeHistoryCleanupResult> DeleteExpiredChangeHistoryAsync(
        DateTime olderThan,
        int maxRecordsPerType,
        ApiKey initiatedByApiKey)
    {
        var activity = CreateCleanupActivity();
        await _application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        return await ExecuteCleanupAsync(activity, olderThan, maxRecordsPerType);
    }

    private static Activity CreateCleanupActivity()
    {
        return new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.HistoryRetentionCleanup,
            TargetOperationType = ActivityTargetOperationType.Delete,
            Message = "Change history and activity retention cleanup"
        };
    }

    private async Task<ChangeHistoryCleanupResult> ExecuteCleanupAsync(
        Activity activity,
        DateTime olderThan,
        int maxRecordsPerType)
    {
        var result = new ChangeHistoryCleanupResult();

        try
        {
            // Delete CSO changes
            Log.Information("ChangeHistoryCleanup: Deleting expired CSO changes (older than {OlderThan})", olderThan);
            result.CsoChangesDeleted = await _application.Repository.ChangeHistory.DeleteExpiredCsoChangesAsync(olderThan, maxRecordsPerType);

            // Delete MVO changes
            Log.Information("ChangeHistoryCleanup: Deleting expired MVO changes (older than {OlderThan})", olderThan);
            result.MvoChangesDeleted = await _application.Repository.ChangeHistory.DeleteExpiredMvoChangesAsync(olderThan, maxRecordsPerType);

            // Delete Activities
            Log.Information("ChangeHistoryCleanup: Deleting expired activities (older than {OlderThan})", olderThan);
            result.ActivitiesDeleted = await _application.Repository.ChangeHistory.DeleteExpiredActivitiesAsync(olderThan, maxRecordsPerType);

            // Calculate overall date range (use oldest/newest across all types)
            // Note: We can't get the exact IDs that were deleted without changing the repository methods,
            // so we'll use the olderThan date as a proxy for the date range
            if (result.CsoChangesDeleted > 0 || result.MvoChangesDeleted > 0 || result.ActivitiesDeleted > 0)
            {
                result.OldestRecordDeleted = olderThan.AddDays(-365); // Estimate - we don't have exact oldest
                result.NewestRecordDeleted = olderThan;
            }

            // Update activity with cleanup statistics
            activity.DeletedCsoChangeCount = result.CsoChangesDeleted;
            activity.DeletedMvoChangeCount = result.MvoChangesDeleted;
            activity.DeletedActivityCount = result.ActivitiesDeleted;
            activity.DeletedRecordsFromDate = result.OldestRecordDeleted;
            activity.DeletedRecordsToDate = result.NewestRecordDeleted;

            await _application.Activities.CompleteActivityAsync(activity);

            Log.Information("ChangeHistoryCleanup: Completed - {CsoCount} CSO changes, {MvoCount} MVO changes, {ActivityCount} activities deleted",
                result.CsoChangesDeleted, result.MvoChangesDeleted, result.ActivitiesDeleted);

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ChangeHistoryCleanup: Error during cleanup");
            await _application.Activities.FailActivityWithErrorAsync(activity, $"Cleanup failed: {ex.Message}");
            throw;
        }
    }
}
