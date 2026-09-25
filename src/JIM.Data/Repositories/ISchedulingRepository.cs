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

    /// <summary>
    /// Gets a window of Schedule headers addressed by absolute <paramref name="offset"/> and
    /// <paramref name="count"/>, for the virtualised (infinite-scroll) Schedules grid. Takes the same search and
    /// sort as <see cref="GetScheduleHeadersAsync"/> and shares its query core, so the two reads can never
    /// disagree on which Schedules match.
    /// </summary>
    /// <param name="offset">The zero-based index of the first Schedule wanted; negative values read as zero.</param>
    /// <param name="count">How many Schedules are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional case-insensitive filter over name and description.</param>
    /// <param name="sortBy">Optional sort key: "name", "isEnabled", "lastRunTime", "nextRunTime"; defaults to created.</param>
    /// <param name="sortDescending">Whether the sort is descending.</param>
    /// <param name="includeTotalCount">Pass false to skip counting the whole match set when the caller already
    /// holds the total; the returned total is then null rather than zero
    /// (see <see cref="RangeResultSet{T}.TotalResults"/>).</param>
    Task<RangeResultSet<ScheduleHeader>> GetScheduleHeadersRangeAsync(
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        bool includeTotalCount = true);

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

    /// <summary>
    /// Gets a page of Schedule Executions, optionally narrowed to one Schedule and to one status. The total count
    /// is taken over the filtered set, so paging works over the matches rather than over every execution.
    /// </summary>
    /// <param name="scheduleId">Optional Schedule to narrow to; null lists every Schedule's executions.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortBy">Optional sort key: "status", "startedat"/"started", "completedat"/"completed", or
    /// the queued time (the default).</param>
    /// <param name="sortDescending">Whether the sort is descending (default: true, newest first).</param>
    /// <param name="status">Optional status to narrow to; null lists executions of every status.</param>
    Task<PagedResultSet<ScheduleExecution>> GetScheduleExecutionsAsync(
        Guid? scheduleId,
        int page,
        int pageSize,
        string? sortBy = null,
        bool sortDescending = true,
        ScheduleExecutionStatus? status = null);

    /// <summary>
    /// Gets a window of Schedule Executions addressed by absolute <paramref name="offset"/> and
    /// <paramref name="count"/>, for the virtualised (infinite-scroll) Schedule Execution grids. Takes the same
    /// filter and sort as <see cref="GetScheduleExecutionsAsync"/> and shares its query core, so the two reads
    /// can never disagree on which executions match.
    /// </summary>
    /// <param name="scheduleId">Optional Schedule to narrow to; null lists every Schedule's executions.</param>
    /// <param name="offset">The zero-based index of the first execution wanted; negative values read as zero.</param>
    /// <param name="count">How many executions are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional case-insensitive filter over the Schedule name and the initiator's
    /// name; the paged read passes none.</param>
    /// <param name="sortBy">Optional sort key: "status", "startedat"/"started", "completedat"/"completed", or
    /// the queued time (the default).</param>
    /// <param name="sortDescending">Whether the sort is descending (default: true, newest first).</param>
    /// <param name="includeTotalCount">Pass false to skip counting the whole match set when the caller already
    /// holds the total; the returned total is then null rather than zero
    /// (see <see cref="RangeResultSet{T}.TotalResults"/>).</param>
    /// <param name="status">Optional status to narrow to; null lists executions of every status.</param>
    Task<RangeResultSet<ScheduleExecution>> GetScheduleExecutionsRangeAsync(
        Guid? scheduleId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool includeTotalCount = true,
        ScheduleExecutionStatus? status = null);

    Task CreateScheduleExecutionAsync(ScheduleExecution execution);

    Task UpdateScheduleExecutionAsync(ScheduleExecution execution);

    /// <summary>
    /// Finishes starting a Schedule Execution (#1768): switches it from Queued to InProgress, sets its current step to
    /// <paramref name="firstStepIndex"/> and its start time, and releases that step group's waiting Worker Tasks to
    /// the queue, all as one atomic operation. It only takes effect while the execution is still Queued, so a
    /// cancellation made while the Schedule was starting is never overwritten and nothing is released after it.
    /// </summary>
    /// <remarks>
    /// Written as set-based updates, which bypass the change tracker, so on success the passed instance is brought
    /// into line with the new database state and, where the context tracks it, left unchanged in the tracker; a later
    /// save on the same context therefore cannot write stale values back over it.
    /// </remarks>
    /// <param name="execution">The execution to start.</param>
    /// <param name="firstStepIndex">The step index of the first step group that has Worker Tasks waiting to run.</param>
    /// <returns>True if the execution was started; false if it was no longer Queued and nothing changed.</returns>
    Task<bool> TryStartScheduleExecutionAsync(ScheduleExecution execution, int firstStepIndex);

    /// <summary>
    /// Moves an InProgress Schedule Execution on to <paramref name="nextStepIndex"/> and releases that step group's
    /// waiting Worker Tasks to the queue, as one atomic operation (#1768). It only takes effect while the execution is
    /// still InProgress and has not already reached that step, so a cancelled or finished execution stays as it is,
    /// and two callers racing to advance the same execution cannot both do so, nor move it backwards.
    /// </summary>
    /// <remarks>Brings the passed instance, and any tracked copy of it, into line on success; see
    /// <see cref="TryStartScheduleExecutionAsync"/>.</remarks>
    /// <param name="execution">The execution to advance.</param>
    /// <param name="nextStepIndex">The step index of the next step group with Worker Tasks waiting to run.</param>
    /// <returns>True if the execution advanced; false if it was not InProgress or had already reached the step.</returns>
    Task<bool> TryAdvanceScheduleExecutionAsync(ScheduleExecution execution, int nextStepIndex);

    /// <summary>
    /// Ends a Schedule Execution with <paramref name="finalStatus"/>, stamping its completion time and, when one is
    /// given, its error message, but only if its status is still one of <paramref name="fromStatuses"/> (#1768). A
    /// single conditional update, so an execution that has already finished (been cancelled, failed or completed by
    /// another caller in the meantime) stays exactly as it finished.
    /// </summary>
    /// <remarks>Brings the passed instance, and any tracked copy of it, into line on success; see
    /// <see cref="TryStartScheduleExecutionAsync"/>.</remarks>
    /// <param name="execution">The execution to end.</param>
    /// <param name="fromStatuses">The statuses the execution may be in for the change to take effect.</param>
    /// <param name="finalStatus">The status to end it with.</param>
    /// <param name="errorMessage">The reason to record, or null to leave the stored message as it is.</param>
    /// <returns>True if the execution was ended; false if its status was not one of <paramref name="fromStatuses"/>.</returns>
    Task<bool> TryFinishScheduleExecutionAsync(
        ScheduleExecution execution,
        IReadOnlyCollection<ScheduleExecutionStatus> fromStatuses,
        ScheduleExecutionStatus finalStatus,
        string? errorMessage);

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
