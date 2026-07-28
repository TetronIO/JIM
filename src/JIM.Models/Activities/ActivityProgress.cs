// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// Lightweight, read-only progress snapshot for a single Activity (#202). Built by a scalar
/// projection plus the Activity's stat counter rows, so it is cheap to serve at a high read
/// frequency while a Run Profile is executing; it never materialises the Activity's Run Profile
/// Execution Items. Consumed by the progress API endpoint and the Activity detail page.
/// </summary>
public class ActivityProgress
{
    public Guid ActivityId { get; set; }

    public ActivityStatus Status { get; set; }

    /// <summary>
    /// The Activity's current progress message; effectively the human-readable phase of the run
    /// (for example "Importing objects from Connected System" or "Saving changes").
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Objects processed so far within the current counting window. Some run phases reset this
    /// alongside <see cref="ObjectsToProcess"/> (for example moving from importing to saving), so
    /// consumers must treat a decrease as a new counting window, not an error.
    /// </summary>
    public int ObjectsProcessed { get; set; }

    /// <summary>
    /// Total objects expected within the current counting window; 0 means progress is indeterminate.
    /// </summary>
    public int ObjectsToProcess { get; set; }

    public DateTime Created { get; set; }

    /// <summary>
    /// When the system began executing the Activity, or null if execution has not started yet.
    /// </summary>
    public DateTime? Executed { get; set; }

    public ActivityTargetType TargetType { get; set; }

    /// <summary>
    /// The type of Run Profile being executed, when the Activity is a Run Profile execution.
    /// </summary>
    public ConnectedSystemRunType? RunType { get; set; }

    /// <summary>
    /// Live operation-type breakdown from the Activity's stat counter rows, keyed by
    /// <see cref="JIM.Models.Enums.ObjectChangeType"/> member name (for example "Added",
    /// "Updated", "Deleted", "Projected"). Empty when no counters have been recorded yet.
    /// </summary>
    public Dictionary<string, long> OperationCounts { get; set; } = new();

    /// <summary>
    /// Total errored objects so far, summed across the error-type stat counters.
    /// </summary>
    public long TotalErrors { get; set; }
}
