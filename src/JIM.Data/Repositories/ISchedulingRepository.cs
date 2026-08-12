// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

public interface ISchedulingRepository
{
    // -----------------------------------------------------------------------------------------------------------------
    // Schedule CRUD
    // -----------------------------------------------------------------------------------------------------------------

    Task<Schedule?> GetScheduleAsync(Guid id);

    Task<Schedule?> GetScheduleWithStepsAsync(Guid id);

    Task<List<Schedule>> GetAllSchedulesAsync();

    /// <summary>
    /// Gets a page of Schedules projected into lightweight headers, each carrying its step count and the outcome of
    /// its most recent execution. The last-execution fields are projected in the same query, so a page costs one
    /// round trip rather than one query per Schedule.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page (capped at 100).</param>
    /// <param name="searchQuery">Optional case-insensitive filter over name and description.</param>
    /// <param name="sortBy">Optional field to sort by (name, isEnabled, lastRunTime, nextRunTime); defaults to created.</param>
    /// <param name="sortDescending">Whether to sort in descending order.</param>
    Task<PagedResultSet<ScheduleHeader>> GetScheduleHeadersAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false);

    Task CreateScheduleAsync(Schedule schedule);

    Task UpdateScheduleAsync(Schedule schedule);

    Task DeleteScheduleAsync(Schedule schedule);

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Step CRUD
    // -----------------------------------------------------------------------------------------------------------------

    Task<ScheduleStep?> GetScheduleStepAsync(Guid id);

    Task<List<ScheduleStep>> GetScheduleStepsAsync(Guid scheduleId);

    Task CreateScheduleStepAsync(ScheduleStep step);

    Task UpdateScheduleStepAsync(ScheduleStep step);

    Task DeleteScheduleStepAsync(ScheduleStep step);

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Execution
    // -----------------------------------------------------------------------------------------------------------------

    Task<ScheduleExecution?> GetScheduleExecutionAsync(Guid id);

    Task<ScheduleExecution?> GetScheduleExecutionWithScheduleAsync(Guid id);

    Task<List<ScheduleExecution>> GetActiveScheduleExecutionsAsync();

    Task<PagedResultSet<ScheduleExecution>> GetScheduleExecutionsAsync(
        Guid? scheduleId,
        int page,
        int pageSize,
        string? sortBy = null,
        bool sortDescending = true);

    /// <summary>
    /// Gets a window of Schedule Executions addressed by absolute <paramref name="offset"/> and
    /// <paramref name="count"/>, for the virtualised (infinite-scroll) Schedule Execution grids. Takes the same
    /// filter and sort as <see cref="GetScheduleExecutionsAsync"/> and shares its query core, so the two reads
    /// can never disagree on which executions match.
    /// </summary>
    /// <param name="scheduleId">Optional Schedule to narrow to; null lists every Schedule's executions.</param>
    /// <param name="offset">The zero-based index of the first execution wanted; negative values read as zero.</param>
    /// <param name="count">How many executions are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="sortBy">Optional sort key: "status", "startedat"/"started", "completedat"/"completed", or
    /// the queued time (the default).</param>
    /// <param name="sortDescending">Whether the sort is descending (default: true, newest first).</param>
    /// <param name="includeTotalCount">Pass false to skip counting the whole match set when the caller already
    /// holds the total; the returned total is then null rather than zero
    /// (see <see cref="RangeResultSet{T}.TotalResults"/>).</param>
    Task<RangeResultSet<ScheduleExecution>> GetScheduleExecutionsRangeAsync(
        Guid? scheduleId,
        int offset,
        int count,
        string? sortBy = null,
        bool sortDescending = true,
        bool includeTotalCount = true);

    Task CreateScheduleExecutionAsync(ScheduleExecution execution);

    Task UpdateScheduleExecutionAsync(ScheduleExecution execution);

    // -----------------------------------------------------------------------------------------------------------------
    // Scheduler Service Queries
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Gets all enabled schedules that are due to run (NextRunTime <= now).
    /// </summary>
    Task<List<Schedule>> GetDueSchedulesAsync(DateTime asOf);

    /// <summary>
    /// Gets all schedules that need their NextRunTime recalculated.
    /// </summary>
    Task<List<Schedule>> GetSchedulesForNextRunCalculationAsync();

    /// <summary>
    /// Gets the most recent successfully completed execution of a schedule that started before the given instant.
    /// Used by the Temporal Scope Reconciler (issue #892) to derive its failure-safe watermark: the previous
    /// successful sweep's start time. Returns null when there is no prior completed execution (bootstrap sweep).
    /// </summary>
    Task<ScheduleExecution?> GetLastCompletedScheduleExecutionAsync(Guid scheduleId, DateTime beforeStartedAt);
}
