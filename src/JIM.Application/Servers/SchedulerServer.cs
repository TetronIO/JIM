// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using System.Text.Json;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
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

    public async Task<List<Schedule>> GetAllSchedulesAsync()
    {
        return await Application.Repository.Scheduling.GetAllSchedulesAsync();
    }

    /// <summary>
    /// Gets a page of Schedules as lightweight headers, each carrying its step count and the outcome of its most
    /// recent execution, so a list view can show whether the last run succeeded rather than only when it happened.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="searchQuery">Optional filter over name and description.</param>
    /// <param name="sortBy">Optional field to sort by (name, isEnabled, lastRunTime, nextRunTime).</param>
    /// <param name="sortDescending">Whether to sort in descending order.</param>
    public async Task<PagedResultSet<ScheduleHeader>> GetScheduleHeadersAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false)
    {
        return await Application.Repository.Scheduling.GetScheduleHeadersAsync(page, pageSize, searchQuery, sortBy, sortDescending);
    }

    /// <summary>
    /// Gets a window of Schedule headers addressed by absolute offset and count, for the virtualised
    /// (infinite-scroll) Schedules grid. Takes the same search and sort as
    /// <see cref="GetScheduleHeadersAsync"/> and shares its query core. Pass
    /// <paramref name="includeTotalCount"/> as false to skip counting the whole match set when the caller already
    /// knows the total; the returned total is then null rather than zero.
    /// </summary>
    /// <param name="offset">The zero-based index of the first Schedule wanted; negative values read as zero.</param>
    /// <param name="count">How many Schedules are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional case-insensitive filter over name and description.</param>
    /// <param name="sortBy">Optional field to sort by (name, isEnabled, lastRunTime, nextRunTime).</param>
    /// <param name="sortDescending">Whether to sort in descending order.</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<ScheduleHeader>> GetScheduleHeadersRangeAsync(
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        bool includeTotalCount = true)
    {
        return await Application.Repository.Scheduling.GetScheduleHeadersRangeAsync(
            offset, count, searchQuery, sortBy, sortDescending, includeTotalCount);
    }

    public async Task CreateScheduleAsync(Schedule schedule, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null, Guid? parentActivityId = null)
    {
        // Every configuration change is tracked with an immutable Activity, the same as Connected Systems and
        // Synchronisation Rules. Internal run-time bookkeeping (NextRunTime / LastRunTime) bypasses this method
        // and writes straight to the repository, so those ticks are correctly not audited here.
        var activity = new Activity
        {
            TargetName = schedule.Name,
            TargetType = ActivityTargetType.Schedule,
            TargetOperationType = ActivityTargetOperationType.Create,
            ParentActivityId = parentActivityId
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await Application.Repository.Scheduling.CreateScheduleAsync(schedule);
        await CaptureConfigurationChangeAsync(activity, schedule.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task UpdateScheduleAsync(Schedule schedule, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        var activity = new Activity
        {
            TargetName = schedule.Name,
            TargetType = ActivityTargetType.Schedule,
            TargetOperationType = ActivityTargetOperationType.Update
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await Application.Repository.Scheduling.UpdateScheduleAsync(schedule);
        await CaptureConfigurationChangeAsync(activity, schedule.Id, changeReason);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    public async Task DeleteScheduleAsync(Schedule schedule, ActivityInitiatorType initiatorType, Guid? initiatorId, string? initiatorName, string? changeReason = null)
    {
        // Built-in schedules (for example the seeded Temporal Scope Reconciliation schedule) are part of
        // the product and must not be deleted; they may be enabled, disabled and re-timed, but not removed.
        // This is the authoritative backstop for any caller; the API also rejects the request with a 400.
        if (schedule.BuiltIn)
            throw new InvalidOperationException($"The built-in schedule '{schedule.Name}' cannot be deleted.");

        var activity = new Activity
        {
            TargetName = schedule.Name,
            TargetType = ActivityTargetType.Schedule,
            TargetOperationType = ActivityTargetOperationType.Delete
        };
        await Application.Activities.CreateActivityWithTriadAsync(activity, initiatorType, initiatorId, initiatorName);
        await CaptureConfigurationDeletionAsync(activity, schedule, changeReason);
        await Application.Repository.Scheduling.DeleteScheduleAsync(schedule);
        await Application.Activities.CompleteActivityAsync(activity);
    }

    /// <summary>
    /// Captures a redacted, versioned configuration snapshot of a Schedule onto its audit Activity via the shared
    /// ConfigurationChangeCaptureService (which owns the toggle, dedupe-guard, versioning and best-effort
    /// behaviours). The schedule is reloaded with its steps so the snapshot reflects persisted truth rather than
    /// the caller's partial in-memory graph; call it after the change has been persisted and, at a call site that
    /// also reconciles steps, after the step changes too.
    /// </summary>
    private async Task CaptureConfigurationChangeAsync(Activity activity, Guid scheduleId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.Schedule, scheduleId,
            async hashKey =>
            {
                var schedule = await Application.Repository.Scheduling.GetScheduleWithStepsAsync(scheduleId);
                return schedule == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(schedule, hashKey);
            },
            $"Schedule {scheduleId}");
    }

    /// <summary>
    /// Captures a tombstone snapshot of a Schedule onto its delete Activity, before the schedule is removed.
    /// Matching the Synchronisation Rule deletion behaviour, this does not set <see cref="Activity.ScheduleId"/>
    /// or a version: the schedule is deleted before the Activity completes, so the Activity is left unlinked and
    /// the snapshot is surfaced via the Activity itself rather than the object's history.
    /// </summary>
    private async Task CaptureConfigurationDeletionAsync(Activity activity, Schedule schedule, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureDeletionAsync(activity, changeReason,
            async hashKey =>
            {
                // Reload with steps for a complete tombstone; fall back to the caller's entity if already gone.
                var persisted = await Application.Repository.Scheduling.GetScheduleWithStepsAsync(schedule.Id) ?? schedule;
                return Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Schedule {schedule.Id}");
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

    /// <summary>
    /// Persists a new Schedule step. Deliberately records no Activity or configuration snapshot itself: step
    /// mutations only occur as part of a whole-Schedule save, and every caller (the Schedule editor dialog and the
    /// REST update endpoint) reconciles steps first and then calls
    /// <see cref="UpdateScheduleAsync(Schedule,ActivityInitiatorType,Guid?,string?,string?)"/>, whose capture
    /// records the step changes in exactly one new version. Any new caller MUST follow the same pattern (reconcile,
    /// then audited whole-Schedule update), or the Schedule's change history will silently drift from reality.
    /// </summary>
    public async Task CreateScheduleStepAsync(ScheduleStep step)
    {
        await GuardStepScheduleNotBuiltInAsync(step, "added to");
        await Application.Repository.Scheduling.CreateScheduleStepAsync(step);
    }

    /// <summary>
    /// Persists a change to a Schedule step. See <see cref="CreateScheduleStepAsync"/> for the caller contract:
    /// step mutations must be followed by an audited whole-Schedule update, which captures them.
    /// </summary>
    public async Task UpdateScheduleStepAsync(ScheduleStep step)
    {
        await GuardStepScheduleNotBuiltInAsync(step, "changed on");
        await Application.Repository.Scheduling.UpdateScheduleStepAsync(step);
    }

    /// <summary>
    /// Deletes a Schedule step. See <see cref="CreateScheduleStepAsync"/> for the caller contract:
    /// step mutations must be followed by an audited whole-Schedule update, which captures them.
    /// </summary>
    public async Task DeleteScheduleStepAsync(ScheduleStep step)
    {
        await GuardStepScheduleNotBuiltInAsync(step, "removed from");
        await Application.Repository.Scheduling.DeleteScheduleStepAsync(step);
    }

    /// <summary>
    /// Authoritative backstop that prevents any caller from adding, changing or removing the steps of a built-in
    /// schedule (for example the seeded Temporal Scope Reconciliation schedule); its steps are defined and
    /// maintained by JIM. The portal and REST API also enforce this, returning a friendly error before reaching
    /// here. A no-op when the parent schedule is a normal user schedule or cannot be found.
    /// </summary>
    private async Task GuardStepScheduleNotBuiltInAsync(ScheduleStep step, string verb)
    {
        var schedule = await Application.Repository.Scheduling.GetScheduleAsync(step.ScheduleId);
        if (schedule?.BuiltIn == true)
            throw new InvalidOperationException($"Steps cannot be {verb} the built-in schedule '{schedule.Name}'.");
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
    /// Gets a paginated list of Schedule Executions, optionally filtered by schedule.
    /// </summary>
    /// <param name="scheduleId">Optional filter by schedule ID.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortBy">Optional field to sort by (queuedAt, startedAt, completedAt, status).</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true for newest first).</param>
    /// <returns>A paged result set of Schedule Executions.</returns>
    public async Task<PagedResultSet<ScheduleExecution>> GetScheduleExecutionsAsync(
        Guid? scheduleId,
        int page,
        int pageSize,
        string? sortBy = null,
        bool sortDescending = true)
    {
        return await Application.Repository.Scheduling.GetScheduleExecutionsAsync(scheduleId, page, pageSize, sortBy, sortDescending);
    }

    /// <summary>
    /// Gets a window of Schedule Executions addressed by absolute offset and count, for the virtualised
    /// (infinite-scroll) Schedule Execution grids. Takes the same filter and sort as
    /// <see cref="GetScheduleExecutionsAsync"/> and shares its query core. Pass
    /// <paramref name="includeTotalCount"/> as false to skip counting the whole match set when the caller
    /// already knows the total; the returned total is then null rather than zero.
    /// </summary>
    /// <param name="scheduleId">Optional filter by Schedule ID; null lists every Schedule's executions.</param>
    /// <param name="offset">The zero-based index of the first execution wanted; negative values read as zero.</param>
    /// <param name="count">How many executions are wanted; clamped to the repository's window-size cap.</param>
    /// <param name="searchQuery">Optional case-insensitive filter over the Schedule name and the initiator's name.</param>
    /// <param name="sortBy">Optional field to sort by (queuedAt, startedAt, completedAt, status).</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true for newest first).</param>
    /// <param name="includeTotalCount">Whether to count the whole match set alongside the window; counting is the
    /// expensive half of a window read, so callers that already hold the total pass false and receive a null total.</param>
    public async Task<RangeResultSet<ScheduleExecution>> GetScheduleExecutionsRangeAsync(
        Guid? scheduleId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool includeTotalCount = true)
    {
        return await Application.Repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset, count, searchQuery, sortBy, sortDescending, includeTotalCount);
    }

    /// <summary>
    /// Gets a Schedule Execution by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the execution.</param>
    public async Task<ScheduleExecution?> GetScheduleExecutionAsync(Guid id)
    {
        return await Application.Repository.Scheduling.GetScheduleExecutionAsync(id);
    }

    /// <summary>
    /// Gets a Schedule Execution by ID, with its parent Schedule included.
    /// </summary>
    /// <param name="id">The unique identifier of the execution.</param>
    public async Task<ScheduleExecution?> GetScheduleExecutionWithScheduleAsync(Guid id)
    {
        return await Application.Repository.Scheduling.GetScheduleExecutionWithScheduleAsync(id);
    }

    /// <summary>
    /// Gets a Schedule Execution with its Schedule and the derived state of every step: how far each step got, when,
    /// what it reported, and which Activity produced it.
    /// </summary>
    /// <remarks>
    /// Step outcomes are read from Activities rather than Worker Tasks. Worker Tasks are deleted the moment they
    /// finish, so they only describe steps that are still live; Activities persist and are the durable record. A
    /// still-live Worker Task therefore takes precedence (its Activity is necessarily still in progress), the
    /// Activity is used once the task is gone, and where neither exists the status is inferred from how far the
    /// execution itself got.
    /// Shared by GET /api/v1/schedule-executions/{id} and the portal's Schedule Execution detail page; the two must
    /// not diverge.
    /// </remarks>
    /// <param name="id">The unique identifier of the execution.</param>
    /// <returns>The execution and its per-step state, or null if no such execution exists.</returns>
    public async Task<ScheduleExecutionDetail?> GetScheduleExecutionDetailAsync(Guid id)
    {
        var execution = await Application.Repository.Scheduling.GetScheduleExecutionWithScheduleAsync(id);
        if (execution == null)
            return null;

        var detail = new ScheduleExecutionDetail { Execution = execution };

        // A deleted Schedule leaves its executions behind, so there are no step definitions to describe.
        if (execution.Schedule == null)
            return detail;

        // Activities survive Worker Task deletion, so they carry the outcome of every step that has run.
        var activities = await Application.Activities.GetActivitiesByScheduleExecutionAsync(id);
        var activitiesByStep = activities.GroupBy(a => a.ScheduleStepIndex ?? -1)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Worker Tasks only exist while a step is queued or running; they describe the live steps.
        var workerTasks = await Application.Tasking.GetWorkerTasksByScheduleExecutionAsync(id);
        var tasksByStep = workerTasks.GroupBy(t => t.ScheduleStepIndex ?? -1)
            .ToDictionary(g => g.Key, g => g.ToList());

        var steps = await Application.Repository.Scheduling.GetScheduleStepsAsync(execution.ScheduleId);
        var stepsByIndex = steps.GroupBy(s => s.StepIndex)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.ConnectedSystemId).ToList());

        var stepNames = await GetRunProfileStepNamesAsync(steps);

        foreach (var stepIndex in stepsByIndex.Keys.OrderBy(i => i))
        {
            var stepsAtIndex = stepsByIndex[stepIndex];
            var stepActivities = activitiesByStep.GetValueOrDefault(stepIndex);
            var stepTasks = tasksByStep.GetValueOrDefault(stepIndex);

            foreach (var step in stepsAtIndex)
            {
                // Parallel steps share a step index, so the Connected System is what tells their Activities apart.
                // Where an index holds a single step, fall back to whatever is there: steps that are not Run
                // Profile steps carry no Connected System to match on.
                var activity = stepActivities?.FirstOrDefault(a => a.ConnectedSystemId == step.ConnectedSystemId)
                               ?? (stepsAtIndex.Count == 1 ? stepActivities?.FirstOrDefault() : null);
                var task = stepTasks?.FirstOrDefault(t => t is SynchronisationWorkerTask swt && swt.ConnectedSystemId == step.ConnectedSystemId)
                           ?? (stepsAtIndex.Count == 1 ? stepTasks?.FirstOrDefault() : null);

                string? connectedSystemName = null;
                string? runProfileName = null;
                if (step.ConnectedSystemId.HasValue && stepNames.TryGetValue(step.ConnectedSystemId.Value, out var names))
                {
                    connectedSystemName = names.ConnectedSystemName;
                    if (step.RunProfileId.HasValue)
                        runProfileName = names.RunProfileNames.GetValueOrDefault(step.RunProfileId.Value);
                }

                detail.Steps.Add(new ScheduleExecutionStepState
                {
                    ScheduleStepId = step.Id,
                    StepIndex = stepIndex,
                    Name = step.Name ?? $"Step {stepIndex + 1}",
                    StepType = step.StepType,
                    ExecutionMode = step.ExecutionMode,
                    ConnectedSystemId = step.ConnectedSystemId,
                    ConnectedSystemName = connectedSystemName,
                    RunProfileId = step.RunProfileId,
                    RunProfileName = runProfileName,
                    Status = DeriveStepStatus(task, activity, stepIndex, execution.CurrentStepIndex, execution.Status),
                    TaskId = task?.Id,
                    StartedAt = activity?.Executed,
                    CompletedAt = activity != null && IsTerminal(activity.Status)
                        ? activity.Executed + (activity.TotalActivityTime ?? TimeSpan.Zero)
                        : null,
                    ErrorMessage = activity?.ErrorMessage,
                    ActivityId = activity?.Id,
                    ActivityStatus = activity?.Status,
                    ContinueOnFailure = step.ContinueOnFailure
                });
            }
        }

        return detail;
    }

    /// <summary>
    /// Resolves the Connected System and Run Profile names for a Schedule's Run Profile steps.
    /// </summary>
    /// <remarks>
    /// Run Profile steps store no name of their own, so without this every one of them reads "Step 1", "Step 2" and
    /// the step list says nothing about what actually ran. One Run Profile lookup per distinct Connected System,
    /// not per step, so a Schedule with several steps against one system costs one query.
    /// </remarks>
    private async Task<Dictionary<int, (string? ConnectedSystemName, Dictionary<int, string> RunProfileNames)>> GetRunProfileStepNamesAsync(
        List<ScheduleStep> steps)
    {
        var connectedSystemIds = steps
            .Where(s => s.ConnectedSystemId.HasValue)
            .Select(s => s.ConnectedSystemId!.Value)
            .Distinct()
            .ToList();

        var names = new Dictionary<int, (string? ConnectedSystemName, Dictionary<int, string> RunProfileNames)>();
        if (connectedSystemIds.Count == 0)
            return names;

        var headers = await Application.ConnectedSystems.GetConnectedSystemHeadersAsync();
        var headersById = headers.ToDictionary(h => h.Id, h => h.Name);

        // Only the Connected Systems that actually have a Run Profile step need their Run Profiles listing; a
        // PowerShell or executable step names a Connected System without referencing a Run Profile at all.
        var systemsWithRunProfileSteps = steps
            .Where(s => s.ConnectedSystemId.HasValue && s.RunProfileId.HasValue)
            .Select(s => s.ConnectedSystemId!.Value)
            .Distinct()
            .ToHashSet();

        foreach (var connectedSystemId in connectedSystemIds)
        {
            var runProfileNames = new Dictionary<int, string>();
            if (systemsWithRunProfileSteps.Contains(connectedSystemId))
            {
                var runProfiles = await Application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
                runProfileNames = runProfiles.ToDictionary(rp => rp.Id, rp => rp.Name);
            }

            names[connectedSystemId] = (headersById.GetValueOrDefault(connectedSystemId), runProfileNames);
        }

        return names;
    }

    /// <summary>
    /// Whether an Activity has finished, in any outcome. Only then does it have an end time.
    /// </summary>
    private static bool IsTerminal(ActivityStatus status)
    {
        return status is ActivityStatus.Complete or ActivityStatus.CompleteWithWarning
            or ActivityStatus.CompleteWithError or ActivityStatus.FailedWithError or ActivityStatus.Cancelled;
    }

    /// <summary>
    /// Determines a step's display status from its Worker Task, its Activity, or failing both, the execution's own
    /// position. Prefers the Activity over the Worker Task, because the Worker Task is deleted on completion.
    /// </summary>
    private static ScheduleExecutionStepStatus DeriveStepStatus(
        WorkerTask? task,
        Activity? activity,
        int stepIndex,
        int currentStepIndex,
        ScheduleExecutionStatus executionStatus)
    {
        // One definition, shared with the Operations queue's group header, which aggregates the same
        // status per step group (#1162). Deriving it twice is how the two surfaces would come to
        // disagree about a step that is finishing at the moment they are each asked.
        return ScheduleStepReading.StatusOf(task?.Status, activity?.Status, stepIndex, currentStepIndex, executionStatus);
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
            // Step groups, not step rows: CurrentStepIndex advances one group at a time, and the two are
            // read together as "step X of Y". Steps sharing a StepIndex are one position, not several.
            TotalSteps = distinctStepIndices.Count,
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

            freshExecution.Status = ScheduleExecutionStatus.Complete;
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

        // Cancel all tasks — processing tasks are signalled for graceful cancellation,
        // queued/waiting tasks are cancelled and removed immediately.
        var tasks = await Application.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(executionId);
        var immediatelyCancelled = 0;
        var signalledForCancellation = 0;
        foreach (var task in tasks)
        {
            if (task.Status == WorkerTaskStatus.Processing)
            {
                // Task is actively being processed by the worker — signal it for cancellation.
                task.Status = WorkerTaskStatus.CancellationRequested;
                await Application.Repository.Tasking.UpdateWorkerTaskAsync(task);
                signalledForCancellation++;
            }
            else
            {
                if (task.Activity != null)
                    await Application.Activities.CancelActivityAsync(task.Activity);

                await Application.Repository.Tasking.DeleteWorkerTaskAsync(task);
                immediatelyCancelled++;
            }
        }

        Log.Information("CancelScheduleExecutionAsync: Cancelled execution {ExecutionId} — {ImmediateCount} tasks cancelled immediately, {SignalledCount} processing tasks signalled for cancellation",
            executionId, immediatelyCancelled, signalledForCancellation);
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

    /// <summary>
    /// Persists run-time bookkeeping (NextRunTime/LastRunTime) only. Deliberately records no Activity or
    /// configuration change: these ticks are operational state produced by the scheduler loop, not a configuration
    /// change made by a principal, mirroring the rationale documented on <see cref="CreateScheduleAsync"/>. Callers
    /// must not use this for configuration changes; use
    /// <see cref="UpdateScheduleAsync(Schedule,ActivityInitiatorType,Guid?,string?,string?)"/> for that.
    /// </summary>
    public async Task UpdateScheduleRunTimesAsync(Schedule schedule)
    {
        await Application.Repository.Scheduling.UpdateScheduleAsync(schedule);
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

            case ScheduleStepType.TemporalScopeReconciliation:
                await QueueTemporalScopeReconciliationStepAsync(execution, step, isParallelGroup, initialStatus, initiatorType, initiatorId, initiatorName);
                break;

            case ScheduleStepType.HistoryRetentionCleanup:
                await QueueHistoryRetentionCleanupStepAsync(execution, step, isParallelGroup, initialStatus, initiatorType, initiatorId, initiatorName);
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

    /// <summary>
    /// Queues a Temporal Scope Reconciliation step (issue #892) by creating a TemporalScopeReconciliationWorkerTask.
    /// The task carries no per-step configuration; the worker derives its watermark from the schedule's execution
    /// history at run time.
    /// </summary>
    private async Task QueueTemporalScopeReconciliationStepAsync(
        ScheduleExecution execution,
        ScheduleStep step,
        bool isParallelGroup,
        WorkerTaskStatus initialStatus,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        var workerTask = new TemporalScopeReconciliationWorkerTask
        {
            Status = initialStatus,
            InitiatedByType = initiatorType,
            InitiatedById = initiatorId,
            InitiatedByName = initiatorName,
            ScheduleExecutionId = execution.Id,
            ScheduleStepIndex = step.StepIndex,
            ContinueOnFailure = step.ContinueOnFailure,
            ExecutionMode = isParallelGroup ? WorkerTaskExecutionMode.Parallel : WorkerTaskExecutionMode.Sequential
        };

        var result = await Application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to create worker task for step {step.Id}: {result.ErrorMessage}");
        }

        Log.Debug("QueueTemporalScopeReconciliationStepAsync: Created worker task {TaskId} for step {StepId} with status {Status}",
            result.WorkerTaskId, step.Id, initialStatus);
    }

    /// <summary>
    /// Queues a History Retention Cleanup step (issue #1118) by creating a HistoryRetentionCleanupWorkerTask.
    /// The task carries no per-step configuration; the worker reads every retention period from its Service
    /// Setting at run time, so changing one takes effect on the next pass without the Schedule being touched.
    /// </summary>
    private async Task QueueHistoryRetentionCleanupStepAsync(
        ScheduleExecution execution,
        ScheduleStep step,
        bool isParallelGroup,
        WorkerTaskStatus initialStatus,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        var workerTask = new HistoryRetentionCleanupWorkerTask
        {
            Status = initialStatus,
            InitiatedByType = initiatorType,
            InitiatedById = initiatorId,
            InitiatedByName = initiatorName,
            ScheduleExecutionId = execution.Id,
            ScheduleStepIndex = step.StepIndex,
            ContinueOnFailure = step.ContinueOnFailure,
            ExecutionMode = isParallelGroup ? WorkerTaskExecutionMode.Parallel : WorkerTaskExecutionMode.Sequential
        };

        var result = await Application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to create worker task for step {step.Id}: {result.ErrorMessage}");
        }

        Log.Debug("QueueHistoryRetentionCleanupStepAsync: Created worker task {TaskId} for step {StepId} with status {Status}",
            result.WorkerTaskId, step.Id, initialStatus);
    }

    /// <summary>
    /// Derives the failure-safe watermark for a Temporal Scope Reconciliation sweep (issue #892): the start time
    /// of the previous successfully completed execution of the same schedule. Because a failed sweep never reaches
    /// Completed status, its window is re-covered by the next sweep rather than silently skipped. Returns null when
    /// there is no prior completed execution (the first, bootstrap sweep, which considers every transitioned object
    /// once).
    /// </summary>
    /// <param name="currentExecutionId">The in-progress execution running the sweep.</param>
    public async Task<DateTime?> GetTemporalScopeReconciliationWatermarkAsync(Guid currentExecutionId)
    {
        var current = await Application.Repository.Scheduling.GetScheduleExecutionAsync(currentExecutionId);
        if (current == null)
        {
            Log.Warning("GetTemporalScopeReconciliationWatermarkAsync: Execution {ExecutionId} not found; using bootstrap (null) watermark.", currentExecutionId);
            return null;
        }

        // StartedAt is populated the moment an execution begins; fall back to QueuedAt defensively.
        var currentStartedAt = current.StartedAt ?? current.QueuedAt;
        var previous = await Application.Repository.Scheduling.GetLastCompletedScheduleExecutionAsync(current.ScheduleId, currentStartedAt);
        return previous?.StartedAt;
    }
}
