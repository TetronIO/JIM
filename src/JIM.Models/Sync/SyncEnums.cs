namespace JIM.Models.Sync;

/// <summary>
/// Describes the fate of a Metaverse Object after its deletion rule was evaluated
/// during CSO disconnection. Used to build the appropriate causality tree outcome.
/// </summary>
public enum MvoDeletionFate
{
    /// <summary>MVO was not marked for deletion (Manual rule, remaining connectors, non-authoritative source, etc.).</summary>
    NotDeleted,
    /// <summary>MVO was queued for immediate synchronous deletion (0 grace period).</summary>
    DeletedImmediately,
    /// <summary>MVO was marked for deferred deletion by housekeeping (grace period configured).</summary>
    DeletionScheduled
}
