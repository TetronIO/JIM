// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Security;
using JIM.Models.Utility;
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

    /// <summary>
    /// Gets the time of the most recent history retention cleanup.
    /// Returns null if no cleanup has ever been performed.
    /// </summary>
    public async Task<DateTime?> GetLastCleanupTimeAsync()
    {
        return await _application.Repository.Activity.GetLastHistoryCleanupTimeAsync();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Configuration change history retrieval
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns a page of a configuration object's change history, newest version first, each row carrying a one-line
    /// summary of what changed versus the previous version.
    /// </summary>
    public async Task<PagedResultSet<ConfigurationChangeHistoryItem>> GetConfigurationChangeHistoryAsync(ActivityTargetType targetType, int objectId, int page = 1, int pageSize = 20)
    {
        if (page < 1)
            page = 1;
        if (pageSize < 1)
            pageSize = 20;

        var total = await _application.Repository.Activity.GetConfigurationChangeCountAsync(targetType, objectId);
        var skip = (page - 1) * pageSize;

        // Fetch one extra older row so the oldest row on the page can be diffed against its predecessor.
        var rows = await _application.Repository.Activity.GetConfigurationChangeActivitiesAsync(targetType, objectId, skip, pageSize + 1);

        var items = new List<ConfigurationChangeHistoryItem>();
        for (var i = 0; i < rows.Count && i < pageSize; i++)
        {
            var current = rows[i];
            var predecessor = i + 1 < rows.Count ? rows[i + 1] : null;
            items.Add(new ConfigurationChangeHistoryItem
            {
                ActivityId = current.ActivityId,
                Version = current.Version,
                Operation = current.Operation,
                InitiatedByType = current.InitiatedByType,
                InitiatedByName = current.InitiatedByName,
                When = current.When,
                Reason = current.Reason,
                Summary = BuildChangeSummary(current, predecessor)
            });
        }

        return new PagedResultSet<ConfigurationChangeHistoryItem>
        {
            Results = items,
            TotalResults = total,
            CurrentPage = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Returns a single configuration change in full: its snapshot and the structured diff against the previous version,
    /// or null if the version does not exist or carries no snapshot.
    /// </summary>
    public async Task<ConfigurationChangeDetail?> GetConfigurationChangeAsync(ActivityTargetType targetType, int objectId, int version)
    {
        var current = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectId, version);
        var currentSnapshot = ConfigurationSnapshotService.Deserialise(current?.SnapshotJson);
        if (current == null || currentSnapshot == null)
            return null;

        var predecessor = await _application.Repository.Activity.GetConfigurationChangeActivityBeforeVersionAsync(targetType, objectId, version);
        var predecessorSnapshot = ConfigurationSnapshotService.Deserialise(predecessor?.SnapshotJson);

        return new ConfigurationChangeDetail
        {
            ActivityId = current.ActivityId,
            Version = current.Version,
            Operation = current.Operation,
            InitiatedByType = current.InitiatedByType,
            InitiatedByName = current.InitiatedByName,
            When = current.When,
            Reason = current.Reason,
            Snapshot = currentSnapshot,
            Diff = _application.ConfigurationDiffs.Diff(predecessorSnapshot, currentSnapshot, predecessor?.Version, current.Version)
        };
    }

    /// <summary>
    /// Compares any two versions of a configuration object, returning the structured diff of the later against the
    /// earlier, or null if the later version does not exist or carries no snapshot.
    /// </summary>
    public async Task<ConfigurationDiff?> CompareConfigurationChangesAsync(ActivityTargetType targetType, int objectId, int fromVersion, int toVersion)
    {
        var from = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectId, fromVersion);
        var to = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectId, toVersion);
        var fromSnapshot = ConfigurationSnapshotService.Deserialise(from?.SnapshotJson);
        var toSnapshot = ConfigurationSnapshotService.Deserialise(to?.SnapshotJson);
        if (toSnapshot == null)
            return null;

        return _application.ConfigurationDiffs.Diff(fromSnapshot, toSnapshot, from?.Version, toVersion);
    }

    private string BuildChangeSummary(ConfigurationChangeActivityData current, ConfigurationChangeActivityData? predecessor)
    {
        if (current.Operation == ActivityTargetOperationType.Create)
            return "Created";

        var currentSnapshot = ConfigurationSnapshotService.Deserialise(current.SnapshotJson);
        var predecessorSnapshot = ConfigurationSnapshotService.Deserialise(predecessor?.SnapshotJson);
        if (currentSnapshot == null || predecessorSnapshot == null)
            return "Updated";

        var diff = _application.ConfigurationDiffs.Diff(predecessorSnapshot, currentSnapshot, predecessor!.Version, current.Version);
        return ConfigurationDiffService.Summarise(diff);
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
