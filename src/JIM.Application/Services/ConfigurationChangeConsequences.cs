// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Services;

/// <summary>
/// Plain-English statements of what a destructive configuration change will actually do, keyed by the same snapshot
/// node keys the classifier is written against. Only Class A (destructive) properties carry copy: those are the
/// changes where the administrator is being asked to consent to consequences, and a vague warning is worse than none.
/// Class B changes need no per-property copy; the acknowledgement tells the administrator to run a Full
/// Synchronisation and lists what changed, which is the whole of the advice.
///
/// The copy is **direction-aware**: switching a Deprovision Action to Delete and switching it back are the same
/// property but opposite consequences, and warning about deletion when the administrator has just removed the risk
/// would teach them to dismiss the dialog. The *class* deliberately stays destructive either way, so that what was
/// consented to and what the change history records cannot disagree; only the wording moves.
///
/// <see cref="ConfigurationChangeConsequenceCompletenessTests"/> asserts every Class A key in
/// <see cref="ConfigurationChangeClassifier"/> has copy here, so a newly-classified destructive property cannot ship
/// with a blank consequence.
/// </summary>
public static class ConfigurationChangeConsequences
{
    // Raw snapshot values for booleans, as written by ConfigurationSnapshotService.Render(bool).
    private const string True = "true";

    /// <summary>
    /// The consequence of changing <paramref name="nodeKey"/> on <paramref name="objectType"/> from
    /// <paramref name="oldValue"/> to <paramref name="newValue"/>, or null when the property carries no curated copy
    /// (every non-destructive property, by design). Values are the snapshot's raw values, not display values, so the
    /// comparisons here are stable against display formatting changes.
    /// </summary>
    public static string? For(string objectType, string nodeKey, string? oldValue, string? newValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectType);
        ArgumentException.ThrowIfNullOrEmpty(nodeKey);

        return (objectType, nodeKey) switch
        {
            (ConfigurationSnapshotService.SyncRuleObjectType, "outboundDeprovisionAction") =>
                newValue == nameof(OutboundDeprovisionAction.Delete)
                    ? "Objects this rule deprovisions will be deleted in the Connected System rather than disconnected " +
                      "from JIM. Deletion happens in the target system and re-running synchronisation will not bring " +
                      "those objects back."
                    : "Objects this rule deprovisions will be disconnected from JIM and left in place in the Connected " +
                      "System, rather than deleted from it.",

            (ConfigurationSnapshotService.SyncRuleObjectType, "inboundOutOfScopeAction") =>
                newValue == nameof(InboundOutOfScopeAction.Disconnect)
                    ? "Every object that falls out of this rule's scope will be disconnected from its Metaverse Object. " +
                      "Attributes this rule contributed are withdrawn, and a Metaverse Object left with no remaining " +
                      "connectors may become eligible for deletion."
                    : "Objects that fall out of this rule's scope will stay joined to their Metaverse Objects instead of " +
                      "being disconnected.",

            // One key, two surfaces: Connected System Object Types and Partitions both snapshot their selection as
            // "selected", and deselecting either has the same shape of consequence.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, "selected") =>
                oldValue == True
                    ? "Deselecting this stops its objects being imported. The Connected System Objects already imported " +
                      "from it become obsolete, and whatever they are joined to is deprovisioned on the next " +
                      "synchronisation."
                    : "Selecting this brings its objects into scope for import on the next Import Run Profile.",

            // A container's selection is its presence in the snapshot, so this key arrives as a whole container being
            // added or removed rather than as a flag moving. Same consequence as deselecting a partition, phrased for
            // the narrower thing being taken out of scope.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, "container") =>
                string.IsNullOrEmpty(newValue)
                    ? "Deselecting this container stops the objects beneath it being imported. The Connected System " +
                      "Objects already imported from it become obsolete, and whatever they are joined to is " +
                      "deprovisioned on the next synchronisation."
                    : "Selecting this container brings the objects beneath it into scope for import on the next " +
                      "Import Run Profile.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, "deletionRule") =>
                "This takes effect immediately: Metaverse Objects of this type that already satisfy the new rule become " +
                "eligible for deletion on the next synchronisation or housekeeping pass, without any further change " +
                "being made to them.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, "deletionGracePeriod") =>
                "Metaverse Objects of this type awaiting deletion are held for this period. Shortening it brings forward " +
                "deletions that were pending, and any object already past the new period becomes eligible immediately.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, "deletionTriggerConnectedSystemIds") =>
                "This changes which Connected System disconnections trigger deletion. Metaverse Objects already " +
                "disconnected from a newly added trigger system become eligible for deletion immediately.",

            // One entry within that list. An addition has no old value; a removal has no new one.
            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, "connectedSystemId") =>
                string.IsNullOrEmpty(oldValue)
                    ? "Adding this Connected System as a deletion trigger takes effect immediately: Metaverse Objects " +
                      "of this type already disconnected from it become eligible for deletion without any further " +
                      "change being made to them."
                    : "Removing this Connected System as a deletion trigger stops disconnections from it making " +
                      "Metaverse Objects of this type eligible for deletion.",

            _ => null
        };
    }

    /// <summary>
    /// Whether curated copy exists for this property at all, in either direction. Used by the completeness test; the
    /// runtime path uses <see cref="For"/>.
    /// </summary>
    public static bool HasCopyFor(string objectType, string nodeKey) =>
        For(objectType, nodeKey, null, null) != null || For(objectType, nodeKey, True, null) != null;
}
