// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
namespace JIM.Models.Tasking.DTOs;

public class WorkerTaskHeader
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Type { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public WorkerTaskStatus Status { get; set; }

    /// <summary>
    /// The type of security principal that initiated this task.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

    /// <summary>
    /// The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this task.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The name of the security principal at the time of task creation.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// The associated Activity ID (if the task has started processing).
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// The total number of objects to process (from the associated Activity).
    /// </summary>
    public int? ObjectsToProcess { get; set; }

    /// <summary>
    /// The number of objects processed so far (from the associated Activity).
    /// </summary>
    public int? ObjectsProcessed { get; set; }

    /// <summary>
    /// The current progress message (from the associated Activity).
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// The run's steps and the one running now (#1162), or null where the task is not a Run Profile
    /// execution and so has no steps: clearing Connected System Objects, example data generation and
    /// factory reset all record none. A list view showing this task falls back to the object counts
    /// alone in that case.
    /// </summary>
    public RunPhaseSummary? Steps { get; set; }

    /// <summary>
    /// Whether the worker runs this task on its own or together with the tasks queued alongside it.
    /// </summary>
    public WorkerTaskExecutionMode ExecutionMode { get; set; } = WorkerTaskExecutionMode.Sequential;

    /// <summary>
    /// The Run Profile this task executes, or null where the task is not a Run Profile execution.
    /// </summary>
    public int? ConnectedSystemRunProfileId { get; set; }

    /// <summary>
    /// The name of the Connected System whose Run Profile this task executes, or null where the task is not a Run
    /// Profile execution or the Run Profile no longer exists.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The name of the Run Profile this task executes, or null where the task is not a Run Profile execution or the
    /// Run Profile no longer exists.
    /// </summary>
    public string? RunProfileName { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule execution context - for grouping tasks by schedule in the queue UI
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// If this task is part of a schedule execution, the execution ID for UI grouping.
    /// </summary>
    public Guid? ScheduleExecutionId { get; set; }

    /// <summary>
    /// The schedule name at execution time, for display in the queue UI.
    /// </summary>
    public string? ScheduleExecutionName { get; set; }

    /// <summary>
    /// The step index within the schedule, for ordering within the group.
    /// </summary>
    public int? ScheduleStepIndex { get; set; }

    /// <summary>
    /// The number of step groups the Schedule Execution is working through.
    /// </summary>
    /// <remarks>
    /// Needed to draw the whole Schedule rather than only the part of it still in the queue (#1162):
    /// finished steps have had their tasks deleted, so a rail counted from the queue alone would shorten
    /// as the Schedule progressed.
    /// </remarks>
    public int? ScheduleTotalSteps { get; set; }

    /// <summary>
    /// The step group the Schedule Execution has reached, counting from 0.
    /// </summary>
    /// <remarks>
    /// Settles where a step that left no record of its own sits relative to the Schedule's progress.
    /// </remarks>
    public int? ScheduleCurrentStepIndex { get; set; }

    /// <summary>
    /// Brings this header up to date with a later read of the same task, keeping this instance, and reports whether
    /// anything changed.
    /// </summary>
    /// <remarks>
    /// The Operations queue's grid keys each row by its item, so replacing a header with the fresh one a read returns
    /// tears its row down and rebuilds it. Progress arrives many times a second during a large run, and a link
    /// rebuilt between a mouse press and its release never receives the click. Updating in place lets the row be
    /// redrawn where it stands. Change is judged on values, because every read returns fresh instances.
    /// </remarks>
    public bool UpdateFrom(WorkerTaskHeader latest)
    {
        var changed =
            Name != latest.Name ||
            Type != latest.Type ||
            Timestamp != latest.Timestamp ||
            Status != latest.Status ||
            InitiatedByType != latest.InitiatedByType ||
            InitiatedById != latest.InitiatedById ||
            InitiatedByName != latest.InitiatedByName ||
            ActivityId != latest.ActivityId ||
            ObjectsToProcess != latest.ObjectsToProcess ||
            ObjectsProcessed != latest.ObjectsProcessed ||
            ProgressMessage != latest.ProgressMessage ||
            !StepsEqual(Steps, latest.Steps) ||
            ExecutionMode != latest.ExecutionMode ||
            ConnectedSystemRunProfileId != latest.ConnectedSystemRunProfileId ||
            ConnectedSystemName != latest.ConnectedSystemName ||
            RunProfileName != latest.RunProfileName ||
            ScheduleExecutionId != latest.ScheduleExecutionId ||
            ScheduleExecutionName != latest.ScheduleExecutionName ||
            ScheduleStepIndex != latest.ScheduleStepIndex ||
            ScheduleTotalSteps != latest.ScheduleTotalSteps ||
            ScheduleCurrentStepIndex != latest.ScheduleCurrentStepIndex;

        Name = latest.Name;
        Type = latest.Type;
        Timestamp = latest.Timestamp;
        Status = latest.Status;
        InitiatedByType = latest.InitiatedByType;
        InitiatedById = latest.InitiatedById;
        InitiatedByName = latest.InitiatedByName;
        ActivityId = latest.ActivityId;
        ObjectsToProcess = latest.ObjectsToProcess;
        ObjectsProcessed = latest.ObjectsProcessed;
        ProgressMessage = latest.ProgressMessage;
        Steps = latest.Steps;
        ExecutionMode = latest.ExecutionMode;
        ConnectedSystemRunProfileId = latest.ConnectedSystemRunProfileId;
        ConnectedSystemName = latest.ConnectedSystemName;
        RunProfileName = latest.RunProfileName;
        ScheduleExecutionId = latest.ScheduleExecutionId;
        ScheduleExecutionName = latest.ScheduleExecutionName;
        ScheduleStepIndex = latest.ScheduleStepIndex;
        ScheduleTotalSteps = latest.ScheduleTotalSteps;
        ScheduleCurrentStepIndex = latest.ScheduleCurrentStepIndex;

        return changed;
    }

    private static bool StepsEqual(RunPhaseSummary? a, RunPhaseSummary? b)
    {
        if (a == null || b == null)
            return a == b;

        return a.CurrentStepName == b.CurrentStepName &&
               a.CurrentStepNumber == b.CurrentStepNumber &&
               a.TotalSteps == b.TotalSteps &&
               a.Steps.Select(s => (s.Order, s.Name, s.Status)).SequenceEqual(b.Steps.Select(s => (s.Order, s.Name, s.Status)));
    }
}