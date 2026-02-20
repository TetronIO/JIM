using JIM.Models.Scheduling;
using JIM.Models.Utility;

namespace JIM.Data.Repositories;

public interface ISchedulingRepository
{
    // -----------------------------------------------------------------------------------------------------------------
    // Schedule CRUD
    // -----------------------------------------------------------------------------------------------------------------

    Task<Schedule?> GetScheduleAsync(Guid id);

    Task<Schedule?> GetScheduleWithStepsAsync(Guid id);

    Task<Schedule?> GetScheduleWithStepsAsNoTrackingAsync(Guid id);

    Task<List<Schedule>> GetAllSchedulesAsync();

    Task<PagedResultSet<Schedule>> GetSchedulesAsync(
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
}
