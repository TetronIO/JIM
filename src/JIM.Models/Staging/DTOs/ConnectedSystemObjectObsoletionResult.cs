// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Sync;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// The staged output of processing one obsolete Connected System Object through the obsoletion core
/// (#809 Phase 1 extraction): everything the operation decided, as data, for the caller to fold into its
/// own batch machinery. The run-time sync processor maps these into its page-flush accumulators; a future
/// read-only preview adapter (#134/#827) consumes the same data without applying any of it.
/// <para>
/// The core mutates the entities it is handed exactly as the in-processor implementation did (the join is
/// broken on the Connected System Object, recalled values are applied to the Metaverse Object); this
/// result carries the bookkeeping those mutations must be accompanied by.
/// </para>
/// </summary>
public class ConnectedSystemObjectObsoletionResult
{
    /// <summary>
    /// The Activity Run Profile Execution Items recording the operation, for the caller to add to its
    /// Activity. One per obsoleted object (the Disconnected/Deleted RPEI); empty for a quiet delete or a
    /// non-obsolete object.
    /// </summary>
    public List<ActivityRunProfileExecutionItem> ExecutionItems { get; } = [];

    /// <summary>
    /// Pre-disconnected Connected System Objects to delete quietly (no RPEI; the disconnection was
    /// already recorded when the Metaverse Object was deleted).
    /// </summary>
    public List<ConnectedSystemObject> QuietCsoDeletions { get; } = [];

    /// <summary>
    /// Connected System Objects to delete, each with the execution item that records the deletion.
    /// </summary>
    public List<(ConnectedSystemObject Cso, ActivityRunProfileExecutionItem ExecutionItem)> CsoDeletions { get; } = [];

    /// <summary>
    /// The Metaverse Object attribute change to record for change tracking (the recalled and any
    /// re-elected values), captured BEFORE the pending changes were applied. Null when no attributes
    /// changed (recall disabled, skipped, or nothing contributed).
    /// </summary>
    public (MetaverseObject Mvo, List<MetaverseObjectAttributeValue> Additions, List<MetaverseObjectAttributeValue> Removals,
        ObjectChangeType ChangeType, ActivityRunProfileExecutionItem ExecutionItem)? MvoAttributeChange { get; set; }

    /// <summary>
    /// The Metaverse Object to queue for persistence because its attribute values changed. Null when
    /// nothing changed.
    /// </summary>
    public MetaverseObject? MvoToUpdate { get; set; }

    /// <summary>
    /// The export evaluation to queue so target systems receive Pending Exports for the recalled (and any
    /// re-elected) attribute values. Null when no attributes changed.
    /// </summary>
    public (MetaverseObject Mvo, List<MetaverseObjectAttributeValue> ChangedAttributes,
        HashSet<MetaverseObjectAttributeValue> RemovedAttributes)? ExportEvaluation { get; set; }

    /// <summary>
    /// The Metaverse Object whose join to the processed Connected System Object was broken, so the caller
    /// can account for the in-memory disconnection before it is flushed. Null when no join was broken.
    /// </summary>
    public Guid? DisconnectedMetaverseObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object deletion-rule verdict for the disconnection, when one was evaluated: the fate
    /// (not deleted, scheduled, deleted immediately), the human-readable reason and any grace period.
    /// Null when the object was not joined or the join was preserved.
    /// </summary>
    public MvoDeletionDecision? MvoDeletionDecision { get; set; }

    /// <summary>
    /// The decision-time deletion policy snapshot (#119) recorded on the execution item, when the
    /// deletion-rule evaluation produced one.
    /// </summary>
    public string? MvoDeletionPolicySnapshotJson { get; set; }

    /// <summary>
    /// How many attributes the recall genuinely cleared: no surviving contributor was re-elected and no
    /// other value remains (#91's NoContributor observability count).
    /// </summary>
    public int RecallClearedAttributeCount { get; set; }

    /// <summary>
    /// How many of the disconnecting system's values were preserved as last known state because no remaining
    /// joined system carries an enabled import Synchronisation Rule for the object's type (#1570's
    /// ValuesPreserved observability count). Zero when the freeze was for a pending deletion, which explains
    /// itself via the deletion outcome instead.
    /// </summary>
    public int PreservedNoSourceAttributeCount { get; set; }
}
