using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Worker.Processors;

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
    /// The number of attribute flows that occurred alongside a non-AttributeFlow change type
    /// (e.g., Join, Projection, DisconnectedOutOfScope). This prevents attribute flows from being
    /// "absorbed" into the primary change type and becoming invisible in reporting.
    /// Only populated when the primary ChangeType is not AttributeFlow itself.
    /// </summary>
    public int? AttributeFlowCount { get; init; }

    /// <summary>
    /// The fate of the MVO after disconnection — whether it was deleted, scheduled for deletion,
    /// or left intact. Used to build the appropriate causality tree outcome.
    /// Only populated for Disconnected and DisconnectedOutOfScope change types.
    /// </summary>
    public MvoDeletionFate MvoDeletionFate { get; init; }

    /// <summary>
    /// MVO attribute values that were recalled (removed) during disconnection.
    /// Captured before applying pending changes so the caller can add them to _pendingMvoChanges
    /// for MVO change tracking. This enables the RPEI detail page to show recalled attribute
    /// values in the causality tree's expandable attribute change table.
    /// Only populated for DisconnectedOutOfScope when attribute recall occurred.
    /// </summary>
    public List<MetaverseObjectAttributeValue>? RecalledAttributeValues { get; init; }

    /// <summary>
    /// The MVO that was disconnected. Needed by the caller to create MVO change tracking records,
    /// because the CSO→MVO join has already been broken by the time the result is returned.
    /// Only populated for DisconnectedOutOfScope when attribute recall occurred.
    /// </summary>
    public MetaverseObject? DisconnectedMvo { get; init; }

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
    /// <param name="attributeFlowCount">Optional count of attribute removals that occurred during disconnection.</param>
    /// <param name="mvoDeletionFate">The fate of the MVO after the disconnection.</param>
    /// <param name="recalledAttributeValues">MVO attribute values that were recalled, for change tracking.</param>
    public static MetaverseObjectChangeResult DisconnectedOutOfScope(
        int? attributeFlowCount = null,
        MvoDeletionFate mvoDeletionFate = MvoDeletionFate.NotDeleted,
        List<MetaverseObjectAttributeValue>? recalledAttributeValues = null,
        MetaverseObject? disconnectedMvo = null) => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.DisconnectedOutOfScope,
        AttributeFlowCount = attributeFlowCount,
        MvoDeletionFate = mvoDeletionFate,
        RecalledAttributeValues = recalledAttributeValues,
        DisconnectedMvo = disconnectedMvo
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
