// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public class ChangeHistoryRepository : IChangeHistoryRepository
{
    private readonly JimDbContext _database;

    public ChangeHistoryRepository(JimDbContext database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Deletes expired CSO change records older than the specified date.
    /// </summary>
    /// <param name="olderThan">Delete records with ChangeTime older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredCsoChangesAsync(DateTime olderThan, int maxRecords)
    {
        var recordsToDelete = await _database.ConnectedSystemObjectChanges
            .AsTracking()
            .Where(c => c.ChangeTime < olderThan)
            .OrderBy(c => c.ChangeTime)
            .Take(maxRecords)
            .ToListAsync();

        if (recordsToDelete.Count == 0)
            return 0;

        _database.ConnectedSystemObjectChanges.RemoveRange(recordsToDelete);
        await _database.SaveChangesAsync();

        return recordsToDelete.Count;
    }

    /// <summary>
    /// Deletes expired MVO change records older than the specified date.
    /// </summary>
    /// <param name="olderThan">Delete records with ChangeTime older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredMvoChangesAsync(DateTime olderThan, int maxRecords)
    {
        var recordsToDelete = await _database.MetaverseObjectChanges
            .AsTracking()
            .Where(c => c.ChangeTime < olderThan)
            .OrderBy(c => c.ChangeTime)
            .Take(maxRecords)
            .ToListAsync();

        if (recordsToDelete.Count == 0)
            return 0;

        _database.MetaverseObjectChanges.RemoveRange(recordsToDelete);
        await _database.SaveChangesAsync();

        return recordsToDelete.Count;
    }

    /// <summary>
    /// Clears the results of expired configuration change previews, bounded by preview rather than by row. See the
    /// interface for why this runs ahead of the Activity cleanup that would otherwise cascade the same rows.
    /// </summary>
    public async Task<int> DeleteExpiredPreviewsAsync(DateTime olderThan, int maxRecords)
    {
        // Selected with exactly the predicate the general Activity cleanup uses, so this only ever clears previews
        // whose Activity is genuinely on its way out. Clearing one that is being retained would leave a preview
        // showing exact group counts with nothing behind them, which reads as "no detail was kept" rather than "the
        // detail aged out".
        var expiredPreviewIds = await _database.ConfigurationChangePreviews
            .Where(p => _database.Activities.Any(a =>
                a.Id == p.ActivityId
                && a.Created < olderThan
                && a.ConfigurationChangeVersion == null
                && a.TargetType != ActivityTargetType.Authentication))
            .OrderBy(p => p.ActivityId)
            .Take(maxRecords)
            .Select(p => p.ActivityId)
            .ToListAsync();

        if (expiredPreviewIds.Count == 0)
            return 0;

        // Deleted in dependency order rather than left to the cascade, so the row counts are this method's to
        // report; every statement is bounded by the preview list above. Nothing in this context tracks preview
        // rows (no read path loads them during housekeeping), so there are no tracked instances to detach.
        var deltas = await _database.ConfigurationChangePreviewDeltas
            .Where(d => expiredPreviewIds.Contains(d.ActivityId)).ExecuteDeleteAsync();
        var groups = await _database.ConfigurationChangePreviewGroups
            .Where(g => expiredPreviewIds.Contains(g.ActivityId)).ExecuteDeleteAsync();
        var previews = await _database.ConfigurationChangePreviews
            .Where(p => expiredPreviewIds.Contains(p.ActivityId)).ExecuteDeleteAsync();

        Serilog.Log.Debug("DeleteExpiredPreviewsAsync: Removed {Previews} previews, {Groups} summary groups and {Deltas} object-level rows",
            previews, groups, deltas);

        return previews;
    }

    /// <summary>
    /// Deletes expired Activity records older than the specified date. Configuration-change Activities (those
    /// carrying a versioned configuration snapshot) are spared: they ARE the configuration change history and are
    /// governed by their own, longer retention period via <see cref="DeleteExpiredConfigurationChangeActivitiesAsync"/>.
    /// Authentication (security event) Activities are likewise spared, governed by their own retention period via
    /// <see cref="DeleteExpiredSecurityEventActivitiesAsync"/>, as are Password Synchronisation Activities, via
    /// <see cref="DeleteExpiredPasswordEventActivitiesAsync"/>.
    /// </summary>
    /// <param name="olderThan">Delete records with Created date older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredActivitiesAsync(DateTime olderThan, int maxRecords)
    {
        var recordsToDelete = await _database.Activities
            .AsTracking()
            .Where(a => a.Created < olderThan && a.ConfigurationChangeVersion == null &&
                        a.TargetType != ActivityTargetType.Authentication &&
                        a.TargetType != ActivityTargetType.PasswordSynchronisation)
            .OrderBy(a => a.Created)
            .Take(maxRecords)
            .ToListAsync();

        if (recordsToDelete.Count == 0)
            return 0;

        _database.Activities.RemoveRange(recordsToDelete);
        await _database.SaveChangesAsync();

        return recordsToDelete.Count;
    }

    /// <summary>
    /// Deletes expired configuration-change Activities (those carrying a versioned configuration snapshot) older
    /// than the specified date. This is the only path that removes configuration change history.
    /// </summary>
    /// <param name="olderThan">Delete records with Created date older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredConfigurationChangeActivitiesAsync(DateTime olderThan, int maxRecords)
    {
        var recordsToDelete = await _database.Activities
            .AsTracking()
            .Where(a => a.Created < olderThan && a.ConfigurationChangeVersion != null)
            .OrderBy(a => a.Created)
            .Take(maxRecords)
            .ToListAsync();

        if (recordsToDelete.Count == 0)
            return 0;

        _database.Activities.RemoveRange(recordsToDelete);
        await _database.SaveChangesAsync();

        return recordsToDelete.Count;
    }

    /// <summary>
    /// Deletes expired security event Activities (TargetType Authentication) older than the specified date. This is
    /// the only path that removes security event history.
    /// </summary>
    /// <param name="olderThan">Delete records with Created date older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredSecurityEventActivitiesAsync(DateTime olderThan, int maxRecords)
    {
        var recordsToDelete = await _database.Activities
            .AsTracking()
            .Where(a => a.Created < olderThan && a.TargetType == ActivityTargetType.Authentication)
            .OrderBy(a => a.Created)
            .Take(maxRecords)
            .ToListAsync();

        if (recordsToDelete.Count == 0)
            return 0;

        _database.Activities.RemoveRange(recordsToDelete);
        await _database.SaveChangesAsync();

        return recordsToDelete.Count;
    }

    /// <summary>
    /// Deletes expired Password Synchronisation Activities (TargetType PasswordSynchronisation) older than the
    /// specified date. This is the only path that removes Password Synchronisation history.
    /// </summary>
    /// <param name="olderThan">Delete records with Created date older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredPasswordEventActivitiesAsync(DateTime olderThan, int maxRecords)
    {
        // Oldest-first, like every other trim here, which also keeps a fan-out parent and its per-system outcome
        // children close together in the ordering: they are created within moments of one another, so a batch
        // boundary rarely falls between them, and ParentActivityId is a plain column rather than a foreign key,
        // so one crossing it leaves no broken reference behind.
        var recordsToDelete = await _database.Activities
            .AsTracking()
            .Where(a => a.Created < olderThan && a.TargetType == ActivityTargetType.PasswordSynchronisation)
            .OrderBy(a => a.Created)
            .Take(maxRecords)
            .ToListAsync();

        if (recordsToDelete.Count == 0)
            return 0;

        _database.Activities.RemoveRange(recordsToDelete);
        await _database.SaveChangesAsync();

        return recordsToDelete.Count;
    }

    /// <summary>
    /// Gets the count of CSO change records for a specific Connected System.
    /// </summary>
    /// <param name="connectedSystemId">Connected System ID</param>
    /// <returns>Count of CSO change records</returns>
    public async Task<int> GetCsoChangeCountAsync(int connectedSystemId)
    {
        return await _database.ConnectedSystemObjectChanges
            .Where(c => c.ConnectedSystemId == connectedSystemId)
            .CountAsync();
    }

    /// <summary>
    /// Gets the date range of CSO change records.
    /// </summary>
    /// <param name="recordIds">List of record IDs to analyze</param>
    /// <returns>Tuple of (oldest, newest) change times, or null if no records</returns>
    public async Task<(DateTime? oldest, DateTime? newest)?> GetCsoChangeDateRangeAsync(List<Guid> recordIds)
    {
        if (recordIds.Count == 0)
            return null;

        var minMax = await _database.ConnectedSystemObjectChanges
            .Where(c => recordIds.Contains(c.Id))
            .GroupBy(c => 1)
            .Select(g => new
            {
                Oldest = g.Min(c => c.ChangeTime),
                Newest = g.Max(c => c.ChangeTime)
            })
            .FirstOrDefaultAsync();

        return minMax == null ? null : (minMax.Oldest, minMax.Newest);
    }

    /// <summary>
    /// Gets the date range of MVO change records.
    /// </summary>
    /// <param name="recordIds">List of record IDs to analyze</param>
    /// <returns>Tuple of (oldest, newest) change times, or null if no records</returns>
    public async Task<(DateTime? oldest, DateTime? newest)?> GetMvoChangeDateRangeAsync(List<Guid> recordIds)
    {
        if (recordIds.Count == 0)
            return null;

        var minMax = await _database.MetaverseObjectChanges
            .Where(c => recordIds.Contains(c.Id))
            .GroupBy(c => 1)
            .Select(g => new
            {
                Oldest = g.Min(c => c.ChangeTime),
                Newest = g.Max(c => c.ChangeTime)
            })
            .FirstOrDefaultAsync();

        return minMax == null ? null : (minMax.Oldest, minMax.Newest);
    }

    /// <summary>
    /// Gets the date range of Activity records.
    /// </summary>
    /// <param name="recordIds">List of record IDs to analyze</param>
    /// <returns>Tuple of (oldest, newest) created times, or null if no records</returns>
    public async Task<(DateTime? oldest, DateTime? newest)?> GetActivityDateRangeAsync(List<Guid> recordIds)
    {
        if (recordIds.Count == 0)
            return null;

        var minMax = await _database.Activities
            .Where(a => recordIds.Contains(a.Id))
            .GroupBy(a => 1)
            .Select(g => new
            {
                Oldest = g.Min(a => a.Created),
                Newest = g.Max(a => a.Created)
            })
            .FirstOrDefaultAsync();

        return minMax == null ? null : (minMax.Oldest, minMax.Newest);
    }
}
