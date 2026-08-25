// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

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

    // Snapshot node keys of the collection items whose own key is not enough to identify what changed. Connected
    // System Object Types and Partitions both carry a "selected" flag, and the two mean different things.
    private const string ObjectTypeNode = "objectType";

    /// <summary>
    /// The consequence of changing <paramref name="nodeKey"/> on <paramref name="objectType"/> from
    /// <paramref name="oldValue"/> to <paramref name="newValue"/>, or null when the property carries no curated copy
    /// (every non-destructive property, by design). Values are the snapshot's raw values, not display values, so the
    /// comparisons here are stable against display formatting changes.
    /// </summary>
    /// <param name="parentKey">The key of the snapshot node the property hangs from, where one property key serves
    /// more than one kind of item. Null where the caller has no tree to read it from (see
    /// <see cref="HasCopyFor"/>), which selects the copy for the more common of the two.</param>
    public static string? For(string objectType, string? parentKey, string nodeKey, string? oldValue, string? newValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectType);
        ArgumentException.ThrowIfNullOrEmpty(nodeKey);

        return (objectType, parentKey, nodeKey) switch
        {
            (ConfigurationSnapshotService.SyncRuleObjectType, _, "outboundDeprovisionAction") =>
                newValue == nameof(OutboundDeprovisionAction.Delete)
                    ? "Objects this rule deprovisions will be deleted in the Connected System rather than disconnected " +
                      "from JIM. Deletion happens in the target system and re-running synchronisation will not bring " +
                      "those objects back."
                    : "Objects this rule deprovisions will be disconnected from JIM and left in place in the Connected " +
                      "System, rather than deleted from it.",

            (ConfigurationSnapshotService.SyncRuleObjectType, _, "inboundOutOfScopeAction") =>
                newValue == nameof(InboundOutOfScopeAction.Disconnect)
                    ? "Every object that falls out of this rule's scope will be disconnected from its Metaverse Object. " +
                      "Attributes this rule contributed are withdrawn, and a Metaverse Object left with no remaining " +
                      "connectors may become eligible for deletion."
                    : "Objects that fall out of this rule's scope will stay joined to their Metaverse Objects instead of " +
                      "being disconnected.",

            // Deselecting an Object Type is the one selection that takes nothing out of scope. Deletion detection
            // walks the SELECTED object types, so a deselected one is never compared against the import at all: its
            // objects are not missing, they are simply never looked for. Saying otherwise would promise a cascade
            // that never arrives and leave the administrator believing the type is out of management.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, ObjectTypeNode, "selected") =>
                oldValue == True
                    ? "Deselecting this stops its objects being imported, and does nothing else. The Connected " +
                      "System Objects already imported from it stay exactly as they are: still joined to their " +
                      "Metaverse Objects, and still contributing the values they last imported, which will not be " +
                      "refreshed again while this stays deselected. Nothing is obsoleted and nothing is deprovisioned."
                    : "Selecting this brings its objects into scope for import on the next Import Run Profile.",

            // One key, two surfaces: Connected System Object Types and Partitions both snapshot their selection as
            // "selected". This arm is the Partition's, where deselecting genuinely does take objects out of an
            // import that still runs.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, _, "selected") =>
                oldValue == True
                    ? "Deselecting this stops its objects being imported. The Connected System Objects already imported " +
                      "from it become obsolete, and whatever they are joined to is deprovisioned on the next " +
                      "synchronisation."
                    : "Selecting this brings its objects into scope for import on the next Import Run Profile.",

            // A container's selection is its presence in the snapshot, so this key arrives as a whole container being
            // added or removed rather than as a flag moving. Same consequence as deselecting a partition, phrased for
            // the narrower thing being taken out of scope.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, _, "container") =>
                string.IsNullOrEmpty(newValue)
                    ? "Deselecting this container stops the objects beneath it being imported. The Connected System " +
                      "Objects already imported from it become obsolete, and whatever they are joined to is " +
                      "deprovisioned on the next synchronisation."
                    : "Selecting this container brings the objects beneath it into scope for import on the next " +
                      "Import Run Profile.",

            // Scope narrows or widens what a selected container imports. Narrowing takes objects out of scope exactly
            // as deselecting a container does; widening is the reverse, and takes nothing away.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, _, "scope") =>
                newValue == nameof(ConnectedSystemContainerScope.OneLevel)
                    ? "Importing only the objects held directly in this container stops the objects in the containers " +
                      "beneath it being imported, unless those containers are selected in their own right. The Connected " +
                      "System Objects already imported from them become obsolete, and whatever they are joined to is " +
                      "deprovisioned on the next synchronisation."
                    : "Importing the whole subtree brings the objects in the containers beneath this one into scope for " +
                      "import on the next Import Run Profile. Nothing already imported is taken out of scope.",

            // Carving a container out of a selection made above it. Reaches the same place as deselecting a
            // container, by a third route; clearing the exclusion is the reverse, and takes nothing away.
            (ConfigurationSnapshotService.ConnectedSystemObjectType, _, "excluded") =>
                newValue == True
                    ? "Excluding this container stops the objects in it being imported, and the same for every " +
                      "container beneath it, unless one of those is selected in its own right. The Connected System " +
                      "Objects already imported from them become obsolete, and whatever they are joined to is " +
                      "deprovisioned on the next synchronisation."
                    : "Including this container again brings its objects, and those beneath it, back into scope for " +
                      "import on the next Import Run Profile. Nothing already imported is taken out of scope.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, _, "deletionRule") =>
                "This takes effect immediately: Metaverse Objects of this type that already satisfy the new rule become " +
                "eligible for deletion on the next synchronisation or housekeeping pass, without any further change " +
                "being made to them.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, _, "deletionGracePeriod") =>
                "Metaverse Objects of this type awaiting deletion are held for this period. Shortening it brings forward " +
                "deletions that were pending, and any object already past the new period becomes eligible immediately.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, _, "deletionTriggerConnectedSystemIds") =>
                "This changes which Connected System disconnections trigger deletion. Metaverse Objects already " +
                "disconnected from a newly added trigger system become eligible for deletion immediately.",

            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, _, "deletionTriggerMode") =>
                newValue == nameof(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect)
                    ? "This takes effect immediately: any one selected source disconnecting is now enough to delete a " +
                      "Metaverse Object of this type. Objects already disconnected from a single selected source, but " +
                      "still connected to others, become eligible for deletion without any further change being made to them."
                    : "This takes effect immediately: a Metaverse Object of this type is now deleted only once every " +
                      "selected source has disconnected. Objects awaiting deletion that still hold a connection to any " +
                      "selected source stop being eligible.",

            // One entry within that list. An addition has no old value; a removal has no new one.
            (ConfigurationSnapshotService.MetaverseObjectTypeObjectType, _, "connectedSystemId") =>
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
        For(objectType, parentKey: null, nodeKey, null, null) != null ||
        For(objectType, parentKey: null, nodeKey, True, null) != null;
}
