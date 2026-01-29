using JIM.Data.Repositories;
using JIM.Models.Scheduling;
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

    public async Task<Schedule?> GetScheduleWithStepsAsNoTrackingAsync(Guid id)
    {
        return await Repository.Database.Schedules
            .AsNoTracking()
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

    public async Task<PagedResultSet<Schedule>> GetSchedulesAsync(
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

        var query = Repository.Database.Schedules
            .Include(s => s.Steps)
            .AsQueryable();

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
            "isenabled" or "enabled" => sortDescending
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

        var totalCount = await query.CountAsync();
        var offset = (page - 1) * pageSize;
        var results = await query.Skip(offset).Take(pageSize).ToListAsync();

        var pagedResultSet = new PagedResultSet<Schedule>
        {
            PageSize = pageSize,
            TotalResults = totalCount,
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

        var query = Repository.Database.ScheduleExecutions
            .Include(e => e.Schedule)
            .AsQueryable();

        // Filter by schedule if specified
        if (scheduleId.HasValue)
        {
            query = query.Where(e => e.ScheduleId == scheduleId.Value);
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
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

        var totalCount = await query.CountAsync();
        var offset = (page - 1) * pageSize;
        var results = await query.Skip(offset).Take(pageSize).ToListAsync();

        var pagedResultSet = new PagedResultSet<ScheduleExecution>
        {
            PageSize = pageSize,
            TotalResults = totalCount,
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
        return await Repository.Database.Schedules
            .Where(s => s.IsEnabled &&
                        s.TriggerType != ScheduleTriggerType.Manual &&
                        (s.NextRunTime == null || s.NextRunTime <= DateTime.UtcNow))
            .ToListAsync();
    }
}
