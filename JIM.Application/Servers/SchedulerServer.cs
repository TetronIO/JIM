using System.Text.Json;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.Models.Utility;
using NCrontab;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Server responsible for schedule management and execution orchestration.
/// Used by the JIM.Scheduler BackgroundService to:
/// - Check for schedules due to run
/// - Start schedule executions
/// - Monitor step completion and queue next steps
/// - Calculate next run times
/// </summary>
public class SchedulerServer
{
    private JimApplication Application { get; }

    internal SchedulerServer(JimApplication application)
    {
        Application = application;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule CRUD (pass-through to repository)
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<Schedule?> GetScheduleAsync(Guid id)
    {
        return await Application.Repository.Scheduling.GetScheduleAsync(id);
    }

    public async Task<Schedule?> GetScheduleWithStepsAsync(Guid id)
    {
        return await Application.Repository.Scheduling.GetScheduleWithStepsAsync(id);
    }

    public async Task<Schedule?> GetScheduleWithStepsAsNoTrackingAsync(Guid id)
    {
        return await Application.Repository.Scheduling.GetScheduleWithStepsAsNoTrackingAsync(id);
    }

    public async Task<List<Schedule>> GetAllSchedulesAsync()
    {
        return await Application.Repository.Scheduling.GetAllSchedulesAsync();
    }

    public async Task<PagedResultSet<Schedule>> GetSchedulesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false)
    {
        return await Application.Repository.Scheduling.GetSchedulesAsync(page, pageSize, searchQuery, sortBy, sortDescending);
    }

    public async Task CreateScheduleAsync(Schedule schedule)
    {
        await Application.Repository.Scheduling.CreateScheduleAsync(schedule);
    }

    public async Task UpdateScheduleAsync(Schedule schedule)
    {
        await Application.Repository.Scheduling.UpdateScheduleAsync(schedule);
    }

