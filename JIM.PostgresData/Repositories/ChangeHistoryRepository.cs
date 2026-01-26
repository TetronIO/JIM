using JIM.Data.Repositories;
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
    /// Deletes expired Activity records older than the specified date.
    /// </summary>
    /// <param name="olderThan">Delete records with Created date older than this date</param>
    /// <param name="maxRecords">Maximum number of records to delete in this batch</param>
    /// <returns>Count of deleted records</returns>
    public async Task<int> DeleteExpiredActivitiesAsync(DateTime olderThan, int maxRecords)
    {
        var recordsToDelete = await _database.Activities
            .Where(a => a.Created < olderThan)
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
    /// Gets the count of CSO change records for a specific connected system.
    /// </summary>
    /// <param name="connectedSystemId">Connected system ID</param>
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
