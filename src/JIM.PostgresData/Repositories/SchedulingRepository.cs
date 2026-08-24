// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
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
                LastExecutionErrorMessage = s.Executions.OrderByDescending(e => e.QueuedAt).Select(e => e.ErrorMessage).FirstOrDefault()
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
        // that started it (Scheduler.ProcessDueSchedulesAsync), which already does it.
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
        return await Repository.Database.ScheduleExecutions
            .Where(e => e.ScheduleId == scheduleId &&
                        e.Status == ScheduleExecutionStatus.Complete &&
                        e.StartedAt < beforeStartedAt)
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefaultAsync();
    }
}
