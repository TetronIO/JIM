// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;

namespace JIM.Models.Sync;

/// <summary>
/// The result of evaluating the MVO deletion rule after a CSO is disconnected.
/// Returned by <c>ISyncEngine.EvaluateMvoDeletionRule</c>.
/// The orchestrator is responsible for persisting the decision (queuing immediate deletion
/// or updating the MVO's LastConnectorDisconnectedDate).
/// </summary>
public readonly struct MvoDeletionDecision
{
    /// <summary>
    /// The fate determined by the deletion rule evaluation.
    /// </summary>
    public MvoDeletionFate Fate { get; init; }

    /// <summary>
    /// For <see cref="MvoDeletionFate.DeletionScheduled"/>: the grace period before deletion.
    /// Null for other fates.
    /// </summary>
    public TimeSpan? GracePeriod { get; init; }

    /// <summary>
    /// A human-readable reason for the decision (for logging and audit).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The machine-readable reason the decision was reached, for causal edge attribution (#1223).
    /// <see cref="CausalReasonCode.NotSet"/> whenever the fate is
    /// <see cref="MvoDeletionFate.NotDeleted"/>, because a decision not to delete produces no effect
    /// for an edge to point at.
    /// </summary>
    /// <remarks>
    /// This exists alongside <see cref="Reason"/> rather than being derived from it because cohort
    /// grouping keys on it. The sentence interpolates the Connected System name, which the attribution
    /// tuple carries separately, so grouping on the sentence would be redundant, would change silently
    /// with any rewording, and would collapse every cohort to one member the moment a per-object
    /// element entered it.
    /// </remarks>
    public CausalReasonCode ReasonCode { get; init; }

    /// <summary>
    /// Creates a decision indicating the MVO should not be deleted.
    /// </summary>
    public static MvoDeletionDecision NotDeleted(string? reason = null) => new()
    {
        Fate = MvoDeletionFate.NotDeleted,
        Reason = reason
    };

    /// <summary>
    /// Creates a decision indicating the MVO should be deleted immediately (0 grace period).
    /// </summary>
    public static MvoDeletionDecision DeleteImmediately(string reason, CausalReasonCode reasonCode) => new()
    {
        Fate = MvoDeletionFate.DeletedImmediately,
        Reason = reason,
        ReasonCode = reasonCode
    };

    /// <summary>
    /// Creates a decision indicating the MVO should be scheduled for deletion after a grace period.
    /// </summary>
    public static MvoDeletionDecision ScheduleDeletion(TimeSpan gracePeriod, string reason, CausalReasonCode reasonCode) => new()
    {
        Fate = MvoDeletionFate.DeletionScheduled,
        GracePeriod = gracePeriod,
        Reason = reason,
        ReasonCode = reasonCode
    };
}
