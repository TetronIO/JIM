// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
namespace JIM.PostgresData.Repositories;

public class SchedulingRepository : ISchedulingRepository
{
    private PostgresDataRepository Repository { get; }

    internal SchedulingRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule CRUD
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<Schedule?> GetScheduleAsync(Guid id)
    {
        return await Repository.Database.Schedules
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<Schedule?> GetScheduleWithStepsAsync(Guid id)
    {
        return await Repository.Database.Schedules
            .Include(s => s.Steps.OrderBy(st => st.StepIndex))
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<Schedule>> GetAllSchedulesAsync()
    {
        return await Repository.Database.Schedules
            .Include(s => s.Steps.OrderBy(st => st.StepIndex))
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<PagedResultSet<ScheduleHeader>> GetScheduleHeadersAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        if (pageSize > 100)
            pageSize = 100;

        var offset = (page - 1) * pageSize;
        var (results, totalCount) = await QueryScheduleHeadersByRangeAsync(
            offset, pageSize, searchQuery, sortBy, sortDescending, includeTotalCount: true);

        var pagedResultSet = new PagedResultSet<ScheduleHeader>
        {
            PageSize = pageSize,
            // The count was requested above, so it is always present here; paging cannot work without it.
            TotalResults = totalCount ?? throw new InvalidOperationException(
                "The paged Schedule header read asked for the total match count and did not receive one."),
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <summary>
    /// The largest window <see cref="GetScheduleHeadersRangeAsync"/> will return, bounding the latency of a single
    /// read. It mirrors the Schedule Execution window cap for the same reason: a page size is a number a person
    /// picked from a fixed list, whereas a virtualiser asks for however many rows the viewport needs, and a cap it
    /// can actually reach truncates the window silently, rendering the shortfall as blank rows.
    /// </summary>
    private const int MaxScheduleHeaderWindowSize = 500;

    /// <inheritdoc />
    public async Task<RangeResultSet<ScheduleHeader>> GetScheduleHeadersRangeAsync(
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        bool includeTotalCount = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (offset < 0)
            offset = 0;

        if (count > MaxScheduleHeaderWindowSize)
            count = MaxScheduleHeaderWindowSize;

        var (results, totalCount) = await QueryScheduleHeadersByRangeAsync(
            offset, count, searchQuery, sortBy, sortDescending, includeTotalCount);

        return new RangeResultSet<ScheduleHeader>
        {
            Results = results,
            TotalResults = totalCount
        };
    }

    /// <summary>
    /// Shared core for the paged and range Schedule header reads: applies the optional search and the sort, windows
    /// the result by absolute <paramref name="offset"/> and <paramref name="count"/>, and returns it alongside the
    /// total match count (or null for that total when <paramref name="includeTotalCount"/> is false). Shared so the
    /// two reads can never disagree on which Schedules match; callers own input validation and clamping.
    /// </summary>
    private async Task<(List<ScheduleHeader> Results, int? TotalResults)> QueryScheduleHeadersByRangeAsync(
        int offset,
        int count,
        string? searchQuery,
        string? sortBy,
        bool sortDescending,
        bool includeTotalCount)
    {
        // Deliberately no Include of Steps: the header carries a count, so a window of Schedules no longer
        // materialises every step row just to display "6 steps".
        var query = Repository.Database.Schedules.AsQueryable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(s =>
                s.Name.ToLower().Contains(searchLower) ||
                (s.Description != null && s.Description.ToLower().Contains(searchLower)));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "name" => sortDescending
                ? query.OrderByDescending(s => s.Name)
                : query.OrderBy(s => s.Name),
            "isenabled" or "enabled" or "status" => sortDescending
                ? query.OrderByDescending(s => s.IsEnabled)
                : query.OrderBy(s => s.IsEnabled),
            "lastruntime" or "lastrun" => sortDescending
                ? query.OrderByDescending(s => s.LastRunTime)
                : query.OrderBy(s => s.LastRunTime),
            "nextruntime" or "nextrun" => sortDescending
                ? query.OrderByDescending(s => s.NextRunTime)
                : query.OrderBy(s => s.NextRunTime),
            _ => sortDescending
                ? query.OrderByDescending(s => s.Created)
                : query.OrderBy(s => s.Created)
        };

        // Counting is the expensive half of a window read, so it only happens when the caller asks; a null total
        // means "not counted", never "nothing matched".
        int? totalCount = includeTotalCount ? await query.CountAsync() : null;

        // A local array, so the provider translates the membership test to an IN list.
        var failedOutcomes = ScheduleFailureHandling.FailedStepOutcomes.ToArray();

        // The most recent execution is projected via correlated subqueries over the Schedule's executions, ordered by
        // QueuedAt. EF Core renders each of these as a scalar subquery inside the single SELECT that fetches the
        // window ("... ORDER BY s1.QueuedAt DESC LIMIT 1"), so the whole window costs one round trip no matter how
        // many Schedules are in it; this must never become a per-row query. The composite
        // IX_ScheduleExecutions_ScheduleId_QueuedAt index turns each subquery into a backward index scan with a
        // LIMIT 1, rather than a sort over every execution the Schedule has ever had. Verified against a real
        // PostgreSQL by ScheduleHeaderQueryDatabaseTests, which counts the commands the provider actually executes.
        var results = await query
            .Skip(offset)
            .Take(count)
            .Select(s => new ScheduleHeader
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                BuiltIn = s.BuiltIn,
                IsEnabled = s.IsEnabled,
                TriggerType = s.TriggerType,
                PatternType = s.PatternType,
                CronExpression = s.CronExpression,
                DaysOfWeek = s.DaysOfWeek,
                RunTimes = s.RunTimes,
                IntervalValue = s.IntervalValue,
                IntervalUnit = s.IntervalUnit,
                IntervalWindowStart = s.IntervalWindowStart,
                IntervalWindowEnd = s.IntervalWindowEnd,
                OnStepFailure = s.OnStepFailure,
                NextRunTime = s.NextRunTime,
                LastRunTime = s.LastRunTime,
                Created = s.Created,
                LastUpdated = s.LastUpdated,
                StepCount = s.Steps.Count,
                LastExecutionId = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => (Guid?)e.Id).FirstOrDefault(),
                LastExecutionStatus = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => (ScheduleExecutionStatus?)e.Status).FirstOrDefault(),
                LastExecutionCurrentStepIndex = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => (int?)e.CurrentStepIndex).FirstOrDefault(),
                LastExecutionTotalSteps = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => (int?)e.TotalSteps).FirstOrDefault(),
                LastExecutionCompletedAt = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => e.CompletedAt).FirstOrDefault(),
                LastExecutionErrorMessage = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => e.ErrorMessage).FirstOrDefault(),
                // The steps a Complete With Error run carried on past (#1787), for the list to name. Starts from the newest
                // execution (one row, from the same backward index scan as above) and only then reaches into Activities,
                // so PostgreSQL joins that one execution to its Activities through IX_Activities_ScheduleExecutionId
                // rather than weighing every Activity against it. Empty for any other outcome: a Failed run stopped, and
                // is described by its current step index.
                LastExecutionFailedStepIndices = s.Executions
                    .OrderByDescending(e => e.QueuedAt)
                    .Take(1)
                    .Where(e => e.Status == ScheduleExecutionStatus.CompleteWithError)
                    .SelectMany(e => Repository.Database.Activities
                        .Where(a => a.ScheduleExecutionId == e.Id &&
                                    a.ScheduleStepIndex != null &&
                                    failedOutcomes.Contains(a.Status)))
                    .Select(a => a.ScheduleStepIndex!.Value)
                    .Distinct()
                    .OrderBy(i => i)
                    .ToArray()
            })
            .ToListAsync();

        return (results, totalCount);
    }

    public async Task CreateScheduleAsync(Schedule schedule)
    {
        Repository.Database.Schedules.Add(schedule);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateScheduleAsync(Schedule schedule)
    {
        schedule.LastUpdated = DateTime.UtcNow;
        Repository.Database.Schedules.Update(schedule);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteScheduleAsync(Schedule schedule)
    {
        Repository.Database.Schedules.Remove(schedule);
        await Repository.Database.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Step CRUD
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<ScheduleStep?> GetScheduleStepAsync(Guid id)
    {
        return await Repository.Database.ScheduleSteps
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    public async Task<List<ScheduleStep>> GetScheduleStepsAsync(Guid scheduleId)
    {
        return await Repository.Database.ScheduleSteps
            .Where(s => s.ScheduleId == scheduleId)
            .OrderBy(s => s.StepIndex)
            .ToListAsync();
    }

    public async Task CreateScheduleStepAsync(ScheduleStep step)
    {
        Repository.Database.ScheduleSteps.Add(step);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateScheduleStepAsync(ScheduleStep step)
    {
        Repository.Database.ScheduleSteps.Update(step);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteScheduleStepAsync(ScheduleStep step)
    {
        Repository.Database.ScheduleSteps.Remove(step);
        await Repository.Database.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Execution
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<ScheduleExecution?> GetScheduleExecutionAsync(Guid id)
    {
        return await Repository.Database.ScheduleExecutions
            .SingleOrDefaultAsync(e => e.Id == id);
    }

    public async Task<ScheduleExecution?> GetScheduleExecutionWithScheduleAsync(Guid id)
    {
        return await Repository.Database.ScheduleExecutions
            .Include(e => e.Schedule)
            .ThenInclude(s => s.Steps.OrderBy(st => st.StepIndex))
            .SingleOrDefaultAsync(e => e.Id == id);
    }

    public async Task<List<ScheduleExecution>> GetActiveScheduleExecutionsAsync()
    {
        return await Repository.Database.ScheduleExecutions
            .Include(e => e.Schedule)
            .Where(e => e.Status == ScheduleExecutionStatus.Queued ||
                        e.Status == ScheduleExecutionStatus.InProgress ||
                        e.Status == ScheduleExecutionStatus.Paused)
            .OrderBy(e => e.QueuedAt)
            .ToListAsync();
    }

    public async Task<PagedResultSet<ScheduleExecution>> GetScheduleExecutionsAsync(
        Guid? scheduleId,
        int page,
        int pageSize,
        string? sortBy = null,
        bool sortDescending = true)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        if (pageSize > 100)
            pageSize = 100;

        var offset = (page - 1) * pageSize;
        var (results, totalCount) = await QueryScheduleExecutionsByRangeAsync(
            scheduleId, offset, pageSize, searchQuery: null, sortBy, sortDescending, includeTotalCount: true);

        var pagedResultSet = new PagedResultSet<ScheduleExecution>
        {
            PageSize = pageSize,
            // The count was requested above, so it is always present here; paging cannot work without it.
            TotalResults = totalCount ?? throw new InvalidOperationException(
                "The paged Schedule Execution read asked for the total match count and did not receive one."),
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <summary>
    /// The largest window <see cref="GetScheduleExecutionsRangeAsync"/> will return, bounding the latency of a
    /// single read. It is deliberately five times the paged reader's page-size cap, because the two caps protect
    /// against different things: a page size is a number a person picked from a fixed list and never approaches
    /// 100, whereas a virtualiser asks for however many rows the viewport needs, and a cap it can actually reach
    /// truncates the window silently, rendering the shortfall as blank rows rather than raising anything. The
    /// derivation from the list grid's height and row-height arithmetic lives on
    /// <c>MetaverseRepository.MaxHeaderWindowSize</c>, which this cap mirrors.
    /// </summary>
    private const int MaxExecutionWindowSize = 500;

    /// <inheritdoc />
    public async Task<RangeResultSet<ScheduleExecution>> GetScheduleExecutionsRangeAsync(
        Guid? scheduleId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool includeTotalCount = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (offset < 0)
            offset = 0;

        if (count > MaxExecutionWindowSize)
            count = MaxExecutionWindowSize;

        var (results, totalCount) = await QueryScheduleExecutionsByRangeAsync(
            scheduleId, offset, count, searchQuery, sortBy, sortDescending, includeTotalCount);

        return new RangeResultSet<ScheduleExecution>
        {
            Results = results,
            TotalResults = totalCount
        };
    }

    /// <summary>
    /// Shared core for the paged and range Schedule Execution reads: applies the optional Schedule filter and
    /// the sort, windows the result by absolute <paramref name="offset"/> and <paramref name="count"/>, and
    /// returns it alongside the total match count (or null for that total when
    /// <paramref name="includeTotalCount"/> is false). Shared so the two reads can never disagree on which
    /// executions match; callers own input validation and clamping.
    /// </summary>
    private async Task<(List<ScheduleExecution> Results, int? TotalResults)> QueryScheduleExecutionsByRangeAsync(
        Guid? scheduleId,
        int offset,
        int count,
        string? searchQuery,
        string? sortBy,
        bool sortDescending,
        bool includeTotalCount)
    {
        var query = Repository.Database.ScheduleExecutions
            .Include(e => e.Schedule)
            .AsQueryable();

        // Filter by schedule if specified
        if (scheduleId.HasValue)
        {
            var scheduleIdValue = scheduleId.Value;
            query = query.Where(e => e.ScheduleId == scheduleIdValue);
        }

        // Apply search filter. The two names are what a reader can actually recognise an execution by: which
        // Schedule ran, and who set it off.
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(e =>
                e.ScheduleName.ToLower().Contains(searchLower) ||
                (e.InitiatedByName != null && e.InitiatedByName.ToLower().Contains(searchLower)));
        }

        // Apply sorting
        var ordered = sortBy?.ToLower() switch
        {
            "status" => sortDescending
                ? query.OrderByDescending(e => e.Status)
                : query.OrderBy(e => e.Status),
            "startedat" or "started" => sortDescending
                ? query.OrderByDescending(e => e.StartedAt)
                : query.OrderBy(e => e.StartedAt),
            "completedat" or "completed" => sortDescending
                ? query.OrderByDescending(e => e.CompletedAt)
                : query.OrderBy(e => e.CompletedAt),
            _ => sortDescending
                ? query.OrderByDescending(e => e.QueuedAt)
                : query.OrderBy(e => e.QueuedAt)
        };

        // Deterministic tie-break: Skip/Take windows are only stable under a total order, and every sort key
        // above can tie (a Schedule that fans several executions into the queue at once stamps them all with the
        // same queued time, and a never-started execution has a null started and completed time). Without it,
        // PostgreSQL may order tied rows differently per window, repeating some executions and skipping others.
        query = ordered.ThenBy(e => e.Id);

        // Counting scans every matching execution rather than a window of them, so it is skipped entirely when
        // the caller already holds the total. Sorting cannot change how many executions match.
        int? totalCount = null;
        if (includeTotalCount)
            totalCount = await query.CountAsync();

        var results = await query.Skip(offset).Take(count).ToListAsync();
        return (results, totalCount);
    }

    public async Task CreateScheduleExecutionAsync(ScheduleExecution execution)
    {
        Repository.Database.ScheduleExecutions.Add(execution);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateScheduleExecutionAsync(ScheduleExecution execution)
    {
        Repository.Database.ScheduleExecutions.Update(execution);
        await Repository.Database.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<bool> TryStartScheduleExecutionAsync(ScheduleExecution execution, int firstStepIndex)
    {
        var executionId = execution.Id;
        var startedAt = DateTime.UtcNow;

        var started = await ReleaseStepGroupAsync(executionId, firstStepIndex, () => Repository.Database.ScheduleExecutions
            .Where(e => e.Id == executionId && e.Status == ScheduleExecutionStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ScheduleExecutionStatus.InProgress)
                .SetProperty(e => e.CurrentStepIndex, firstStepIndex)
                .SetProperty(e => e.StartedAt, startedAt)));

        if (started)
        {
            ApplyCommittedValues(execution, e =>
            {
                e.Status = ScheduleExecutionStatus.InProgress;
                e.CurrentStepIndex = firstStepIndex;
                e.StartedAt = startedAt;
            }, nameof(ScheduleExecution.Status), nameof(ScheduleExecution.CurrentStepIndex), nameof(ScheduleExecution.StartedAt));
        }

        return started;
    }

    /// <inheritdoc />
    public async Task<bool> TryAdvanceScheduleExecutionAsync(ScheduleExecution execution, int nextStepIndex)
    {
        var executionId = execution.Id;

        // Advancing only ever moves forwards. The Worker and the Scheduler's safety net can reach the same decision
        // for the same execution at the same moment; this makes the second of them a no-op rather than a second
        // release, and stops a caller acting on a stale reading from moving the execution backwards.
        var advanced = await ReleaseStepGroupAsync(executionId, nextStepIndex, () => Repository.Database.ScheduleExecutions
            .Where(e => e.Id == executionId && e.Status == ScheduleExecutionStatus.InProgress && e.CurrentStepIndex < nextStepIndex)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CurrentStepIndex, nextStepIndex)));

        if (advanced)
            ApplyCommittedValues(execution, e => e.CurrentStepIndex = nextStepIndex, nameof(ScheduleExecution.CurrentStepIndex));

        return advanced;
    }

    /// <inheritdoc />
    public async Task<bool> TryFinishScheduleExecutionAsync(
        ScheduleExecution execution,
        IReadOnlyCollection<ScheduleExecutionStatus> fromStatuses,
        ScheduleExecutionStatus finalStatus,
        string? errorMessage)
    {
        var executionId = execution.Id;
        var allowedStatuses = fromStatuses.ToList();
        var completedAt = DateTime.UtcNow;

        // One conditional statement: whichever of two competing callers (a step finishing, an administrator
        // cancelling, the safety net) reaches the row first decides how the execution ended, and the other finds its
        // condition no longer true and changes nothing.
        var updated = await Repository.Database.ScheduleExecutions
            .Where(e => e.Id == executionId && allowedStatuses.Contains(e.Status))
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(e => e.Status, finalStatus);
                s.SetProperty(e => e.CompletedAt, completedAt);
                if (errorMessage != null)
                    s.SetProperty(e => e.ErrorMessage, errorMessage);
            });

        if (updated == 0)
            return false;

        var changedProperties = errorMessage != null
            ? new[] { nameof(ScheduleExecution.Status), nameof(ScheduleExecution.CompletedAt), nameof(ScheduleExecution.ErrorMessage) }
            : new[] { nameof(ScheduleExecution.Status), nameof(ScheduleExecution.CompletedAt) };

        ApplyCommittedValues(execution, e =>
        {
            e.Status = finalStatus;
            e.CompletedAt = completedAt;
            if (errorMessage != null)
                e.ErrorMessage = errorMessage;
        }, changedProperties);

        return true;
    }

    /// <summary>
    /// The shared core of starting and advancing an execution: runs the caller's conditional update of the execution
    /// row, and only if it matched, moves the step group's waiting Worker Tasks to Queued, both inside one
    /// transaction. Reuses an ambient transaction where there is one (Npgsql does not nest them) and commits only a
    /// transaction it began itself.
    /// </summary>
    /// <remarks>
    /// Deliberately two small set-based statements rather than a transaction around the whole start. A rolled-back
    /// transaction does not roll back the change tracker, and the Scheduler reuses one context for its whole polling
    /// cycle (the same Schedule is saved again afterwards to advance its next run time), so a wide transaction that
    /// rolled back could leave tracked inserts behind to fail every later save on that context (#1765). Set-based
    /// statements add nothing to the tracker to go stale.
    /// </remarks>
    /// <returns>True if the execution row matched and the group was released; false if nothing changed.</returns>
    private async Task<bool> ReleaseStepGroupAsync(Guid executionId, int stepIndex, Func<Task<int>> updateExecutionAsync)
    {
        var database = Repository.Database.Database;
        var ownsTransaction = database.CurrentTransaction == null;
        await using var transaction = ownsTransaction ? await database.BeginTransactionAsync() : null;

        // No match means the execution is no longer in the state the caller saw. Nothing has been written, so an
        // owned transaction simply rolls back when it is disposed.
        if (await updateExecutionAsync() == 0)
            return false;

        await Repository.Database.WorkerTasks
            .Where(t => t.ScheduleExecutionId == executionId
                        && t.ScheduleStepIndex == stepIndex
                        && t.Status == WorkerTaskStatus.WaitingForPreviousStep)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, WorkerTaskStatus.Queued));

        if (ownsTransaction)
            await transaction!.CommitAsync();

        return true;
    }

    /// <summary>
    /// Brings an execution in memory into line with a set-based update that has just been written, which bypassed the
    /// change tracker. Applies the new values to the caller's instance and to whichever instance this context tracks
    /// for the same row, and accepts them as that tracked instance's original values, so the tracker sees the row as
    /// unchanged. Without that last step a later save on the same context (the Scheduler saving a Schedule's next run
    /// time, say) would detect the difference and write these values back over whatever has happened to the row since,
    /// such as the Worker completing the execution.
    /// </summary>
    private void ApplyCommittedValues(ScheduleExecution execution, Action<ScheduleExecution> apply, params string[] propertyNames)
    {
        apply(execution);

        // Reading the tracker must not trigger DetectChanges: on a long-lived context that can attach unrelated
        // untracked graphs (see "Tracker surgery must not trigger DetectChanges" in src/CLAUDE.md).
        var changeTracker = Repository.Database.ChangeTracker;
        var autoDetectChanges = changeTracker.AutoDetectChangesEnabled;
        changeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entry = Repository.Database.ScheduleExecutions.Local.FindEntry(execution.Id);
            if (entry == null)
                return;

            if (!ReferenceEquals(entry.Entity, execution))
                apply(entry.Entity);

            foreach (var propertyName in propertyNames)
            {
                var property = entry.Property(propertyName);
                property.OriginalValue = property.CurrentValue;
                property.IsModified = false;
            }
        }
        finally
        {
            changeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Scheduler Service Queries
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<List<Schedule>> GetDueSchedulesAsync(DateTime asOf)
    {
        return await Repository.Database.Schedules
            .Include(s => s.Steps.OrderBy(st => st.StepIndex))
            .Where(s => s.IsEnabled &&
                        s.NextRunTime.HasValue &&
                        s.NextRunTime.Value <= asOf)
            .OrderBy(s => s.NextRunTime)
            .ToListAsync();
    }

    public async Task<List<Schedule>> GetSchedulesForNextRunCalculationAsync()
    {
        // Deliberately only schedules with NO next run time: this bootstraps a newly created, newly enabled or
        // newly cron-triggered schedule, and nothing else. Advancing one that has just run belongs to the code
        // that started it (SchedulerServer.StartDueSchedulesAsync), which already does it.
        //
        // This used to include schedules whose next run time had already arrived, which meant the scheduler's
        // polling cycle recomputed the time into the future in step 1 and then found nothing due in step 2, on
        // the exact cycle each schedule became due, on every cycle. No cron-triggered schedule ever fired.
        // See SchedulingRepositoryDueScheduleTests for the invariant between this query and GetDueSchedulesAsync.
        return await Repository.Database.Schedules
            .Where(s => s.IsEnabled &&
                        s.TriggerType != ScheduleTriggerType.Manual &&
                        s.NextRunTime == null)
            .ToListAsync();
    }

    public async Task<ScheduleExecution?> GetLastCompletedScheduleExecutionAsync(Guid scheduleId, DateTime beforeStartedAt)
    {
        // Complete only, deliberately: Complete With Error (#1787) is finished everywhere else, but a run that carried on
        // past a failed step may have missed part of its window, so it must not move the Temporal Scope Reconciliation
        // watermark forward. The next clean run's watermark then covers that window again.
        return await Repository.Database.ScheduleExecutions
            .Where(e => e.ScheduleId == scheduleId &&
                        e.Status == ScheduleExecutionStatus.Complete &&
                        e.StartedAt < beforeStartedAt)
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefaultAsync();
    }
}
