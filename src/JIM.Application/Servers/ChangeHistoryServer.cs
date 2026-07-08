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
        public int ConfigurationChangeActivitiesDeleted { get; set; }
        public DateTime? OldestRecordDeleted { get; set; }
        public DateTime? NewestRecordDeleted { get; set; }
    }

    /// <summary>
    /// Deletes expired change history and activity records based on retention policy. Configuration-change
    /// Activities have their own cutoff (typically far older than the general one), so configuration change history
    /// outlives the high-volume sync and identity-data history.
    /// Creates a system-initiated Activity record to audit the cleanup operation.
    /// Use this overload for automated/scheduled cleanup (worker housekeeping).
    /// </summary>
    public async Task<ChangeHistoryCleanupResult> DeleteExpiredChangeHistoryAsync(
        DateTime olderThan,
        DateTime configurationOlderThan,
        int maxRecordsPerType)
    {
        var activity = CreateCleanupActivity();
        await _application.Activities.CreateSystemActivityAsync(activity);
        return await ExecuteCleanupAsync(activity, olderThan, configurationOlderThan, maxRecordsPerType);
    }

    /// <summary>
    /// Deletes expired change history and activity records based on retention policy. Configuration-change
    /// Activities have their own cutoff (typically far older than the general one), so configuration change history
    /// outlives the high-volume sync and identity-data history.
    /// Creates an Activity record attributed to the specified API key.
    /// Use this overload for API-initiated cleanup.
    /// </summary>
    public async Task<ChangeHistoryCleanupResult> DeleteExpiredChangeHistoryAsync(
        DateTime olderThan,
        DateTime configurationOlderThan,
        int maxRecordsPerType,
        ApiKey initiatedByApiKey)
    {
        var activity = CreateCleanupActivity();
        await _application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        return await ExecuteCleanupAsync(activity, olderThan, configurationOlderThan, maxRecordsPerType);
    }

    /// <summary>
    /// Gets the time of the most recent history retention cleanup.
    /// Returns null if no cleanup has ever been performed.
    /// </summary>
    public async Task<DateTime?> GetLastCleanupTimeAsync()
    {
        return await _application.Repository.Activity.GetLastHistoryCleanupTimeAsync();
    }

    /// <summary>
    /// Gets the count of Connected System Object change history records for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>The count of CSO change records.</returns>
    public async Task<int> GetCsoChangeCountAsync(int connectedSystemId)
    {
        return await _application.Repository.ChangeHistory.GetCsoChangeCountAsync(connectedSystemId);
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
        NormalisePaging(ref page, ref pageSize);

        var total = await _application.Repository.Activity.GetConfigurationChangeCountAsync(targetType, objectId);
        var skip = (page - 1) * pageSize;

        // Fetch one extra older row so the oldest row on the page can be diffed against its predecessor.
        var rows = await _application.Repository.Activity.GetConfigurationChangeActivitiesAsync(targetType, objectId, skip, pageSize + 1);

        return BuildHistoryPage(rows, total, page, pageSize);
    }

    /// <summary>
    /// Returns a page of a Guid-keyed configuration object's (e.g. a Schedule's) change history, newest version first.
    /// The Guid-keyed counterpart of <see cref="GetConfigurationChangeHistoryAsync(ActivityTargetType,int,int,int)"/>.
    /// </summary>
    public async Task<PagedResultSet<ConfigurationChangeHistoryItem>> GetConfigurationChangeHistoryAsync(ActivityTargetType targetType, Guid objectId, int page = 1, int pageSize = 20)
    {
        NormalisePaging(ref page, ref pageSize);

        var total = await _application.Repository.Activity.GetConfigurationChangeCountAsync(targetType, objectId);
        var skip = (page - 1) * pageSize;

        // Fetch one extra older row so the oldest row on the page can be diffed against its predecessor.
        var rows = await _application.Repository.Activity.GetConfigurationChangeActivitiesAsync(targetType, objectId, skip, pageSize + 1);

        return BuildHistoryPage(rows, total, page, pageSize);
    }

    /// <summary>
    /// Returns a page of a string-keyed configuration object's (e.g. a Service Setting's) change history, newest
    /// version first. The string-keyed counterpart of
    /// <see cref="GetConfigurationChangeHistoryAsync(ActivityTargetType,int,int,int)"/>.
    /// </summary>
    public async Task<PagedResultSet<ConfigurationChangeHistoryItem>> GetConfigurationChangeHistoryAsync(ActivityTargetType targetType, string objectKey, int page = 1, int pageSize = 20)
    {
        NormalisePaging(ref page, ref pageSize);

        var total = await _application.Repository.Activity.GetConfigurationChangeCountAsync(targetType, objectKey);
        var skip = (page - 1) * pageSize;

        // Fetch one extra older row so the oldest row on the page can be diffed against its predecessor.
        var rows = await _application.Repository.Activity.GetConfigurationChangeActivitiesAsync(targetType, objectKey, skip, pageSize + 1);

        return BuildHistoryPage(rows, total, page, pageSize);
    }

    private static void NormalisePaging(ref int page, ref int pageSize)
    {
        if (page < 1)
            page = 1;
        if (pageSize < 1)
            pageSize = 20;
    }

    // Builds the history page from the fetched rows (which include one extra older row so the oldest row on the page
    // can be diffed against its predecessor). Shared by the int- and Guid-keyed overloads so both key shapes stay
    // behaviourally identical.
    private PagedResultSet<ConfigurationChangeHistoryItem> BuildHistoryPage(List<ConfigurationChangeActivityData> rows, int total, int page, int pageSize)
    {
        var items = new List<ConfigurationChangeHistoryItem>();
        for (var i = 0; i < rows.Count && i < pageSize; i++)
        {
            var current = rows[i];
            var predecessor = i + 1 < rows.Count ? rows[i + 1] : null;

            // The operation shown in an object's configuration history describes what happened to the object as a whole,
            // not to whichever sub-entity carried the change. A granular endpoint (e.g. adding an Attribute Flow mapping)
            // records its own Create/Delete operation on its Activity, but at the object level that is an Update. The only
            // version that is genuinely a creation is the object's first; every later version is an update of an existing
            // object. The extra older row fetched above guarantees predecessor is null only for that genuine first version.
            var isFirstVersion = predecessor == null;

            // Compute the diff against the predecessor once and carry it on the row: the list renders it inline, so there
            // is no second round-trip per row, and the summary is derived from the same diff. The first version has no
            // predecessor, so its diff shows the whole object as created.
            var currentSnapshot = ConfigurationSnapshotService.Deserialise(current.SnapshotJson);
            var predecessorSnapshot = ConfigurationSnapshotService.Deserialise(predecessor?.SnapshotJson);
            var diff = currentSnapshot == null
                ? null
                : _application.ConfigurationDiffs.Diff(predecessorSnapshot, currentSnapshot, predecessor?.Version, current.Version);

            items.Add(new ConfigurationChangeHistoryItem
            {
                ActivityId = current.ActivityId,
                Version = current.Version,
                Operation = isFirstVersion ? ActivityTargetOperationType.Create : ActivityTargetOperationType.Update,
                InitiatedByType = current.InitiatedByType,
                InitiatedById = current.InitiatedById,
                InitiatedByName = current.InitiatedByName,
                When = current.When,
                Reason = current.Reason,
                // The first version is a creation regardless of the recording activity's own operation; otherwise the
                // summary is the one-line rollup of the diff we just computed (falling back to a generic label if the
                // snapshot could not be deserialised).
                Summary = isFirstVersion ? "Created" : diff != null ? ConfigurationDiffService.Summarise(diff) : "Updated",
                Diff = diff
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
        var predecessor = current == null
            ? null
            : await _application.Repository.Activity.GetConfigurationChangeActivityBeforeVersionAsync(targetType, objectId, version);
        return BuildChangeDetail(current, predecessor);
    }

    /// <summary>
    /// Returns a single configuration change of a Guid-keyed configuration object (e.g. a Schedule) in full, or null
    /// if the version does not exist or carries no snapshot.
    /// </summary>
    public async Task<ConfigurationChangeDetail?> GetConfigurationChangeAsync(ActivityTargetType targetType, Guid objectId, int version)
    {
        var current = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectId, version);
        var predecessor = current == null
            ? null
            : await _application.Repository.Activity.GetConfigurationChangeActivityBeforeVersionAsync(targetType, objectId, version);
        return BuildChangeDetail(current, predecessor);
    }

    /// <summary>
    /// Returns a single configuration change of a string-keyed configuration object (e.g. a Service Setting) in full,
    /// or null if the version does not exist or carries no snapshot.
    /// </summary>
    public async Task<ConfigurationChangeDetail?> GetConfigurationChangeAsync(ActivityTargetType targetType, string objectKey, int version)
    {
        var current = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectKey, version);
        var predecessor = current == null
            ? null
            : await _application.Repository.Activity.GetConfigurationChangeActivityBeforeVersionAsync(targetType, objectKey, version);
        return BuildChangeDetail(current, predecessor);
    }

    /// <summary>
    /// Builds a change detail directly from a deletion tombstone's snapshot, as carried on the delete Activity itself.
    /// A delete records an unversioned tombstone (no surviving object and no version to look up), so it cannot go
    /// through the version-based <see cref="GetConfigurationChangeAsync(ActivityTargetType,int,int)"/> path. The result
    /// renders the deleted object's final captured state as a whole-object removal, letting the Activity detail page
    /// show what was deleted. Returns null when the Activity carries no snapshot (e.g. tracking was disabled).
    /// </summary>
    public ConfigurationChangeDetail? BuildConfigurationChangeDetailFromDeletionSnapshot(string? snapshotJson)
    {
        var snapshot = ConfigurationSnapshotService.Deserialise(snapshotJson);
        if (snapshot == null)
            return null;

        return new ConfigurationChangeDetail
        {
            Operation = ActivityTargetOperationType.Delete,
            Snapshot = snapshot,
            Diff = _application.ConfigurationDiffs.DiffDeletion(snapshot)
        };
    }

    // Builds the change detail from the fetched version and its predecessor. Shared by the int-, Guid- and
    // string-keyed overloads.
    private ConfigurationChangeDetail? BuildChangeDetail(ConfigurationChangeActivityData? current, ConfigurationChangeActivityData? predecessor)
    {
        var currentSnapshot = ConfigurationSnapshotService.Deserialise(current?.SnapshotJson);
        if (current == null || currentSnapshot == null)
            return null;

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
        return BuildComparison(from, to, toVersion);
    }

    /// <summary>
    /// Compares any two versions of a Guid-keyed configuration object (e.g. a Schedule), returning the structured diff
    /// of the later against the earlier, or null if the later version does not exist or carries no snapshot.
    /// </summary>
    public async Task<ConfigurationDiff?> CompareConfigurationChangesAsync(ActivityTargetType targetType, Guid objectId, int fromVersion, int toVersion)
    {
        var from = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectId, fromVersion);
        var to = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectId, toVersion);
        return BuildComparison(from, to, toVersion);
    }

    /// <summary>
    /// Compares any two versions of a string-keyed configuration object (e.g. a Service Setting), returning the
    /// structured diff of the later against the earlier, or null if the later version does not exist or carries no
    /// snapshot.
    /// </summary>
    public async Task<ConfigurationDiff?> CompareConfigurationChangesAsync(ActivityTargetType targetType, string objectKey, int fromVersion, int toVersion)
    {
        var from = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectKey, fromVersion);
        var to = await _application.Repository.Activity.GetConfigurationChangeActivityByVersionAsync(targetType, objectKey, toVersion);
        return BuildComparison(from, to, toVersion);
    }

    // Builds the comparison diff from the two fetched versions. Shared by the int-, Guid- and string-keyed overloads.
    private ConfigurationDiff? BuildComparison(ConfigurationChangeActivityData? from, ConfigurationChangeActivityData? to, int toVersion)
    {
        var fromSnapshot = ConfigurationSnapshotService.Deserialise(from?.SnapshotJson);
        var toSnapshot = ConfigurationSnapshotService.Deserialise(to?.SnapshotJson);
        if (toSnapshot == null)
            return null;

        return _application.ConfigurationDiffs.Diff(fromSnapshot, toSnapshot, from?.Version, toVersion);
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
        DateTime configurationOlderThan,
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

            // Delete Activities (spares configuration-change Activities; they have their own cutoff below)
            Log.Information("ChangeHistoryCleanup: Deleting expired activities (older than {OlderThan})", olderThan);
            result.ActivitiesDeleted = await _application.Repository.ChangeHistory.DeleteExpiredActivitiesAsync(olderThan, maxRecordsPerType);

            // Delete configuration-change Activities at their own (typically far longer) retention cutoff
            Log.Information("ChangeHistoryCleanup: Deleting expired configuration-change activities (older than {ConfigurationOlderThan})", configurationOlderThan);
            result.ConfigurationChangeActivitiesDeleted = await _application.Repository.ChangeHistory.DeleteExpiredConfigurationChangeActivitiesAsync(configurationOlderThan, maxRecordsPerType);

            // Calculate overall date range (use oldest/newest across all types)
            // Note: We can't get the exact IDs that were deleted without changing the repository methods,
            // so we'll use the olderThan date as a proxy for the date range
            if (result.CsoChangesDeleted > 0 || result.MvoChangesDeleted > 0 || result.ActivitiesDeleted > 0 || result.ConfigurationChangeActivitiesDeleted > 0)
            {
                result.OldestRecordDeleted = olderThan.AddDays(-365); // Estimate - we don't have exact oldest
                result.NewestRecordDeleted = olderThan;
            }

            // Update activity with cleanup statistics
            activity.DeletedCsoChangeCount = result.CsoChangesDeleted;
            activity.DeletedMvoChangeCount = result.MvoChangesDeleted;
            activity.DeletedActivityCount = result.ActivitiesDeleted + result.ConfigurationChangeActivitiesDeleted;
            activity.DeletedRecordsFromDate = result.OldestRecordDeleted;
            activity.DeletedRecordsToDate = result.NewestRecordDeleted;

            await _application.Activities.CompleteActivityAsync(activity);

            Log.Information("ChangeHistoryCleanup: Completed - {CsoCount} CSO changes, {MvoCount} MVO changes, {ActivityCount} activities, {ConfigurationActivityCount} configuration-change activities deleted",
                result.CsoChangesDeleted, result.MvoChangesDeleted, result.ActivitiesDeleted, result.ConfigurationChangeActivitiesDeleted);

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
