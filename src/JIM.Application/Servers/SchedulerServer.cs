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
    /// Starts every schedule that is due to run, then advances each one's next run time. Called by the JIM.Scheduler
    /// service once per polling cycle; cron-triggered executions are initiated by System.
    /// </summary>
    /// <remarks>
    /// The next run time is advanced whether or not the start succeeds. A schedule that cannot start (a step's
    /// Connected System being deleted, for example) therefore fails once per scheduled occurrence and is attempted
    /// again at its next one, rather than staying due and failing again on every polling cycle, seconds apart, until
    /// someone fixes the cause (#1765). Each schedule is isolated: nothing that goes wrong with one stops the others
    /// being started.
    /// </remarks>
    public async Task StartDueSchedulesAsync()
    {
        var dueSchedules = await GetDueSchedulesAsync();
        if (dueSchedules.Count == 0)
            return;

        int started = 0, skipped = 0, failedToStart = 0, notAdvanced = 0;

        foreach (var schedule in dueSchedules)
        {
            try
            {
                // Prevent overlap. A schedule skipped here keeps its due time, so it starts on the first cycle after
                // the running execution ends.
                var activeExecutions = await GetActiveExecutionsAsync();
                if (activeExecutions.Any(e => e.ScheduleId == schedule.Id))
                {
                    Log.Warning("StartDueSchedulesAsync: Schedule {ScheduleId} ({ScheduleName}) is due but already has an active execution. Skipping.",
                        schedule.Id, schedule.Name);
                    skipped++;
                    continue;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartDueSchedulesAsync: Could not check schedule {ScheduleId} ({ScheduleName}) for an active execution. It stays due and will be checked again next cycle.",
                    schedule.Id, schedule.Name);
                notAdvanced++;
                continue;
            }

            Log.Information("StartDueSchedulesAsync: Starting execution of due schedule {ScheduleId} ({ScheduleName})",
                schedule.Id, schedule.Name);

            try
            {
                await StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Scheduler Service");
                started++;
            }
            catch (Exception ex)
            {
                // StartScheduleExecutionAsync has already recorded the failure on the Schedule Execution, if it got
                // as far as creating one. Fall through to advance the next run time regardless.
                Log.Error(ex, "StartDueSchedulesAsync: Failed to start execution for schedule {ScheduleId} ({ScheduleName}). It will be attempted again at its next scheduled run time.",
                    schedule.Id, schedule.Name);
                failedToStart++;
            }

            if (!await TryAdvanceNextRunTimeAsync(schedule))
                notAdvanced++;
        }

        Log.Information("StartDueSchedulesAsync: {DueCount} due schedule(s): {Started} started, {Skipped} skipped (already running), {FailedToStart} failed to start, {NotAdvanced} next run time(s) not advanced.",
            dueSchedules.Count, started, skipped, failedToStart, notAdvanced);
    }

    /// <summary>
    /// Advances a schedule's next run time to its next cron occurrence and saves it. Returns false, having logged
    /// why, when it cannot; the schedule then stays due and is picked up again next cycle.
    /// </summary>
    private async Task<bool> TryAdvanceNextRunTimeAsync(Schedule schedule)
    {
        try
        {
            var nextRunTime = CalculateNextRunTime(schedule);
            if (!nextRunTime.HasValue)
                return false;

            schedule.NextRunTime = nextRunTime.Value;
            await UpdateScheduleRunTimesAsync(schedule);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TryAdvanceNextRunTimeAsync: Failed to save the next run time for schedule {ScheduleId} ({ScheduleName}). It stays due and will be picked up again next cycle.",
                schedule.Id, schedule.Name);
            return false;
        }
    }

    /// <summary>
    /// Gets a paginated list of Schedule Executions, optionally filtered by schedule and by status. The total
    /// count is taken over the filtered set.
    /// </summary>
    /// <param name="scheduleId">Optional filter by schedule ID.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortBy">Optional field to sort by (queuedAt, startedAt, completedAt, status).</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true for newest first).</param>
    /// <param name="status">Optional filter by status; null returns executions of every status.</param>
    /// <returns>A paged result set of Schedule Executions.</returns>
    public async Task<PagedResultSet<ScheduleExecution>> GetScheduleExecutionsAsync(
        Guid? scheduleId,
        int page,
        int pageSize,
        string? sortBy = null,
        bool sortDescending = true,
        ScheduleExecutionStatus? status = null)
    {
        return await Application.Repository.Scheduling.GetScheduleExecutionsAsync(
            scheduleId, page, pageSize, sortBy, sortDescending, status);
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
    /// <param name="status">Optional filter by status; null returns executions of every status.</param>
    public async Task<RangeResultSet<ScheduleExecution>> GetScheduleExecutionsRangeAsync(
        Guid? scheduleId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool includeTotalCount = true,
        ScheduleExecutionStatus? status = null)
    {
        return await Application.Repository.Scheduling.GetScheduleExecutionsRangeAsync(
            scheduleId, offset, count, searchQuery, sortBy, sortDescending, includeTotalCount, status);
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
                // Parallel steps share a step index. Each Activity and Worker Task records the step it belongs to
                // (#1768), which tells them apart even when two parallel steps run against the same Connected System.
                // Records made before that was recorded carry no step, so those fall back to the Connected System, and
                // where an index holds a single step, to whatever is there: steps that are not Run Profile steps carry
                // no Connected System to match on.
                var activity = stepActivities?.FirstOrDefault(a => a.ScheduleStepId == step.Id)
                               ?? MatchUnidentified(stepActivities, a => a.ScheduleStepId, a => a.ConnectedSystemId, step, stepsAtIndex.Count);
                var task = stepTasks?.FirstOrDefault(t => t.ScheduleStepId == step.Id)
                           ?? MatchUnidentified(stepTasks, t => t.ScheduleStepId, t => (t as SynchronisationWorkerTask)?.ConnectedSystemId, step, stepsAtIndex.Count);

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
                    // Only the reasons JIM writes when it cancels a step that had not started. A step cancelled while
                    // running did run, and its message is whatever progress it last reported.
                    CancellationReason = activity is { Status: ActivityStatus.Cancelled } && ScheduleStepNotRunReasons.IsNotRunReason(activity.Message)
                        ? activity.Message
                        : null,
                    ActivityId = activity?.Id,
                    ActivityStatus = activity?.Status,
                    // The effective behaviour, from the step's own setting or its Schedule's (#1787), and where it comes from.
                    ContinueOnFailure = ScheduleFailureHandling.ContinuesOnFailure(step, execution.Schedule),
                    FailureBehaviourSource = ScheduleFailureHandling.Source(step)
                });
            }
        }

        return detail;
    }

    /// <summary>
    /// The fallback match for a step's Activity or Worker Task when none records the step itself: those recorded before
    /// steps were identified. Matched on the Connected System, or, where the step index holds a single step, taken as
    /// whatever is there. Only records that identify no step are considered, so one belonging to a sibling is never
    /// borrowed.
    /// </summary>
    private static T? MatchUnidentified<T>(
        List<T>? candidates,
        Func<T, Guid?> stepIdOf,
        Func<T, int?> connectedSystemIdOf,
        ScheduleStep step,
        int stepsAtIndex) where T : class
    {
        var unidentified = candidates?.Where(c => stepIdOf(c) == null).ToList();
        if (unidentified == null || unidentified.Count == 0)
            return null;

        return unidentified.FirstOrDefault(c => connectedSystemIdOf(c) == step.ConnectedSystemId)
               ?? (stepsAtIndex == 1 ? unidentified[0] : null);
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
    /// How long a Schedule Execution may stay Queued before the Scheduler's safety net treats its start as abandoned
    /// (#1768). Starting a Schedule only queues a Worker Task per step, which takes seconds; an execution still Queued
    /// after this long was left part-way through starting, most likely by a JIM service stopping.
    /// </summary>
    public static readonly TimeSpan StaleStartThreshold = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Starts execution of a schedule (#1768). Creates the Schedule Execution as Queued and queues a Worker Task for
    /// every step as WaitingForPreviousStep, so nothing is runnable, and neither the Worker nor the Scheduler's safety
    /// net acts on the execution, while it is only part-started. Once every step has been queued, the execution is
    /// switched to InProgress and its first step group released, atomically and only if it is still Queued, so a
    /// cancellation made during the start stands. From then on the Worker drives step advancement via
    /// TaskingServer.TryAdvanceScheduleExecutionAsync.
    /// </summary>
    /// <remarks>
    /// A step JIM refuses to queue (its Connected System is being deleted, say) gets a Failed Activity saying why. If
    /// the step continues on failure (its own setting, or its Schedule's when it follows the Schedule; #1787) it is
    /// skipped and the rest of the Schedule runs without it, and the execution ends Complete With Error rather than
    /// Complete. Otherwise the start stops: the steps already queued are cancelled, the execution is marked Failed with
    /// the reason, and the error is thrown to the caller, which reports it (the Scheduler's due-schedule loop logs it;
    /// the Run endpoint returns it). An unexpected error takes the same stopping path whatever the step's setting, and is rethrown as
    /// it is. Nothing runs in either case.
    /// </remarks>
    /// <param name="schedule">The schedule to execute (must include Steps).</param>
    /// <param name="initiatorType">The type of principal initiating the execution.</param>
    /// <param name="initiatorId">The ID of the principal initiating the execution.</param>
    /// <param name="initiatorName">The name of the principal at time of execution.</param>
    /// <returns>The created ScheduleExecution, or null if the schedule has no steps.</returns>
    /// <exception cref="InvalidOperationException">A step set to stop the Schedule could not be queued. The message is
    /// the reason recorded on the execution.</exception>
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

        var distinctStepIndices = schedule.Steps
            .Select(s => s.StepIndex)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        Log.Information("StartScheduleExecutionAsync: Starting execution of schedule {ScheduleId} ({ScheduleName}) with {StepCount} steps across {GroupCount} step groups.",
            schedule.Id, schedule.Name, schedule.Steps.Count, distinctStepIndices.Count);

        var execution = new ScheduleExecution
        {
            ScheduleId = schedule.Id,
            ScheduleName = schedule.Name,
            // Held at Queued until every step has been queued; see the summary. It gets a start time when its first
            // step group is released, not before.
            Status = ScheduleExecutionStatus.Queued,
            CurrentStepIndex = distinctStepIndices[0],
            // Step groups, not step rows: CurrentStepIndex advances one group at a time, and the two are
            // read together as "step X of Y". Steps sharing a StepIndex are one position, not several.
            TotalSteps = distinctStepIndices.Count,
            InitiatedByType = initiatorType,
            InitiatedById = initiatorId,
            InitiatedByName = initiatorName
        };
        await Application.Repository.Scheduling.CreateScheduleExecutionAsync(execution);

        // From here on, anything that goes wrong is recorded on the execution before it is rethrown, so it never sits
        // Queued (or worse, half-started) with no explanation.
        try
        {
            schedule.LastRunTime = DateTime.UtcNow;
            await Application.Repository.Scheduling.UpdateScheduleAsync(schedule);
        }
        catch (Exception ex)
        {
            await FailStartAsync(execution, $"The Schedule could not start: {AsClause(ex.Message)}. No steps ran.");
            throw;
        }

        int? firstStepIndexWithTasks = null;
        var tasksQueued = 0;
        // The steps that could not be queued but continue on failure, with the Failed Activity recorded for each.
        var skippedSteps = new List<(ScheduleStep Step, Activity? Activity)>();

        foreach (var stepIndex in distinctStepIndices)
        {
            var stepsAtIndex = schedule.Steps.Where(s => s.StepIndex == stepIndex).ToList();
            var isParallelGroup = stepsAtIndex.Count > 1;
            if (isParallelGroup)
            {
                Log.Information("StartScheduleExecutionAsync: Step index {StepIndex} is a parallel group with {Count} steps for execution {ExecutionId}.",
                    stepIndex, stepsAtIndex.Count, execution.Id);
            }

            foreach (var step in stepsAtIndex)
            {
                var workerTask = BuildWorkerTask(execution, step, isParallelGroup, initiatorType, initiatorId, initiatorName);
                if (workerTask == null)
                {
                    Log.Warning("StartScheduleExecutionAsync: Step type {StepType} is not yet implemented. Skipping step {StepId} of execution {ExecutionId}.",
                        step.StepType, step.Id, execution.Id);
                    continue;
                }

                string? refusal;
                try
                {
                    refusal = await TryQueueWorkerTaskAsync(step, workerTask);
                }
                catch (Exception ex)
                {
                    // Continue On Failure covers a step JIM refuses to queue, not an unexpected error such as the
                    // database becoming unavailable part-way through, which stops the start whatever the setting.
                    Log.Error(ex, "StartScheduleExecutionAsync: Unexpected error queuing step {StepId} of execution {ExecutionId}. The Schedule will not start.",
                        step.Id, execution.Id);
                    var stepActivity = await RecordStepNotQueuedAsync(execution, step, workerTask, ex.Message);
                    await FailStartAsync(execution, DescribeStartFailure(step, stepActivity, ex.Message));
                    throw;
                }

                if (refusal == null)
                {
                    // Step indices are visited in ascending order, so the first one to queue anything is the first to run.
                    firstStepIndexWithTasks ??= stepIndex;
                    tasksQueued++;
                    continue;
                }

                var failedActivity = await RecordStepNotQueuedAsync(execution, step, workerTask, refusal);

                if (ScheduleFailureHandling.ContinuesOnFailure(step, schedule))
                {
                    Log.Warning("StartScheduleExecutionAsync: Step {StepId} of execution {ExecutionId} could not be queued ({Reason}). It is set to continue on failure (from the {Source}), so the rest of the Schedule will run without it.",
                        step.Id, execution.Id, refusal, ScheduleFailureHandling.Source(step));
                    skippedSteps.Add((step, failedActivity));
                    continue;
                }

                var message = DescribeStartFailure(step, failedActivity, refusal);
                Log.Warning("StartScheduleExecutionAsync: Step {StepId} of execution {ExecutionId} could not be queued ({Reason}) and is set to stop the Schedule. The Schedule will not start.",
                    step.Id, execution.Id, refusal);
                await FailStartAsync(execution, message);
                throw new InvalidOperationException(message);
            }
        }

        try
        {
            await ReleaseFirstStepGroupAsync(execution, firstStepIndexWithTasks, skippedSteps);
        }
        catch (Exception ex)
        {
            await FailStartAsync(execution, $"The Schedule could not start: {AsClause(ex.Message)}. No steps ran.");
            throw;
        }

        Log.Information("StartScheduleExecutionAsync: Execution {ExecutionId} of schedule {ScheduleId} ({ScheduleName}): {TasksQueued} task(s) queued, {StepsSkipped} step(s) that could not be queued skipped, status {Status}.",
            execution.Id, schedule.Id, schedule.Name, tasksQueued, skippedSteps.Count, execution.Status);

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

        // Every Activity has finished: the step group is over, so decide what happens next exactly as the Worker would
        // have, had it not stopped before it could.
        Log.Information("CheckAndAdvanceExecutionAsync: Safety net concluding step {StepIndex} of execution {ExecutionId}.",
            currentStepIndex, execution.Id);

        return await ConcludeStepGroupAsync(freshExecution, currentStepIndex, activitiesForStep);
    }

    /// <summary>
    /// Decides what happens once every task in a step group has finished (#1768): stop the Schedule, move on to the
    /// next step group, or complete the execution: Complete when every step succeeded, Complete With Error when any step
    /// failed and was allowed to continue (#1787). The one implementation, shared by the Worker's advancement
    /// (TaskingServer.TryAdvanceScheduleExecutionAsync) and the Scheduler's safety net
    /// (<see cref="CheckAndAdvanceExecutionAsync"/>), so the two can never reach different conclusions about the same
    /// execution.
    /// </summary>
    /// <remarks>
    /// The caller must have loaded the execution with its Schedule and Steps and confirmed it was InProgress. Every
    /// change made here is conditional on it still being InProgress when the write lands, so a cancellation (or a
    /// competing conclusion) that arrives in between stands: a finished execution stays finished.
    /// </remarks>
    /// <param name="execution">The execution, with its Schedule and Steps loaded.</param>
    /// <param name="stepIndex">The step group that has just finished.</param>
    /// <param name="activitiesForStep">The Activities recorded for that step group.</param>
    /// <returns>True if the execution is still in progress; false if it stopped, completed, or had already finished.</returns>
    internal async Task<bool> ConcludeStepGroupAsync(ScheduleExecution execution, int stepIndex, IReadOnlyCollection<Activity> activitiesForStep)
    {
        var stopReason = GetReasonToStop(execution.Schedule, stepIndex, activitiesForStep);
        if (stopReason != null)
        {
            Log.Warning("ConcludeStepGroupAsync: Execution {ExecutionId} stops at step {StepIndex}: {Reason}",
                execution.Id, stepIndex, stopReason);

            // Cancel the remaining steps before marking the execution Failed, so an interruption between the two leaves
            // an InProgress execution the safety net will conclude again, never a Failed one with waiting tasks that
            // nothing would ever remove. A failure here is thrown, leaving exactly that retryable state.
            var cancelled = await Application.Repository.Tasking.DeleteWaitingTasksForExecutionAsync(
                execution.Id, ScheduleStepNotRunReasons.EarlierStepStoppedSchedule);
            if (cancelled > 0)
            {
                Log.Information("ConcludeStepGroupAsync: Cancelled {Count} remaining step task(s) of execution {ExecutionId}.",
                    cancelled, execution.Id);
            }

            if (!await Application.Repository.Scheduling.TryFinishScheduleExecutionAsync(
                    execution, [ScheduleExecutionStatus.InProgress], ScheduleExecutionStatus.Failed, stopReason))
            {
                Log.Information("ConcludeStepGroupAsync: Execution {ExecutionId} had already finished, so it is left as it is.", execution.Id);
            }

            return false;
        }

        var nextStepIndex = await Application.Repository.Tasking.GetNextWaitingStepIndexAsync(execution.Id);
        if (!nextStepIndex.HasValue)
        {
            // A step that failed and stopped the Schedule has already taken the Failed path above, so any failure found
            // here, in this or an earlier step group, is one that was allowed to let the Schedule continue.
            var failedActivities = (await Application.Repository.Activity.GetFailedScheduleExecutionActivitiesAsync(execution.Id))
                .Where(IsFailedStepOutcome)
                .ToList();
            var finalStatus = failedActivities.Count > 0 ? ScheduleExecutionStatus.CompleteWithError : ScheduleExecutionStatus.Complete;
            var continuedFailures = failedActivities.Count > 0
                ? DescribeContinuedFailures(failedActivities.Select(a => (a.ScheduleStepIndex ?? stepIndex, FindStep(execution.Schedule, a), (Activity?)a)))
                : null;

            if (await Application.Repository.Scheduling.TryFinishScheduleExecutionAsync(
                    execution, [ScheduleExecutionStatus.InProgress], finalStatus, continuedFailures))
            {
                if (finalStatus == ScheduleExecutionStatus.CompleteWithError)
                {
                    Log.Warning("ConcludeStepGroupAsync: Execution {ExecutionId} completed with error. All steps done; {FailedCount} failed step outcome(s) were set to let the Schedule continue: {Message}",
                        execution.Id, failedActivities.Count, continuedFailures);
                }
                else
                {
                    Log.Information("ConcludeStepGroupAsync: Execution {ExecutionId} completed. All steps done.", execution.Id);
                }
            }
            else
            {
                Log.Information("ConcludeStepGroupAsync: Execution {ExecutionId} had already finished, so it is left as it is.", execution.Id);
            }

            return false;
        }

        if (!await Application.Repository.Scheduling.TryAdvanceScheduleExecutionAsync(execution, nextStepIndex.Value))
        {
            Log.Information("ConcludeStepGroupAsync: Execution {ExecutionId} was not advanced to step {StepIndex}: it had already finished, or had already been advanced.",
                execution.Id, nextStepIndex.Value);
            return false;
        }

        Log.Information("ConcludeStepGroupAsync: Advanced execution {ExecutionId} from step {CompletedStep} to step {NextStep}.",
            execution.Id, stepIndex, nextStepIndex.Value);
        return true;
    }

    /// <summary>
    /// Whether a finished step group stops the Schedule, and if so, why, in words an administrator can act on; null
    /// when the Schedule carries on.
    /// </summary>
    /// <remarks>
    /// The failing step's effective behaviour decides (#1768, #1787): its own setting, or its Schedule's when it follows
    /// the Schedule, resolved by <see cref="ScheduleFailureHandling"/>. The Schedule stops if any step that FAILED stops
    /// it when it fails. A sibling that succeeded has no say, whatever its setting. Each failed Activity identifies its
    /// step by ScheduleStepId. Where one cannot (it was recorded before steps were identified, or its step has since
    /// been deleted), the group is judged by the older, stricter rule, failing safe: it stops if any step at that
    /// position stops the Schedule, or if the position no longer has any steps at all.
    /// </remarks>
    private static string? GetReasonToStop(Schedule? schedule, int stepIndex, IReadOnlyCollection<Activity> activitiesForStep)
    {
        var failed = activitiesForStep.Where(IsFailedStepOutcome).ToList();
        if (failed.Count == 0)
            return null;

        var stepsAtIndex = (schedule?.Steps ?? []).Where(s => s.StepIndex == stepIndex).ToList();
        var failedSteps = failed
            .Select(activity => (Activity: activity, Step: activity.ScheduleStepId is { } stepId
                ? stepsAtIndex.FirstOrDefault(s => s.Id == stepId)
                : null))
            .ToList();

        if (stepsAtIndex.Count == 0 || failedSteps.Any(f => f.Step == null))
        {
            if (stepsAtIndex.Count > 0 && stepsAtIndex.All(s => ScheduleFailureHandling.ContinuesOnFailure(s, schedule)))
                return null;

            return $"{StepSubject(stepIndex, failedSteps.Select(f => StepDisplayName(f.Step, f.Activity)))} failed, so the remaining steps did not run.";
        }

        var stopping = failedSteps.Where(f => !ScheduleFailureHandling.ContinuesOnFailure(f.Step!, schedule)).ToList();
        if (stopping.Count == 0)
            return null;

        var setting = stopping.Select(f => f.Step!.Id).Distinct().Count() == 1
            ? "It is set to stop the Schedule when it fails"
            : "They are set to stop the Schedule when they fail";
        return $"{StepSubject(stepIndex, stopping.Select(f => StepDisplayName(f.Step, f.Activity)))} failed. {setting}, so the remaining steps did not run.";
    }

    /// <summary>
    /// Whether an Activity records a step that did not succeed, for failure handling: it failed outright, completed with
    /// errors, or was cancelled (<see cref="ScheduleFailureHandling.FailedStepOutcomes"/>).
    /// </summary>
    private static bool IsFailedStepOutcome(Activity activity) => ScheduleFailureHandling.IsFailedStepOutcome(activity.Status);

    /// <summary>
    /// The step an Activity was recorded for, where it names one that still exists.
    /// </summary>
    private static ScheduleStep? FindStep(Schedule? schedule, Activity activity) =>
        activity.ScheduleStepId is { } stepId ? schedule?.Steps.FirstOrDefault(s => s.Id == stepId) : null;

    /// <summary>
    /// Why an execution that reached its end is Complete With Error (#1787), naming each failed step in step order:
    /// "The Schedule finished, but step 3, Active Directory - Export, failed. It is set to let the Schedule continue.",
    /// or for several step groups, "The Schedule finished, but steps 2 and 3 (HR - Full Synchronisation; Active
    /// Directory - Export) failed. They are set to let the Schedule continue.". Steps are named as the stop messages name
    /// them, and numbered 1-based, as the portal shows them.
    /// </summary>
    private static string DescribeContinuedFailures(IEnumerable<(int StepIndex, ScheduleStep? Step, Activity? Activity)> failures)
    {
        var failureList = failures.ToList();
        var byStepIndex = failureList
            .GroupBy(f => f.StepIndex)
            .OrderBy(g => g.Key)
            .Select(g => (StepIndex: g.Key, Names: g.Select(f => StepDisplayName(f.Step, f.Activity))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct()
                .ToList()))
            .ToList();

        // One step or several: a parallel group can fail more than once at the same position.
        var failedStepCount = failureList
            .Select(f => f.Step?.Id ?? f.Activity?.ScheduleStepId ?? f.Activity?.Id)
            .Distinct()
            .Count();
        var setting = failedStepCount == 1
            ? "It is set to let the Schedule continue."
            : "They are set to let the Schedule continue.";

        if (byStepIndex.Count == 1)
        {
            var only = byStepIndex[0];
            return $"The Schedule finished, but {LowerFirst(StepSubject(only.StepIndex, only.Names))} failed. {setting}";
        }

        var numbers = JoinNames(byStepIndex.Select(g => (g.StepIndex + 1).ToString()).ToList());
        var names = byStepIndex.All(g => g.Names.Count == 0)
            ? string.Empty
            : $" ({string.Join("; ", byStepIndex.Select(g => g.Names.Count > 0 ? JoinNames(g.Names) : $"step {g.StepIndex + 1}"))})";
        return $"The Schedule finished, but steps {numbers}{names} failed. {setting}";
    }

    /// <summary>
    /// A sentence-initial subject such as "Step 3" for use mid-sentence.
    /// </summary>
    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>
    /// Cancels a running or queued schedule execution.
    /// Sets the execution status to Cancelled, cancels all task activities,
    /// and deletes all tasks regardless of their current status.
    /// </summary>
    /// <remarks>
    /// The cancellation is written conditionally (#1768): it only takes effect while the execution is still Queued or
    /// InProgress, so an execution that finished between being read and being cancelled stays exactly as it finished.
    /// Steps that had not started record why they did not run.
    /// </remarks>
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

        if (!await Application.Repository.Scheduling.TryFinishScheduleExecutionAsync(
                execution,
                [ScheduleExecutionStatus.Queued, ScheduleExecutionStatus.InProgress],
                ScheduleExecutionStatus.Cancelled,
                "Cancelled by user"))
        {
            Log.Warning("CancelScheduleExecutionAsync: Execution {ExecutionId} finished before it could be cancelled, so it is left as it finished.",
                executionId);
            return false;
        }

        // Cancel all tasks: processing tasks are signalled for graceful cancellation,
        // queued/waiting tasks are cancelled and removed immediately.
        var tasks = await Application.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(executionId);
        var immediatelyCancelled = 0;
        var signalledForCancellation = 0;
        foreach (var task in tasks)
        {
            if (task.Status == WorkerTaskStatus.Processing)
            {
                // Task is actively being processed by the worker; signal it for cancellation.
                task.Status = WorkerTaskStatus.CancellationRequested;
                await Application.Repository.Tasking.UpdateWorkerTaskAsync(task);
                signalledForCancellation++;
            }
            else
            {
                if (task.Activity != null)
                {
                    // A step that never started says why it did not run. One already being cancelled did run, so its
                    // Activity keeps whatever it last reported.
                    if (task.Status is WorkerTaskStatus.Queued or WorkerTaskStatus.WaitingForPreviousStep)
                        task.Activity.Message = ScheduleStepNotRunReasons.ExecutionCancelled;

                    await Application.Activities.CancelActivityAsync(task.Activity);
                }

                await Application.Repository.Tasking.DeleteWorkerTaskAsync(task);
                immediatelyCancelled++;
            }
        }

        Log.Information("CancelScheduleExecutionAsync: Cancelled execution {ExecutionId}: {ImmediateCount} tasks cancelled immediately, {SignalledCount} processing tasks signalled for cancellation",
            executionId, immediatelyCancelled, signalledForCancellation);
        return true;
    }

    /// <summary>
    /// The Scheduler's safety net over active Schedule Executions, run once per polling cycle. Normally the Worker drives
    /// step transitions (TaskingServer.TryAdvanceScheduleExecutionAsync) as each task completes; this catches what it
    /// cannot:
    /// <list type="bullet">
    /// <item>An InProgress execution with nothing Queued or Processing, most likely because the Worker stopped after
    /// completing a task but before advancing: it is concluded exactly as the Worker would have concluded it.</item>
    /// <item>A Queued execution left part-way through starting (#1768), most likely because a JIM service stopped
    /// mid-start. A Queued execution is still starting, and is never advanced or completed here; only once it has been
    /// Queued for longer than <see cref="StaleStartThreshold"/> are its waiting steps cancelled and the execution
    /// failed. Only this safety net does that.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Each execution is handled in isolation: a failure with one is logged and the rest are still recovered.
    /// </remarks>
    public async Task RecoverStuckExecutionsAsync()
    {
        var activeExecutions = await GetActiveExecutionsAsync();
        int abandonedStarts = 0, concluded = 0, errors = 0;

        foreach (var execution in activeExecutions)
        {
            try
            {
                switch (execution.Status)
                {
                    case ScheduleExecutionStatus.Queued when DateTime.UtcNow - execution.QueuedAt > StaleStartThreshold:
                        if (await FailAbandonedStartAsync(execution))
                            abandonedStarts++;
                        break;

                    case ScheduleExecutionStatus.InProgress:
                        if (await ConcludeIfNothingIsRunningAsync(execution))
                            concluded++;
                        break;
                }
            }
            catch (Exception ex)
            {
                errors++;
                Log.Error(ex, "RecoverStuckExecutionsAsync: Error recovering execution {ExecutionId} of schedule {ScheduleName}. It will be tried again next cycle.",
                    execution.Id, execution.ScheduleName);
            }
        }

        if (abandonedStarts > 0 || concluded > 0 || errors > 0)
        {
            Log.Information("RecoverStuckExecutionsAsync: {ActiveCount} active execution(s): {AbandonedStarts} abandoned start(s) failed, {Concluded} stuck execution(s) concluded, {Errors} error(s).",
                activeExecutions.Count, abandonedStarts, concluded, errors);
        }
    }

    /// <summary>
    /// Fails an execution that never finished starting: cancels its waiting steps, saying the Schedule could not start,
    /// then marks it Failed if it is still Queued. The steps are removed first, and a failure to remove them is thrown,
    /// so the execution is only ever failed once nothing is left waiting; otherwise it stays Queued and is tried again
    /// next cycle.
    /// </summary>
    /// <returns>True if the execution was failed.</returns>
    private async Task<bool> FailAbandonedStartAsync(ScheduleExecution execution)
    {
        Log.Warning("FailAbandonedStartAsync: Execution {ExecutionId} of schedule {ScheduleName} has been Queued since {QueuedAt}, longer than any start takes. Failing it.",
            execution.Id, execution.ScheduleName, execution.QueuedAt);

        await Application.Repository.Tasking.DeleteWaitingTasksForExecutionAsync(execution.Id, ScheduleStepNotRunReasons.ScheduleCouldNotStart);

        return await Application.Repository.Scheduling.TryFinishScheduleExecutionAsync(
            execution,
            [ScheduleExecutionStatus.Queued],
            ScheduleExecutionStatus.Failed,
            "The Schedule did not finish starting, most likely because a JIM service stopped part-way through. No steps ran.");
    }

    /// <summary>
    /// Concludes an InProgress execution the Worker has lost track of: one with no Queued or Processing task, but with
    /// steps still waiting (the Worker stopped before advancing) or no tasks at all (the last step finished but the
    /// execution was never completed).
    /// </summary>
    /// <returns>True if the safety net acted on the execution.</returns>
    private async Task<bool> ConcludeIfNothingIsRunningAsync(ScheduleExecution execution)
    {
        var allTasks = await Application.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(execution.Id);
        if (allTasks.Any(t => t.Status is WorkerTaskStatus.Queued or WorkerTaskStatus.Processing))
            return false; // Normal operation: the Worker is handling it.

        var waitingCount = allTasks.Count(t => t.Status == WorkerTaskStatus.WaitingForPreviousStep);
        if (waitingCount > 0)
        {
            Log.Warning("ConcludeIfNothingIsRunningAsync: Execution {ExecutionId} for schedule {ScheduleName} has no active tasks but {WaitingCount} waiting tasks. Running safety-net advancement.",
                execution.Id, execution.ScheduleName, waitingCount);
        }
        else if (allTasks.Count == 0)
        {
            Log.Warning("ConcludeIfNothingIsRunningAsync: Execution {ExecutionId} for schedule {ScheduleName} has no tasks at all. Running safety-net completion.",
                execution.Id, execution.ScheduleName);
        }
        else
        {
            // Only tasks already being cancelled remain; the Worker finishes those.
            return false;
        }

        await CheckAndAdvanceExecutionAsync(execution);
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
    /// Builds the Worker Task that runs a Schedule Step, waiting for the execution to release it. Returns null for step
    /// types that are not yet implemented, which queue nothing.
    /// </summary>
    private static WorkerTask? BuildWorkerTask(
        ScheduleExecution execution,
        ScheduleStep step,
        bool isParallelGroup,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        WorkerTask? workerTask = step.StepType switch
        {
            // A Run Profile step missing its Connected System or Run Profile is refused before anything is queued (see
            // TryQueueWorkerTaskAsync); zero stands in only so the refusal can still be recorded against the step.
            ScheduleStepType.RunProfile => new SynchronisationWorkerTask
            {
                ConnectedSystemId = step.ConnectedSystemId ?? 0,
                ConnectedSystemRunProfileId = step.RunProfileId ?? 0
            },
            // Temporal Scope Reconciliation (issue #892) carries no per-step configuration; the worker derives its
            // watermark from the schedule's execution history at run time.
            ScheduleStepType.TemporalScopeReconciliation => new TemporalScopeReconciliationWorkerTask(),
            // History Retention Cleanup (issue #1118) carries none either; the worker reads every retention period from
            // its Service Setting at run time, so changing one takes effect on the next pass.
            ScheduleStepType.HistoryRetentionCleanup => new HistoryRetentionCleanupWorkerTask(),
            _ => null
        };

        if (workerTask == null)
            return null;

        // Every step starts out waiting, including the first: nothing is runnable until the whole Schedule has been
        // queued and the execution releases its first step group.
        workerTask.Status = WorkerTaskStatus.WaitingForPreviousStep;
        workerTask.InitiatedByType = initiatorType;
        workerTask.InitiatedById = initiatorId;
        workerTask.InitiatedByName = initiatorName;
        workerTask.ScheduleExecutionId = execution.Id;
        workerTask.ScheduleStepIndex = step.StepIndex;
        workerTask.ScheduleStepId = step.Id;
        // Use parallel execution if this step runs with others at the same index
        workerTask.ExecutionMode = isParallelGroup ? WorkerTaskExecutionMode.Parallel : WorkerTaskExecutionMode.Sequential;
        return workerTask;
    }

    /// <summary>
    /// Queues a step's Worker Task. Returns null once it is queued, or the reason JIM refused to queue it (its
    /// Connected System is being deleted, its partition configuration is incomplete, and so on), which Continue On
    /// Failure applies to. Anything unexpected is thrown.
    /// </summary>
    private async Task<string?> TryQueueWorkerTaskAsync(ScheduleStep step, WorkerTask workerTask)
    {
        if (step.StepType == ScheduleStepType.RunProfile && (!step.ConnectedSystemId.HasValue || !step.RunProfileId.HasValue))
            return "The step has no Connected System or Run Profile set.";

        var result = await Application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
            return string.IsNullOrWhiteSpace(result.ErrorMessage) ? "JIM refused to queue the step." : result.ErrorMessage;

        Log.Debug("TryQueueWorkerTaskAsync: Created worker task {TaskId} for step {StepId} of execution {ExecutionId}",
            result.WorkerTaskId, step.Id, workerTask.ScheduleExecutionId);
        return null;
    }

    /// <summary>
    /// Records a Failed Activity for a step that could not be queued, so its row on the Schedule Execution says why. A
    /// failure to record it is logged rather than thrown: the start's outcome must still be recorded on the execution,
    /// and the caller must still see the error that explains it.
    /// </summary>
    /// <returns>The Activity recorded, or null if it could not be.</returns>
    private async Task<Activity?> RecordStepNotQueuedAsync(ScheduleExecution execution, ScheduleStep step, WorkerTask workerTask, string reason)
    {
        try
        {
            return await Application.Tasking.RecordWorkerTaskNotQueuedAsync(workerTask, $"Could not be queued: {AsSentence(reason)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecordStepNotQueuedAsync: Could not record step {StepId} of execution {ExecutionId} as not queued.",
                step.Id, execution.Id);
            return null;
        }
    }

    /// <summary>
    /// Finishes a start once every step has been queued: releases the first step group with anything to run, or, where
    /// there is nothing at all to run, completes the execution: Complete With Error if any step could not be queued and
    /// was skipped (#1787), Complete otherwise. If the execution is no longer Queued (someone cancelled it while it was
    /// starting), it is left as it is and the steps queued since are removed.
    /// </summary>
    /// <param name="execution">The execution being started.</param>
    /// <param name="firstStepIndexWithTasks">The first step group that queued anything, or null if none did.</param>
    /// <param name="skippedSteps">The steps that could not be queued but continue on failure, each with the Failed
    /// Activity recorded for it (null where that could not be recorded).</param>
    private async Task ReleaseFirstStepGroupAsync(
        ScheduleExecution execution,
        int? firstStepIndexWithTasks,
        IReadOnlyCollection<(ScheduleStep Step, Activity? Activity)> skippedSteps)
    {
        bool tookEffect;
        if (firstStepIndexWithTasks is { } firstStepIndex)
        {
            tookEffect = await Application.Repository.Scheduling.TryStartScheduleExecutionAsync(execution, firstStepIndex);
            if (tookEffect)
            {
                Log.Information("ReleaseFirstStepGroupAsync: Execution {ExecutionId} started; step group {StepIndex} released.",
                    execution.Id, firstStepIndex);
            }
        }
        else
        {
            // Nothing to run: every step was of a type that queues nothing, or could not be queued and was set to
            // continue. There is nothing for the Worker to advance through, so the execution ends here. A skipped step
            // has a Failed Activity, so the run is not a clean one.
            var finalStatus = skippedSteps.Count > 0 ? ScheduleExecutionStatus.CompleteWithError : ScheduleExecutionStatus.Complete;
            var continuedFailures = skippedSteps.Count > 0
                ? DescribeContinuedFailures(skippedSteps.Select(s => (s.Step.StepIndex, (ScheduleStep?)s.Step, s.Activity)))
                : null;
            tookEffect = await Application.Repository.Scheduling.TryFinishScheduleExecutionAsync(
                execution, [ScheduleExecutionStatus.Queued], finalStatus, continuedFailures);
            if (tookEffect)
            {
                Log.Information("ReleaseFirstStepGroupAsync: Execution {ExecutionId} had no step with anything to run, so it is {Status}; {SkippedCount} step(s) could not be queued and were skipped.",
                    execution.Id, finalStatus, skippedSteps.Count);
            }
        }

        if (tookEffect)
            return;

        var current = await Application.Repository.Scheduling.GetScheduleExecutionAsync(execution.Id);
        Log.Warning("ReleaseFirstStepGroupAsync: Execution {ExecutionId} was no longer Queued (status {Status}) when its steps had been queued, so nothing was released.",
            execution.Id, current?.Status);

        await TryCancelWaitingStepsAsync(execution, current?.Status == ScheduleExecutionStatus.Cancelled
            ? ScheduleStepNotRunReasons.ExecutionCancelled
            : ScheduleStepNotRunReasons.ScheduleCouldNotStart);
    }

    /// <summary>
    /// Records a start that could not finish (#1768): cancels the steps already queued, saying the Schedule could not
    /// start, then marks the execution Failed with the reason, provided it is still Queued (a cancellation made in the
    /// meantime stands). Nothing is thrown from here; the caller rethrows the error that explains what happened.
    /// </summary>
    /// <remarks>
    /// In that order, and the second step only if the first succeeds, so an interruption can only ever leave a Queued
    /// execution for the Scheduler's stale-start safety net to finish off (see <see cref="StaleStartThreshold"/>),
    /// never a Failed one whose waiting tasks nothing will ever remove.
    /// </remarks>
    private async Task FailStartAsync(ScheduleExecution execution, string message)
    {
        if (!await TryCancelWaitingStepsAsync(execution, ScheduleStepNotRunReasons.ScheduleCouldNotStart))
            return;

        try
        {
            if (!await Application.Repository.Scheduling.TryFinishScheduleExecutionAsync(
                    execution, [ScheduleExecutionStatus.Queued], ScheduleExecutionStatus.Failed, message))
            {
                Log.Warning("FailStartAsync: Execution {ExecutionId} of schedule {ScheduleId} ({ScheduleName}) was no longer Queued, most likely cancelled while starting, so it is left as it is.",
                    execution.Id, execution.ScheduleId, execution.ScheduleName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FailStartAsync: Could not record execution {ExecutionId} of schedule {ScheduleId} ({ScheduleName}) as Failed after its start failed. The Scheduler will fail it once it has been Queued for {Threshold}.",
                execution.Id, execution.ScheduleId, execution.ScheduleName, StaleStartThreshold);
        }
    }

    /// <summary>
    /// Removes an execution's waiting steps, recording why each did not run, logging rather than throwing any failure.
    /// </summary>
    /// <returns>True if the waiting steps were removed; false if that failed.</returns>
    private async Task<bool> TryCancelWaitingStepsAsync(ScheduleExecution execution, string reason)
    {
        try
        {
            var cancelled = await Application.Repository.Tasking.DeleteWaitingTasksForExecutionAsync(execution.Id, reason);
            if (cancelled > 0)
            {
                Log.Information("TryCancelWaitingStepsAsync: Cancelled {Count} waiting task(s) of execution {ExecutionId}: {Reason}",
                    cancelled, execution.Id, reason);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "TryCancelWaitingStepsAsync: Could not cancel the waiting tasks of execution {ExecutionId}. The Scheduler will finish cleaning up once it has been Queued for {Threshold}.",
                execution.Id, StaleStartThreshold);
            return false;
        }
    }

    /// <summary>
    /// The reason a start failed at a step, as recorded on the execution: which step, what it runs, and why.
    /// </summary>
    private static string DescribeStartFailure(ScheduleStep step, Activity? stepActivity, string reason) =>
        $"The Schedule could not start. {StepSubject(step.StepIndex, [StepDisplayName(step, stepActivity)])} could not be queued: {AsClause(reason)}. No steps ran.";

    /// <summary>
    /// "Step 3" on its own, or "Step 3, Active Directory - Full Import," when there are names to give, ready to be
    /// followed by a verb. Step numbers are 1-based, as the portal shows them.
    /// </summary>
    private static string StepSubject(int stepIndex, IEnumerable<string?> names)
    {
        var knownNames = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();
        return knownNames.Count == 0
            ? $"Step {stepIndex + 1}"
            : $"Step {stepIndex + 1}, {JoinNames(knownNames)},";
    }

    /// <summary>
    /// "A", "A and B", or "A, B and C".
    /// </summary>
    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}"
    };

    /// <summary>
    /// What to call a step in a message. Run Profile steps store no name of their own, so they are named the way the
    /// Operations queue names their task ("Connected System - Run Profile"), from the Activity they produced; other
    /// steps use their stored name. Null when there is nothing to call it beyond its number.
    /// </summary>
    private static string? StepDisplayName(ScheduleStep? step, Activity? activity)
    {
        if (activity is { TargetName.Length: > 0 } && (step == null || step.StepType == ScheduleStepType.RunProfile))
        {
            return string.IsNullOrEmpty(activity.TargetContext)
                ? activity.TargetName
                : $"{activity.TargetContext} - {activity.TargetName}";
        }

        if (!string.IsNullOrWhiteSpace(step?.Name))
            return step.Name;

        return string.IsNullOrEmpty(activity?.TargetName) ? null : activity.TargetName;
    }

    /// <summary>
    /// A reason as a clause to be embedded in a longer sentence: trimmed, without its closing full stop.
    /// </summary>
    private static string AsClause(string reason) => reason.Trim().TrimEnd('.');

    /// <summary>
    /// A reason as a sentence of its own: trimmed, and ending with a full stop.
    /// </summary>
    private static string AsSentence(string reason) => $"{AsClause(reason)}.";

    /// <summary>
    /// Derives the failure-safe watermark for a Temporal Scope Reconciliation sweep (issue #892): the start time
    /// of the previous successfully completed execution of the same schedule. Because a failed sweep never reaches
    /// Complete status, its window is re-covered by the next sweep rather than silently skipped. Complete With Error
    /// (#1787) deliberately does not count either, although it is finished everywhere else: a run that carried on past a
    /// failed step may have missed part of its window. Returns null when there is no prior completed execution (the
    /// first, bootstrap sweep, which considers every transitioned object once).
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
