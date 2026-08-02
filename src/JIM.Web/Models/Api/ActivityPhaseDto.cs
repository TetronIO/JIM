// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// One step of a Run Profile execution (#454): what it was, how it turned out, and how long it
/// took. Returned in run order, so a client can show the whole journey (what is done, what is
/// running, and what is still to come) rather than only the current message.
/// </summary>
public class ActivityPhaseDto
{
    /// <summary>
    /// The step's stable identifier. JIM's own steps use keys such as "import.save"; a Connector's
    /// steps are prefixed "connector:". Use it to correlate across polls; show <see cref="Name"/>.
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>
    /// The administrator-facing step label, for example "Saving changes".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// For a Connector's step, the key of the JIM step it runs inside; null for a JIM step. Lets a
    /// client nest Connector detail under the step that called it.
    /// </summary>
    public string? ParentKey { get; set; }

    /// <summary>
    /// Position in the run, ascending.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// How the step turned out: Pending, Active, Completed, Skipped or Failed. Skipped means the
    /// run legitimately did not need it (no deletion detection on a Delta Import, no connection to
    /// open for a file-based import), not that anything went wrong.
    /// </summary>
    public ActivityPhaseStatus Status { get; set; }

    /// <summary>
    /// When the step was first entered (UTC), or null if it never ran.
    /// </summary>
    public DateTime? Started { get; set; }

    /// <summary>
    /// When the step finished (UTC), or null while it is still running.
    /// </summary>
    public DateTime? Ended { get; set; }

    /// <summary>
    /// How long the step took in seconds, or null while it is still running or if it never ran.
    /// </summary>
    public double? DurationSeconds { get; set; }

    public static ActivityPhaseDto FromEntity(ActivityPhase phase) => new()
    {
        Key = phase.Key,
        Name = phase.Name,
        ParentKey = phase.ParentKey,
        Order = phase.Order,
        Status = phase.Status,
        Started = phase.Started,
        Ended = phase.Ended,
        DurationSeconds = phase.Duration?.TotalSeconds
    };
}
