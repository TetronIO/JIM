// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Activities;

/// <summary>
/// One step of a Run Profile execution as it actually happened (#454). Every phase a run can
/// perform is recorded when the run starts, so the Activity carries the whole journey: what is
/// done (with how long it took), what is happening now, and what is still to come.
/// </summary>
/// <remarks>
/// Rows are written by the worker as it moves through the run and are read by the portal, the API
/// and PowerShell. They survive the run, so a completed Activity can be opened days later and still
/// answer "where did the four hours go?". Rows are removed by cascade when the Activity is deleted.
/// </remarks>
public class ActivityPhase
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Run Profile execution Activity this phase belongs to.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// Position in the run, ascending. Assigned when the phases are recorded and never renumbered,
    /// so a phase discovered at runtime (a Connector entering a phase it did not declare) sorts
    /// after the phases that were declared.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The stable key the worker or Connector enters this phase by. A JIM phase key comes from
    /// <see cref="RunPhaseKeys"/>; a Connector phase key is the Connector's own, qualified with
    /// <see cref="ConnectorPhaseKeyPrefix"/> so the two vocabularies cannot collide.
    /// </summary>
    [MaxLength(200)]
    public string Key { get; set; } = null!;

    /// <summary>
    /// The administrator-facing step label, captured when the phase was recorded so historic
    /// Activities keep the wording that was in use at the time.
    /// </summary>
    [MaxLength(200)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// For a Connector's phase, the key of the JIM phase it runs inside; null for a JIM phase.
    /// This is what keeps the top-level step count the same whichever Connector is in use.
    /// </summary>
    [MaxLength(200)]
    public string? ParentKey { get; set; }

    public ActivityPhaseStatus Status { get; set; } = ActivityPhaseStatus.Pending;

    /// <summary>
    /// When the phase was first entered (UTC), or null if it was never entered.
    /// </summary>
    public DateTime? Started { get; set; }

    /// <summary>
    /// When the phase finished (UTC), or null while it is still running.
    /// </summary>
    public DateTime? Ended { get; set; }

    /// <summary>
    /// How long the phase took, or null while it is still running or if it never ran.
    /// </summary>
    [NotMapped]
    public TimeSpan? Duration => Started.HasValue && Ended.HasValue ? Ended.Value - Started.Value : null;

    /// <summary>
    /// The prefix applied to a Connector's own phase keys when they are recorded, so that a
    /// Connector cannot accidentally claim one of JIM's phase keys.
    /// </summary>
    public const string ConnectorPhaseKeyPrefix = "connector:";

    /// <summary>
    /// Qualifies a Connector-supplied phase key for storage.
    /// </summary>
    public static string QualifyConnectorKey(string connectorPhaseKey) => ConnectorPhaseKeyPrefix + connectorPhaseKey;
}
