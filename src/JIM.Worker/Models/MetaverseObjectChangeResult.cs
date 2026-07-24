// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Sync;

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
    /// The number of Attribute Flows that occurred alongside a non-AttributeFlow change type
    /// (e.g., Join, Projection, DisconnectedOutOfScope). This prevents Attribute Flows from being
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
    /// values in the causality tree's expandable attribute change table. Includes attributes handed
    /// over to a re-elected surviving contributor as well as those genuinely cleared: mirrors
    /// ProcessObsoleteConnectedSystemObjectAsync, which records the full removals list so the change
    /// record reflects the whole handover, not just the blank.
    /// Only populated for DisconnectedOutOfScope when attribute recall occurred.
    /// </summary>
    public List<MetaverseObjectAttributeValue>? RecalledAttributeValues { get; init; }

    /// <summary>
    /// MVO attribute values re-elected from a surviving lower-priority contributor during disconnection (#91).
    /// Captured before applying pending changes, alongside <see cref="RecalledAttributeValues"/>, so the caller
    /// can add both to _pendingMvoChanges: the recalled value's removal and the survivor's value being added are
    /// two sides of the same handover, and both must appear in the MVO change record for the RPEI detail page to
    /// show it accurately (a value change, not a clear).
    /// Only populated for DisconnectedOutOfScope when attribute recall occurred.
    /// </summary>
    public List<MetaverseObjectAttributeValue>? RecalledAttributeAdditions { get; init; }

    /// <summary>
    /// The MVO that was disconnected. Needed by the caller to create MVO change tracking records,
    /// because the CSO→MVO join has already been broken by the time the result is returned.
    /// Only populated for DisconnectedOutOfScope when attribute recall occurred.
    /// </summary>
    public MetaverseObject? DisconnectedMvo { get; init; }

    /// <summary>
    /// The id of the MVO that was disconnected, captured before the CSO→MVO join was broken.
    /// Unlike <see cref="DisconnectedMvo"/>, this is populated for EVERY DisconnectedOutOfScope
    /// result (including immediate deletion, where attribute recall is skipped), so the caller
    /// can build sync outcome nodes that identify the affected Metaverse Object (#1086).
    /// </summary>
    public Guid? DisconnectedMvoId { get; init; }

    /// <summary>
    /// A display name snapshot of the disconnected MVO, captured before the join was broken and
    /// before any deletion, paired with <see cref="DisconnectedMvoId"/> so sync outcome nodes can
    /// describe the affected Metaverse Object even after it has been deleted (#1086).
    /// </summary>
    public string? DisconnectedMvoDisplayName { get; init; }

    /// <summary>
    /// A human-readable reason for the Metaverse Object Deletion Rule decision (e.g. "last
    /// connector disconnected"), when the deletion rule was triggered. Threaded through to the
    /// MvoDeleted/MvoDeletionScheduled outcome's detail message (#1086). Null when the MVO was
    /// not deleted or no reason was determinable.
    /// </summary>
    public string? MvoDeletionReason { get; init; }

    /// <summary>
    /// The deletion grace period applied when <see cref="MvoDeletionFate"/> is DeletionScheduled,
    /// for inclusion in the MvoDeletionScheduled outcome's detail message. Null otherwise.
    /// </summary>
    public TimeSpan? MvoDeletionGracePeriod { get; init; }

    /// <summary>
    /// The id of the Synchronisation Rule attributed to this change, when one was determinable at
    /// decision time (#1085): the scoping rule the Connected System Object fell out of scope of for
    /// DisconnectedOutOfScope, or the projecting rule for Projected. Threaded through to the sync
    /// outcome node so the causality tree records which rule drove the change. Null when no single
    /// rule is attributable (e.g. Joined, AttributeFlow).
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// Snapshot of the attributed Synchronisation Rule's name at decision time, paired with
    /// <see cref="SyncRuleId"/> so the outcome's attribution survives later rule renames or deletions.
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// Creates a result indicating no changes occurred.
    /// </summary>
    public static MetaverseObjectChangeResult NoChanges() => new() { HasChanges = false };

    /// <summary>
    /// Creates a result indicating a projection (new MVO created).
    /// </summary>
    /// <param name="attributesAdded">The number of MVO attributes that were added by the projection.</param>
    /// <param name="projectionSyncRule">The Synchronisation Rule that caused the projection, when known, for outcome attribution (#1085).</param>
    public static MetaverseObjectChangeResult Projected(int attributesAdded, SyncRule? projectionSyncRule = null) => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.Projected,
        AttributesAdded = attributesAdded,
        SyncRuleId = projectionSyncRule?.Id,
        SyncRuleName = projectionSyncRule?.Name
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
    /// Creates a result indicating Attribute Flow occurred.
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
    /// of import Synchronisation Rule scoping criteria.
    /// </summary>
    /// <param name="attributeFlowCount">Optional count of attribute changes (removals and re-elected additions) that occurred during disconnection.</param>
    /// <param name="mvoDeletionFate">The fate of the MVO after the disconnection.</param>
    /// <param name="recalledAttributeValues">MVO attribute values that were recalled, for change tracking.</param>
    /// <param name="recalledAttributeAdditions">MVO attribute values re-elected from a surviving contributor, for change tracking.</param>
    /// <param name="disconnectedMvo">The MVO that was disconnected, for change tracking.</param>
    /// <param name="scopingSyncRule">The Synchronisation Rule whose scope the CSO fell out of, when determinable, for outcome attribution (#1085).</param>
    /// <param name="disconnectedMvoId">The disconnected MVO's id, captured before the join was broken, for outcome nodes (#1086).</param>
    /// <param name="disconnectedMvoDisplayName">A display name snapshot of the disconnected MVO, captured before deletion, for outcome nodes (#1086).</param>
    /// <param name="mvoDeletionReason">A human-readable Deletion Rule reason when the deletion rule was triggered (#1086).</param>
    /// <param name="mvoDeletionGracePeriod">The grace period applied when the deletion was scheduled (#1086).</param>
    public static MetaverseObjectChangeResult DisconnectedOutOfScope(
        int? attributeFlowCount = null,
        MvoDeletionFate mvoDeletionFate = MvoDeletionFate.NotDeleted,
        List<MetaverseObjectAttributeValue>? recalledAttributeValues = null,
        List<MetaverseObjectAttributeValue>? recalledAttributeAdditions = null,
        MetaverseObject? disconnectedMvo = null,
        SyncRule? scopingSyncRule = null,
        Guid? disconnectedMvoId = null,
        string? disconnectedMvoDisplayName = null,
        string? mvoDeletionReason = null,
        TimeSpan? mvoDeletionGracePeriod = null) => new()
    {
        HasChanges = true,
        ChangeType = ObjectChangeType.DisconnectedOutOfScope,
        AttributeFlowCount = attributeFlowCount,
        MvoDeletionFate = mvoDeletionFate,
        RecalledAttributeValues = recalledAttributeValues,
        RecalledAttributeAdditions = recalledAttributeAdditions,
        DisconnectedMvo = disconnectedMvo,
        SyncRuleId = scopingSyncRule?.Id,
        SyncRuleName = scopingSyncRule?.Name,
        DisconnectedMvoId = disconnectedMvoId,
        DisconnectedMvoDisplayName = disconnectedMvoDisplayName,
        MvoDeletionReason = mvoDeletionReason,
        MvoDeletionGracePeriod = mvoDeletionGracePeriod
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
