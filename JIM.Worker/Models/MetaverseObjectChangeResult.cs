using JIM.Models.Core;
using JIM.Models.Enums;

namespace JIM.Worker.Models;

/// <summary>
/// Lightweight struct to track what MVO changes occurred during sync processing.
/// Using a struct to avoid heap allocations for the common "no changes" case.
/// </summary>
public readonly struct MetaverseObjectChangeResult
{
    /// <summary>
    /// Whether any changes occurred that warrant recording an execution item.
    /// </summary>
    public bool HasChanges { get; init; }

    /// <summary>
    /// The type of change that occurred.
    /// </summary>
    public ObjectChangeType ChangeType { get; init; }

    /// <summary>
    /// The number of MVO attributes that were added.
    /// </summary>
    public int AttributesAdded { get; init; }

    /// <summary>
    /// The number of MVO attributes that were removed.
    /// </summary>
    public int AttributesRemoved { get; init; }

    /// <summary>
    /// Creates a result indicating no changes occurred.
    /// </summary>
    public static MetaverseObjectChangeResult NoChanges() => new() { HasChanges = false };

    /// <summary>
    /// Creates a result indicating a projection (new MVO created).
    /// </summary>
    public static MetaverseObjectChangeResult Projected(int attributesAdded) => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.Projected,
        AttributesAdded = attributesAdded
    };

    /// <summary>
    /// Creates a result indicating a join to an existing MVO.
    /// </summary>
    public static MetaverseObjectChangeResult Joined(int attributesAdded = 0, int attributesRemoved = 0) => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.Joined,
        AttributesAdded = attributesAdded,
        AttributesRemoved = attributesRemoved
    };

    /// <summary>
    /// Creates a result indicating attribute flow occurred.
    /// </summary>
    public static MetaverseObjectChangeResult AttributeFlow(int attributesAdded, int attributesRemoved) => new()
    {
        HasChanges = attributesAdded > 0 || attributesRemoved > 0,
        ChangeType = ObjectChangeType.AttributeFlow,
        AttributesAdded = attributesAdded,
        AttributesRemoved = attributesRemoved
    };

    /// <summary>
    /// Creates a result indicating a CSO was disconnected from MVO (out of scope).
    /// </summary>
    public static MetaverseObjectChangeResult Disconnected() => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.Disconnected
    };

    /// <summary>
    /// Creates a result indicating a CSO was disconnected because it fell out of scope
    /// of import sync rule scoping criteria.
    /// </summary>
    public static MetaverseObjectChangeResult DisconnectedOutOfScope() => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.DisconnectedOutOfScope
    };

    /// <summary>
    /// Creates a result indicating a CSO fell out of scope but remained joined
    /// (InboundOutOfScopeAction = RemainJoined).
    /// </summary>
    public static MetaverseObjectChangeResult OutOfScopeRetainJoin() => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.OutOfScopeRetainJoin
    };
}
