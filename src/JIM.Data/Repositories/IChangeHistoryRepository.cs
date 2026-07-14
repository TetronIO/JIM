// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// Deletes expired Activity records older than the specified date, sparing configuration-change Activities
    /// (those carrying a versioned configuration snapshot) and Authentication (security event) Activities, both of
    /// which are governed by their own, separate retention periods.
    /// </summary>
    Task<int> DeleteExpiredActivitiesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Deletes expired configuration-change Activities (those carrying a versioned configuration snapshot) older
    /// than the specified date. The general Activity cleanup never touches these; this is the only path that
    /// removes configuration change history.
    /// </summary>
    Task<int> DeleteExpiredConfigurationChangeActivitiesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Deletes expired security event Activities (TargetType Authentication: interactive sign-in success/failure,
    /// API key authentication failure) older than the specified date. The general Activity cleanup never touches
    /// these; this is the only path that removes security event history.
    /// </summary>
    Task<int> DeleteExpiredSecurityEventActivitiesAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Gets the count of CSO change records for a specific Connected System.
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
