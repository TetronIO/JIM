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
    /// Removes the results of expired configuration change previews (#827): the preview row, its summary groups and
    /// its delta rows, for previews whose Activity is older than the cutoff and would itself be removed by
    /// <see cref="DeleteExpiredActivitiesAsync"/>.
    ///
    /// The rows cascade from the Activity, so this path deletes nothing the Activity cleanup would not. What it adds
    /// is a bound: the batch limit counts Activities, and a single preview Activity can own hundreds of thousands of
    /// delta rows, so a full batch of them would cascade in one statement regardless of the limit. Run this first,
    /// bounded by the same limit, and each pass stays the size the limit was chosen for.
    ///
    /// The Activity itself is deliberately left behind. A preview whose results have gone reads as "no longer
    /// available", which is honest; removing the Activity here would delete a run record this pass had not budgeted
    /// for, under a limit meant for something else.
    /// </summary>
    /// <param name="maxRecords">Maximum previews (not rows) to clear in this batch.</param>
    /// <returns>Count of previews whose results were removed.</returns>
    Task<int> DeleteExpiredPreviewsAsync(DateTime olderThan, int maxRecords);

    /// <summary>
    /// Deletes expired Activity records older than the specified date, sparing configuration-change Activities
    /// (those carrying a versioned configuration snapshot), Authentication (security event) Activities, and
    /// Password Synchronisation Activities, each of which is governed by its own, separate retention period.
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
    /// Deletes expired Password Synchronisation Activities (TargetType PasswordSynchronisation: the delivery
    /// passes, the fan-out records, and the per-system outcome children) older than the specified date. The
    /// general Activity cleanup never touches these; this is the only path that removes password history.
    /// <para>
    /// Its own retention class because the question these answer ("was this person's password ever set in that
    /// system, and if not, why?") is asked long after the sync history around them stops being interesting.
    /// </para>
    /// </summary>
    Task<int> DeleteExpiredPasswordEventActivitiesAsync(DateTime olderThan, int maxRecords);

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
