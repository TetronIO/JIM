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
    public static MvoDeletionDecision DeleteImmediately(string reason) => new()
    {
        Fate = MvoDeletionFate.DeletedImmediately,
        Reason = reason
    };

    /// <summary>
    /// Creates a decision indicating the MVO should be scheduled for deletion after a grace period.
    /// </summary>
    public static MvoDeletionDecision ScheduleDeletion(TimeSpan gracePeriod, string reason) => new()
    {
        Fate = MvoDeletionFate.DeletionScheduled,
        GracePeriod = gracePeriod,
        Reason = reason
    };
}