    public async Task DeleteScheduleAsync(Schedule schedule)
    {
        await Application.Repository.Scheduling.DeleteScheduleAsync(schedule);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Steps
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<List<ScheduleStep>> GetScheduleStepsAsync(Guid scheduleId)
    {
        return await Application.Repository.Scheduling.GetScheduleStepsAsync(scheduleId);
    }

    public async Task<ScheduleStep?> GetScheduleStepAsync(Guid stepId)
    {
        return await Application.Repository.Scheduling.GetScheduleStepAsync(stepId);
    }

    public async Task CreateScheduleStepAsync(ScheduleStep step)
    {
        await Application.Repository.Scheduling.CreateScheduleStepAsync(step);
    }

    public async Task UpdateScheduleStepAsync(ScheduleStep step)
    {
        await Application.Repository.Scheduling.UpdateScheduleStepAsync(step);
    }

    public async Task DeleteScheduleStepAsync(ScheduleStep step)
    {
        await Application.Repository.Scheduling.DeleteScheduleStepAsync(step);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Execution
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Gets all schedules that are due to run now.
    /// </summary>
    public async Task<List<Schedule>> GetDueSchedulesAsync()
    {
        return await Application.Repository.Scheduling.GetDueSchedulesAsync(DateTime.UtcNow);
    }

    /// <summary>
    /// Starts execution of a schedule. Creates a ScheduleExecution record and queues the first step(s).
    /// </summary>
    /// <param name="schedule">The schedule to execute (must include Steps).</param>
    /// <param name="initiatorType">The type of principal initiating the execution.</param>
    /// <param name="initiatorId">The ID of the principal initiating the execution.</param>
    /// <param name="initiatorName">The name of the principal at time of execution.</param>
    /// <returns>The created ScheduleExecution, or null if the schedule has no steps.</returns>
    public async Task<ScheduleExecution?> StartScheduleExecutionAsync(
        Schedule schedule,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        if (schedule.Steps.Count == 0)
        {
            Log.Warning("StartScheduleExecutionAsync: Schedule {ScheduleId} ({ScheduleName}) has no steps. Skipping.",
                schedule.Id, schedule.Name);
            return null;
        }

        Log.Information("StartScheduleExecutionAsync: Starting execution of schedule {ScheduleId} ({ScheduleName}) with {StepCount} steps.",
            schedule.Id, schedule.Name, schedule.Steps.Count);

        // Create the execution record
        var execution = new ScheduleExecution
        {
            ScheduleId = schedule.Id,
            ScheduleName = schedule.Name,
            Status = ScheduleExecutionStatus.InProgress,
            CurrentStepIndex = 0,
            TotalSteps = schedule.Steps.Count,
            StartedAt = DateTime.UtcNow,
            InitiatedByType = initiatorType,
            InitiatedById = initiatorId,
            InitiatedByName = initiatorName
        };
        await Application.Repository.Scheduling.CreateScheduleExecutionAsync(execution);

        // Update schedule's last run time
        schedule.LastRunTime = DateTime.UtcNow;
        await Application.Repository.Scheduling.UpdateScheduleAsync(schedule);

        // Queue the first step group (all steps at index 0)
        await QueueStepGroupAsync(execution, schedule.Steps, 0, initiatorType, initiatorId, initiatorName);

        return execution;
    }

    /// <summary>
    /// Checks if all tasks for the current step group have completed and advances to the next step if so.
    /// </summary>
    /// <returns>True if the execution is still in progress, false if it has completed or failed.</returns>
    public async Task<bool> CheckAndAdvanceExecutionAsync(ScheduleExecution execution)
    {
        // Get fresh execution with schedule and steps
        var freshExecution = await Application.Repository.Scheduling.GetScheduleExecutionWithScheduleAsync(execution.Id);
        if (freshExecution == null)
        {
            Log.Warning("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} not found.", execution.Id);
            return false;
        }

        if (freshExecution.Status != ScheduleExecutionStatus.InProgress)
        {
            Log.Debug("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} is not in progress (status: {Status}).",
                execution.Id, freshExecution.Status);
            return false;
        }

        // Get all tasks for the current step group
        var currentStepIndex = freshExecution.CurrentStepIndex;
        var tasksForCurrentStep = await Application.Repository.Tasking.GetWorkerTasksByScheduleExecutionStepAsync(
            execution.Id, currentStepIndex);

        // Check if all tasks for the current step are complete
        var allComplete = true;
        var anyFailed = false;

        foreach (var task in tasksForCurrentStep)
        {
            if (task.Status == WorkerTaskStatus.Queued || task.Status == WorkerTaskStatus.Processing)
            {
                allComplete = false;
                break;
            }

            // Check if the task's activity failed
            if (task.Activity?.Status == ActivityStatus.FailedWithError ||
                task.Activity?.Status == ActivityStatus.CompleteWithError ||
                task.Activity?.Status == ActivityStatus.Cancelled)
            {
                anyFailed = true;
            }
        }

        if (!allComplete)
        {
            Log.Debug("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} has {TaskCount} tasks, not all complete.",
                execution.Id, currentStepIndex, tasksForCurrentStep.Count);
            return true; // Still in progress
        }

        Log.Information("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} completed. AnyFailed: {AnyFailed}",
            execution.Id, currentStepIndex, anyFailed);

        // Check if any failed and ContinueOnFailure is false
        if (anyFailed)
        {
            var currentStep = freshExecution.Schedule.Steps.FirstOrDefault(s => s.StepIndex == currentStepIndex);
            if (currentStep != null && !currentStep.ContinueOnFailure)
            {
                Log.Warning("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} failed at step {StepIndex} due to task failure.",
                    execution.Id, currentStepIndex);

                freshExecution.Status = ScheduleExecutionStatus.Failed;
                freshExecution.CompletedAt = DateTime.UtcNow;
                freshExecution.ErrorMessage = $"Step '{currentStep.Name}' failed and ContinueOnFailure is false.";
                await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(freshExecution);
                return false;
            }
        }

        // Find the next step index
        var nextStepIndex = currentStepIndex + 1;

        // Skip over any parallel steps (they would have the same index as the current one due to grouping)
        // Actually, find the next distinct step index
        var orderedSteps = freshExecution.Schedule.Steps.OrderBy(s => s.StepIndex).ToList();
        var nextStep = orderedSteps.FirstOrDefault(s => s.StepIndex > currentStepIndex);

        if (nextStep == null)
        {
            // No more steps - execution complete
            Log.Information("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} completed successfully.", execution.Id);

            freshExecution.Status = ScheduleExecutionStatus.Completed;
            freshExecution.CompletedAt = DateTime.UtcNow;
            await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(freshExecution);
            return false;
        }

        // Advance to next step group
        freshExecution.CurrentStepIndex = nextStep.StepIndex;
        await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(freshExecution);

        // Queue the next step group
        await QueueStepGroupAsync(
            freshExecution,
            freshExecution.Schedule.Steps,
            nextStep.StepIndex,
            freshExecution.InitiatedByType,
            freshExecution.InitiatedById,
            freshExecution.InitiatedByName);

        return true;
    }

    /// <summary>
    /// Gets all active (in-progress, queued, or paused) schedule executions.
    /// Used by the scheduler to monitor ongoing executions.
    /// </summary>
    public async Task<List<ScheduleExecution>> GetActiveExecutionsAsync()
    {
        return await Application.Repository.Scheduling.GetActiveScheduleExecutionsAsync();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Next Run Time Calculation
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Calculates and updates the NextRunTime for all enabled cron-based schedules.
    /// Should be called periodically by the scheduler service.
    /// </summary>
    public async Task UpdateNextRunTimesAsync()
    {
        var schedules = await Application.Repository.Scheduling.GetSchedulesForNextRunCalculationAsync();

        foreach (var schedule in schedules)
        {
            var nextRun = CalculateNextRunTime(schedule);
            if (nextRun.HasValue)
            {
                schedule.NextRunTime = nextRun.Value;
                await Application.Repository.Scheduling.UpdateScheduleAsync(schedule);
                Log.Debug("UpdateNextRunTimesAsync: Schedule {ScheduleId} ({ScheduleName}) next run at {NextRunTime}",
                    schedule.Id, schedule.Name, nextRun.Value);
            }
        }
    }

    /// <summary>
    /// Calculates the next run time for a schedule based on its cron expression.
    /// </summary>
    public DateTime? CalculateNextRunTime(Schedule schedule)
    {
        if (schedule.TriggerType != ScheduleTriggerType.Cron || string.IsNullOrWhiteSpace(schedule.CronExpression))
        {
            return null;
        }

        try
        {
            var cronSchedule = CrontabSchedule.Parse(schedule.CronExpression);
            var nextOccurrence = cronSchedule.GetNextOccurrence(DateTime.UtcNow);
            return nextOccurrence;
        }
        catch (CrontabException ex)
        {
            Log.Error(ex, "CalculateNextRunTime: Invalid cron expression '{CronExpression}' for schedule {ScheduleId}",
                schedule.CronExpression, schedule.Id);
            return null;
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Private Methods
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Queues all steps at a given step index (a "step group" that runs in parallel).
    /// </summary>
    private async Task QueueStepGroupAsync(
        ScheduleExecution execution,
        List<ScheduleStep> allSteps,
        int stepIndex,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        // Get all steps at this index (could be multiple if ParallelWithPrevious)
        var stepsAtIndex = allSteps.Where(s => s.StepIndex == stepIndex).ToList();

        foreach (var step in stepsAtIndex)
        {
            try
            {
                await QueueStepAsync(execution, step, initiatorType, initiatorId, initiatorName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "QueueStepGroupAsync: Failed to queue step {StepId} ({StepName}) for execution {ExecutionId}",
                    step.Id, step.Name, execution.Id);

                // If we can't queue a step, fail the execution
                execution.Status = ScheduleExecutionStatus.Failed;
                execution.CompletedAt = DateTime.UtcNow;
                execution.ErrorMessage = $"Failed to queue step '{step.Name}': {ex.Message}";
                await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(execution);
                throw;
            }
        }
    }

    /// <summary>
    /// Queues a single schedule step by creating the appropriate WorkerTask.
    /// </summary>
    private async Task QueueStepAsync(
        ScheduleExecution execution,
        ScheduleStep step,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        Log.Information("QueueStepAsync: Queueing step {StepId} ({StepName}) type {StepType} for execution {ExecutionId}",
            step.Id, step.Name, step.StepType, execution.Id);

        switch (step.StepType)
        {
            case ScheduleStepType.RunProfile:
                await QueueRunProfileStepAsync(execution, step, initiatorType, initiatorId, initiatorName);
                break;

            case ScheduleStepType.PowerShell:
            case ScheduleStepType.Executable:
            case ScheduleStepType.SqlScript:
                // These step types will be implemented post-MVP
                Log.Warning("QueueStepAsync: Step type {StepType} is not yet implemented. Skipping step {StepId}.",
                    step.StepType, step.Id);
                break;

            default:
                Log.Warning("QueueStepAsync: Unknown step type {StepType} for step {StepId}.", step.StepType, step.Id);
                break;
        }
    }

    /// <summary>
    /// Queues a RunProfile step by creating a SynchronisationWorkerTask.
    /// </summary>
    private async Task QueueRunProfileStepAsync(
        ScheduleExecution execution,
        ScheduleStep step,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        // Parse configuration to get ConnectedSystemId and RunProfileId
        var config = JsonSerializer.Deserialize<RunProfileStepConfiguration>(step.Configuration);
        if (config == null || config.ConnectedSystemId == default || config.RunProfileId == 0)
        {
            throw new InvalidOperationException($"Invalid RunProfile configuration for step {step.Id}. Configuration: {step.Configuration}");
        }

        // Create the worker task
        var workerTask = new SynchronisationWorkerTask
        {
            ConnectedSystemId = config.ConnectedSystemId,
            ConnectedSystemRunProfileId = config.RunProfileId,
            InitiatedByType = initiatorType,
            InitiatedById = initiatorId,
            InitiatedByName = initiatorName,
            ScheduleExecutionId = execution.Id,
            ScheduleStepIndex = step.StepIndex,
            // Use parallel execution if this step runs with others at the same index
            ExecutionMode = WorkerTaskExecutionMode.Sequential
        };

        var result = await Application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to create worker task for step {step.Id}: {result.ErrorMessage}");
        }

        Log.Debug("QueueRunProfileStepAsync: Created worker task {TaskId} for step {StepId}", result.WorkerTaskId, step.Id);
    }

    /// <summary>
    /// Configuration model for RunProfile step type.
    /// </summary>
    private class RunProfileStepConfiguration
    {
        public int ConnectedSystemId { get; set; }
        public int RunProfileId { get; set; }
    }
}
