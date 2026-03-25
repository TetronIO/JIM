using JIM.Models.Core;

namespace JIM.Models.Sync;

/// <summary>
/// The result of evaluating what should happen to an obsolete CSO.
/// Returned by <c>ISyncEngine.EvaluateObsoleteCso</c>.
/// The orchestrator is responsible for creating RPEIs and persisting changes.
/// </summary>
public readonly struct ObsoleteDecision
{
    /// <summary>
    /// The action to take for the obsolete CSO.
    /// </summary>
    public ObsoleteAction Action { get; init; }

    /// <summary>
    /// MVO attribute values that should be recalled (removed) during disconnection.
    /// Only populated when <see cref="Action"/> is <see cref="ObsoleteAction.DisconnectAndRecall"/>.
    /// </summary>
    public IReadOnlyList<MetaverseObjectAttributeValue>? AttributesToRecall { get; init; }

    /// <summary>
    /// The MVO deletion decision, evaluated after disconnection.
    /// Only populated when <see cref="Action"/> is <see cref="ObsoleteAction.Disconnect"/>
    /// or <see cref="ObsoleteAction.DisconnectAndRecall"/>.
    /// </summary>
    public MvoDeletionDecision? DeletionDecision { get; init; }

    /// <summary>
    /// The out-of-scope action determined by the import sync rules.
    /// Only populated when <see cref="Action"/> is <see cref="ObsoleteAction.RetainJoin"/>.
    /// </summary>
    public bool RetainJoin { get; init; }

    /// <summary>
    /// Creates a decision to delete the CSO without disconnection (not joined to any MVO).
    /// </summary>
    public static ObsoleteDecision DeleteOnly() => new() { Action = ObsoleteAction.DeleteOnly };

    /// <summary>
    /// Creates a decision to delete the CSO that was already pre-disconnected during MVO deletion.
    /// </summary>
    public static ObsoleteDecision DeletePreDisconnected() => new() { Action = ObsoleteAction.DeletePreDisconnected };

    /// <summary>
    /// Creates a decision to disconnect the CSO from its MVO and delete it, without attribute recall.
    /// </summary>
    public static ObsoleteDecision Disconnect(MvoDeletionDecision deletionDecision) => new()
    {
        Action = ObsoleteAction.Disconnect,
        DeletionDecision = deletionDecision
    };

    /// <summary>
    /// Creates a decision to disconnect, recall contributed attributes, and delete the CSO.
    /// </summary>
    public static ObsoleteDecision DisconnectAndRecall(
        IReadOnlyList<MetaverseObjectAttributeValue> attributesToRecall,
        MvoDeletionDecision deletionDecision) => new()
    {
        Action = ObsoleteAction.DisconnectAndRecall,
        AttributesToRecall = attributesToRecall,
        DeletionDecision = deletionDecision
    };

    /// <summary>
    /// Creates a decision to retain the join (InboundOutOfScopeAction = RemainJoined).
    /// </summary>
    public static ObsoleteDecision KeepJoined() => new()
    {
        Action = ObsoleteAction.RetainJoin,
        RetainJoin = true
    };
}

/// <summary>
/// The action to take for an obsolete CSO.
/// </summary>
public enum ObsoleteAction
{
    /// <summary>CSO is not joined; simply delete it.</summary>
    DeleteOnly,
    /// <summary>CSO was already disconnected during MVO deletion; clean up.</summary>
    DeletePreDisconnected,
    /// <summary>Disconnect CSO from MVO, then delete (no attribute recall).</summary>
    Disconnect,
    /// <summary>Disconnect CSO, recall contributed attributes, then delete.</summary>
    DisconnectAndRecall,
    /// <summary>Retain the join per InboundOutOfScopeAction setting.</summary>
    RetainJoin
}
