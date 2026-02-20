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
    /// Starts execution of a schedule. Creates a ScheduleExecution record and queues ALL steps upfront.
    /// Step 0 tasks are set to Queued (ready to run). All subsequent step tasks are set to
    /// WaitingForPreviousStep (visible on the queue but blocked until the worker advances them).
    /// The worker drives step advancement via TryAdvanceScheduleExecutionAsync.
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

        // Get the distinct step indices so we know which is step 0 and which are subsequent
        var distinctStepIndices = schedule.Steps
            .Select(s => s.StepIndex)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        Log.Information("StartScheduleExecutionAsync: Starting execution of schedule {ScheduleId} ({ScheduleName}) with {StepCount} steps across {GroupCount} step groups. Queueing all steps upfront.",
            schedule.Id, schedule.Name, schedule.Steps.Count, distinctStepIndices.Count);

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

        // Queue ALL step groups upfront
        var firstStepIndex = distinctStepIndices[0];
        foreach (var stepIndex in distinctStepIndices)
        {
            // First step group is Queued (ready to run), all others are WaitingForPreviousStep
            var initialStatus = stepIndex == firstStepIndex
                ? WorkerTaskStatus.Queued
                : WorkerTaskStatus.WaitingForPreviousStep;

            await QueueStepGroupAsync(execution, schedule.Steps, stepIndex, initialStatus, initiatorType, initiatorId, initiatorName);
        }

        Log.Information("StartScheduleExecutionAsync: All {StepCount} steps queued for execution {ExecutionId}. Step group 0 is Queued, remaining groups are WaitingForPreviousStep.",
            schedule.Steps.Count, execution.Id);

        return execution;
    }

    /// <summary>
    /// Checks if all tasks for the current step group have completed and advances to the next step if so.
    /// Uses Activities (the immutable audit record) to determine step outcomes, because worker tasks
    /// are deleted upon completion and may not be present when the scheduler polls.
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

        var currentStepIndex = freshExecution.CurrentStepIndex;

        // First, check if any worker tasks are still active (Queued or Processing).
        // If so, the step is still in progress.
        var tasksForCurrentStep = await Application.Repository.Tasking.GetWorkerTasksByScheduleExecutionStepAsync(
            execution.Id, currentStepIndex);

        var hasActiveTasks = tasksForCurrentStep.Any(t =>
            t.Status == WorkerTaskStatus.Queued || t.Status == WorkerTaskStatus.Processing);

        if (hasActiveTasks)
        {
            Log.Debug("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} has active tasks, not yet complete.",
                execution.Id, currentStepIndex);
            return true; // Still in progress
        }

        // No active worker tasks. Query Activities to determine step outcomes.
        // Activities persist after worker task deletion and are the source of truth for step results.
        var activitiesForStep = await Application.Repository.Activity.GetActivitiesByScheduleExecutionStepAsync(
            execution.Id, currentStepIndex);

        if (activitiesForStep.Count == 0)
        {
            // No activities and no active tasks — the step may not have produced activities yet
            // (e.g. unsupported step type that was skipped). Check if tasks were ever created.
            if (tasksForCurrentStep.Count == 0)
            {
                // No tasks were ever created for this step (or they were already cleaned up with no activity).
                // Treat as complete and advance.
                Log.Information("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} has no tasks or activities. Advancing.",
                    execution.Id, currentStepIndex);
            }
            else
            {
                // Tasks exist but no activities yet — tasks may still be starting up.
                Log.Debug("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} has tasks but no activities yet. Waiting.",
                    execution.Id, currentStepIndex);
                return true;
            }
        }

        // Check if all activities for this step have reached a terminal status
        var allActivitiesComplete = activitiesForStep.All(a =>
            a.Status != ActivityStatus.InProgress && a.Status != ActivityStatus.NotSet);

        if (!allActivitiesComplete)
        {
            Log.Debug("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} has {ActivityCount} activities, not all complete.",
                execution.Id, currentStepIndex, activitiesForStep.Count);
            return true; // Still in progress
        }

        // All activities are in a terminal state. Check for failures.
        var anyFailed = activitiesForStep.Any(a =>
            a.Status == ActivityStatus.FailedWithError ||
            a.Status == ActivityStatus.CompleteWithError ||
            a.Status == ActivityStatus.Cancelled);

        Log.Information("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} step {StepIndex} completed. AnyFailed: {AnyFailed}",
            execution.Id, currentStepIndex, anyFailed);

        // Check if any failed and ContinueOnFailure is false
        if (anyFailed)
        {
            // Check ALL steps at this index (parallel steps share the same index),
            // not just the first one. Fail if ANY step has ContinueOnFailure = false.
            var stepsAtIndex = freshExecution.Schedule.Steps
                .Where(s => s.StepIndex == currentStepIndex).ToList();

            if (stepsAtIndex.Count == 0 || stepsAtIndex.Any(s => !s.ContinueOnFailure))
            {
                var failedStepNames = stepsAtIndex
                    .Where(s => !s.ContinueOnFailure)
                    .Select(s => string.IsNullOrEmpty(s.Name) ? $"Step {s.StepIndex}" : s.Name)
                    .ToList();

                var stepDescription = failedStepNames.Count > 0
                    ? string.Join(", ", failedStepNames)
                    : $"Step index {currentStepIndex}";

                Log.Warning("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} failed at step {StepIndex} ({StepNames}) due to activity failure.",
                    execution.Id, currentStepIndex, stepDescription);

                freshExecution.Status = ScheduleExecutionStatus.Failed;
                freshExecution.CompletedAt = DateTime.UtcNow;
                freshExecution.ErrorMessage = $"Step '{stepDescription}' failed and ContinueOnFailure is false.";
                await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(freshExecution);

                // Clean up remaining WaitingForPreviousStep tasks
                var deletedCount = await Application.Repository.Tasking.DeleteWaitingTasksForExecutionAsync(execution.Id);
                if (deletedCount > 0)
                {
                    Log.Information("CheckAndAdvanceExecutionAsync: Cleaned up {Count} waiting tasks for failed execution {ExecutionId}",
                        deletedCount, execution.Id);
                }

                return false;
            }
        }

        // Find the next waiting step group (all steps are queued upfront as WaitingForPreviousStep)
        var nextStepIndex = await Application.Repository.Tasking.GetNextWaitingStepIndexAsync(execution.Id);

        if (!nextStepIndex.HasValue)
        {
            // No more waiting steps - execution complete
            Log.Information("CheckAndAdvanceExecutionAsync: Execution {ExecutionId} completed successfully.", execution.Id);

            freshExecution.Status = ScheduleExecutionStatus.Completed;
            freshExecution.CompletedAt = DateTime.UtcNow;
            await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(freshExecution);
            return false;
        }

        // Advance to next step group by transitioning WaitingForPreviousStep -> Queued
        Log.Information("CheckAndAdvanceExecutionAsync: Safety net advancing execution {ExecutionId} to step {StepIndex}",
            execution.Id, nextStepIndex.Value);

        freshExecution.CurrentStepIndex = nextStepIndex.Value;
        await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(freshExecution);

        var transitioned = await Application.Repository.Tasking.TransitionStepToQueuedAsync(execution.Id, nextStepIndex.Value);
        Log.Information("CheckAndAdvanceExecutionAsync: Transitioned {Count} tasks to Queued for execution {ExecutionId} step {StepIndex}",
            transitioned, execution.Id, nextStepIndex.Value);

        return true;
    }

    /// <summary>
    /// Cancels a running or queued schedule execution.
    /// Sets the execution status to Cancelled, cancels all task activities,
    /// and deletes all tasks regardless of their current status.
    /// </summary>
    /// <returns>True if the execution was cancelled, false if it was not in a cancellable state.</returns>
    public async Task<bool> CancelScheduleExecutionAsync(Guid executionId)
    {
        var execution = await Application.Repository.Scheduling.GetScheduleExecutionAsync(executionId);
        if (execution == null)
        {
            Log.Warning("CancelScheduleExecutionAsync: Execution {ExecutionId} not found", executionId);
            return false;
        }

        if (execution.Status != ScheduleExecutionStatus.Queued &&
            execution.Status != ScheduleExecutionStatus.InProgress)
        {
            Log.Warning("CancelScheduleExecutionAsync: Cannot cancel execution {ExecutionId} with status {Status}",
                executionId, execution.Status);
            return false;
        }

        execution.Status = ScheduleExecutionStatus.Cancelled;
        execution.CompletedAt = DateTime.UtcNow;
        execution.ErrorMessage = "Cancelled by user";
        await Application.Repository.Scheduling.UpdateScheduleExecutionAsync(execution);

        // Cancel all tasks — same aggressive approach as individual task cancellation
        var tasks = await Application.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(executionId);
        foreach (var task in tasks)
        {
            if (task.Activity != null)
                await Application.Activities.CancelActivityAsync(task.Activity);

            await Application.Repository.Tasking.DeleteWorkerTaskAsync(task);
        }

        Log.Information("CancelScheduleExecutionAsync: Cancelled execution {ExecutionId}, deleted {Count} tasks",
            executionId, tasks.Count);
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
        WorkerTaskStatus initialStatus,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        // Get all steps at this index (could be multiple if ParallelWithPrevious)
        var stepsAtIndex = allSteps.Where(s => s.StepIndex == stepIndex).ToList();
        var isParallelGroup = stepsAtIndex.Count > 1;

        if (isParallelGroup)
        {
            Log.Information("QueueStepGroupAsync: Step index {StepIndex} is a parallel group with {Count} steps for execution {ExecutionId} (status: {InitialStatus})",
                stepIndex, stepsAtIndex.Count, execution.Id, initialStatus);
        }

        foreach (var step in stepsAtIndex)
        {
            try
            {
                await QueueStepAsync(execution, step, isParallelGroup, initialStatus, initiatorType, initiatorId, initiatorName);
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
        bool isParallelGroup,
        WorkerTaskStatus initialStatus,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        Log.Information("QueueStepAsync: Queueing step {StepId} ({StepName}) type {StepType} mode {ExecutionMode} status {InitialStatus} for execution {ExecutionId}",
            step.Id, step.Name, step.StepType, isParallelGroup ? "Parallel" : "Sequential", initialStatus, execution.Id);

        switch (step.StepType)
        {
            case ScheduleStepType.RunProfile:
                await QueueRunProfileStepAsync(execution, step, isParallelGroup, initialStatus, initiatorType, initiatorId, initiatorName);
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
        bool isParallelGroup,
        WorkerTaskStatus initialStatus,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        // Validate RunProfile configuration
        if (!step.ConnectedSystemId.HasValue || !step.RunProfileId.HasValue)
        {
            throw new InvalidOperationException($"Invalid RunProfile configuration for step {step.Id}. ConnectedSystemId and RunProfileId are required.");
        }

        // Create the worker task
        var workerTask = new SynchronisationWorkerTask
        {
            ConnectedSystemId = step.ConnectedSystemId.Value,
            ConnectedSystemRunProfileId = step.RunProfileId.Value,
            Status = initialStatus,
            InitiatedByType = initiatorType,
            InitiatedById = initiatorId,
            InitiatedByName = initiatorName,
            ScheduleExecutionId = execution.Id,
            ScheduleStepIndex = step.StepIndex,
            ContinueOnFailure = step.ContinueOnFailure,
            // Use parallel execution if this step runs with others at the same index
            ExecutionMode = isParallelGroup ? WorkerTaskExecutionMode.Parallel : WorkerTaskExecutionMode.Sequential
        };

        var result = await Application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to create worker task for step {step.Id}: {result.ErrorMessage}");
        }

        Log.Debug("QueueRunProfileStepAsync: Created worker task {TaskId} for step {StepId} with status {Status}",
            result.WorkerTaskId, step.Id, initialStatus);
    }
}
