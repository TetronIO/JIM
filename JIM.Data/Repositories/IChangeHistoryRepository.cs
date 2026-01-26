namespace JIM.Data.Repositories;

public interface IChangeHistoryRepository
{
    /// <summary>
    /// Deletes expired CSO change records older than the specified date.
    /// </summary>
    Task<int> DeleteExpiredCsoChangesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Deletes expired MVO change records older than the specified date.
    /// </summary>
    Task<int> DeleteExpiredMvoChangesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Deletes expired Activity records older than the specified date.
    /// </summary>
    Task<int> DeleteExpiredActivitiesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Gets the count of CSO change records for a specific connected system.
    /// </summary>
    Task<int> GetCsoChangeCountAsync(int connectedSystemId);

    /// <summary>
    /// Gets the date range of CSO change records.
    /// </summary>
    Task<(DateTime? oldest, DateTime? newest)?> GetCsoChangeDateRangeAsync(List<Guid> recordIds);

    /// <summary>
    /// Gets the date range of MVO change records.
    /// </summary>
    Task<(DateTime? oldest, DateTime? newest)?> GetMvoChangeDateRangeAsync(List<Guid> recordIds);

    /// <summary>
    /// Gets the date range of Activity records.
    /// </summary>
    Task<(DateTime? oldest, DateTime? newest)?> GetActivityDateRangeAsync(List<Guid> recordIds);
}
