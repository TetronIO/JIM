// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Lightweight live-progress snapshot for an Activity (#202), designed for frequent polling and
/// push-triggered refreshes while a Run Profile executes. Deliberately much cheaper to serve than
/// the Activity detail response: it never touches the Activity's execution items.
/// </summary>
public class ActivityProgressDto
{
    /// <summary>
    /// The Activity's unique identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Activity's current status. Terminal statuses are Complete, CompleteWithWarning,
    /// CompleteWithError, FailedWithError and Cancelled; stop polling once one is reached.
    /// </summary>
    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The current progress message; effectively the human-readable phase of the run.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Objects processed so far within the current counting window. Some run phases reset the
    /// counters, so a decrease indicates a new phase, not lost work.
    /// </summary>
    public int ObjectsProcessed { get; set; }

    /// <summary>
    /// Total objects expected within the current counting window; 0 means indeterminate.
    /// </summary>
    public int ObjectsToProcess { get; set; }

    /// <summary>
    /// Percentage complete for the current counting window (0 to 100), or null when progress is
    /// indeterminate.
    /// </summary>
    public double? PercentComplete { get; set; }

    /// <summary>
    /// Seconds elapsed since the Activity began executing (or was created, when execution has
    /// not started yet).
    /// </summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>
    /// Objects processed per second over the recent sample window, or null when no rate is
    /// available yet (for example on the first read of a run).
    /// </summary>
    public double? ObjectsPerSecond { get; set; }

    /// <summary>
    /// Estimated seconds until the current counting window completes, or null when no estimate
    /// is available (no rate yet, zero rate, or indeterminate total).
    /// </summary>
    public double? EstimatedSecondsRemaining { get; set; }

    /// <summary>
    /// The type of object the Activity targets.
    /// </summary>
    public ActivityTargetType TargetType { get; set; }

    /// <summary>
    /// The type of Run Profile being executed, when the Activity is a Run Profile execution.
    /// </summary>
    public ConnectedSystemRunType? RunType { get; set; }

    /// <summary>
    /// Live operation-type breakdown keyed by object change type name (for example "Added",
    /// "Updated", "Deleted", "Projected", "Exported"). Advisory while the run is in flight;
    /// exact once the Activity completes. Empty when no operations have been recorded yet.
    /// </summary>
    public Dictionary<string, long> OperationCounts { get; set; } = new();

    /// <summary>
    /// Total errored objects so far.
    /// </summary>
    public long TotalErrors { get; set; }

    /// <summary>
    /// When this snapshot was taken (UTC).
    /// </summary>
    public DateTime RetrievedAt { get; set; }

    /// <summary>
    /// The steps of the run in order (#454): what is done, what is running, and what is still to
    /// come. Empty for Activities that are not Run Profile executions, and for runs that predate
    /// phase recording.
    /// </summary>
    public List<ActivityPhaseDto> Phases { get; set; } = [];

    /// <summary>
    /// The step currently running, or null when nothing is running. The most specific one: a
    /// Connector's step in preference to the JIM step hosting it.
    /// </summary>
    public ActivityPhaseDto? CurrentPhase { get; set; }

    /// <summary>
    /// The current step's position among the run's top-level steps (1-based), or null when nothing
    /// is running. Pairs with <see cref="TotalPhases"/> to read as "step 3 of 7".
    /// </summary>
    public int? CurrentPhaseNumber { get; set; }

    /// <summary>
    /// How many top-level steps the run has. Connector steps are not counted: they are detail
    /// within the step that called the Connector, so the count stays comparable across Connectors.
    /// </summary>
    public int TotalPhases { get; set; }

    public static ActivityProgressDto FromEntity(ActivityProgress progress, ActivityEtaEstimate eta, DateTime utcNow)
    {
        double? percentComplete = null;
        if (progress.ObjectsToProcess > 0)
            percentComplete = Math.Round(Math.Clamp(progress.ObjectsProcessed / (double)progress.ObjectsToProcess * 100d, 0d, 100d), 1);

        var startedAt = progress.Executed ?? progress.Created;

        // "Step 3 of 7" counts only top-level steps: a Connector's steps are detail inside the step
        // that called it, so counting them would make the same run read differently per Connector.
        var topLevelPhases = progress.Phases.Where(p => p.ParentKey == null).OrderBy(p => p.Order).ToList();
        var current = progress.CurrentPhase;
        var currentTopLevel = current == null
            ? null
            : current.ParentKey == null ? current : progress.Phases.SingleOrDefault(p => p.Key == current.ParentKey);
        var currentIndex = currentTopLevel == null ? -1 : topLevelPhases.FindIndex(p => p.Key == currentTopLevel.Key);
        int? currentPhaseNumber = currentIndex >= 0 ? currentIndex + 1 : null;

        return new ActivityProgressDto
        {
            Id = progress.ActivityId,
            Status = progress.Status,
            Message = progress.Message,
            ObjectsProcessed = progress.ObjectsProcessed,
            ObjectsToProcess = progress.ObjectsToProcess,
            PercentComplete = percentComplete,
            ElapsedSeconds = Math.Max(0d, (utcNow - startedAt).TotalSeconds),
            ObjectsPerSecond = eta.ObjectsPerSecond,
            EstimatedSecondsRemaining = eta.EstimatedSecondsRemaining,
            TargetType = progress.TargetType,
            RunType = progress.RunType,
            OperationCounts = progress.OperationCounts,
            TotalErrors = progress.TotalErrors,
            RetrievedAt = utcNow,
            // Ordered here rather than relying on the read: the contract is "in run order", and a
            // client should not have to sort what it is handed.
            Phases = progress.Phases.OrderBy(p => p.Order).Select(ActivityPhaseDto.FromEntity).ToList(),
            CurrentPhase = current == null ? null : ActivityPhaseDto.FromEntity(current),
            CurrentPhaseNumber = currentPhaseNumber,
            TotalPhases = topLevelPhases.Count
        };
    }
}
