// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// Tracks changes made to CSOs and MVOs as a result of a Sync Run Profile being executed.
/// </summary>
public class ActivityRunProfileExecutionItem
{
    /// <summary>
    /// Unique identifier for this Run Profile execution item.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The parent activity for this Run Profile execution item. For EF navigation purposes.
    /// </summary>
    public Activity Activity { get; set; } = null!;

    /// <summary>
    /// Foreign key for the parent <see cref="Activity"/> navigation property.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// What happened with the ConnectedSystemObject? Was it created/updated/deleted?
    /// </summary>
    public ObjectChangeType ObjectChangeType { get; set; }

    /// <summary>
    /// If ObjectChangeType is NoChange, indicates why the no-net-change was detected.
    /// </summary>
    public NoChangeReason? NoChangeReason { get; set; }

    /// <summary>
    /// If this was an import operation, what CSO does this sync operation item relate to?
    /// Note: If the change was a delete, then there will be no CSO to reference.
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// Foreign key for the ConnectedSystemObject navigation property.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Pending Export associated with this execution item, when ObjectChangeType is PendingExport.
    /// Enables the detail page to load the Pending Export for Create-type exports that have no CSO yet.
    /// </summary>
    public Guid? PendingExportId { get; set; }

    /// <summary>
    /// Snapshot of the external ID value at the time the RPEI was created.
    /// This preserves the external ID even if the CSO is later deleted (e.g., due to obsolescence),
    /// which would otherwise null out the ConnectedSystemObjectId via FK cascade.
    /// </summary>
    public string? ExternalIdSnapshot { get; set; }

    /// <summary>
    /// Snapshot of the CSO display name at the time the RPEI was created.
    /// Provides a fallback for display purposes when the CSO has been deleted.
    /// </summary>
    public string? DisplayNameSnapshot { get; set; }

    /// <summary>
    /// Snapshot of the CSO object type name at the time the RPEI was created.
    /// Provides a fallback for display purposes when the CSO has been deleted.
    /// </summary>
    public string? ObjectTypeSnapshot { get; set; }

    /// <summary>
    /// If this is for an import operation, what changes, if any were made to the Connected System Object in question?
    /// This needs populating for update and delete scenarios.
    /// </summary>
    public ConnectedSystemObjectChange? ConnectedSystemObjectChange { get; set; }

    /// <summary>
    /// If this is for a full, or delta sync Run Profile execution, what changes, if any were made to a joined Metaverse Object?
    /// This needs populating for project, join, update and delete scenarios.
    /// </summary>
    public MetaverseObjectChange? MetaverseObjectChange { get; set; }

    // errors:
    // two-tiers of error logging, depending on system settings:
    // - individual error items with detailed error info
    // - individual error items with detailed error info and json snapshot of exported/imported object (not yet implemented, but planned for future)

    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; } = ActivityRunProfileExecutionItemErrorType.NotSet;

    /// <summary>
    /// Human-readable error message describing what went wrong during processing.
    /// Null when no error occurred.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Stack trace captured from the exception, for diagnostic purposes.
    /// Null when no error occurred or when the error type does not originate from an exception.
    /// </summary>
    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// When the primary ObjectChangeType is Joined, Projected, Disconnected, or DisconnectedOutOfScope,
    /// this field records how many MVO attributes were added or removed as part of the same operation.
    /// This prevents Attribute Flow from being "absorbed" into the primary change type, enabling accurate
    /// Attribute Flow counting alongside joins, projections, and disconnections.
    /// Null when no attribute changes occurred or when the primary type is already AttributeFlow.
    /// </summary>
    public int? AttributeFlowCount { get; set; }

    /// <summary>
    /// Denormalised summary of sync outcome types for fast list-view rendering.
    /// Comma-separated outcome types with counts, e.g., "Projected:1,AttributeFlow:12,PendingExportCreated:2".
    /// Populated during outcome tree construction — no separate maintenance path.
    /// Null when no outcome tracking is configured or for legacy RPEIs.
    /// </summary>
    public string? OutcomeSummary { get; set; }

    /// <summary>
    /// The decision-time deletion policy snapshot (a serialised <c>MvoDeletionPolicySnapshot</c>), written
    /// whenever a deletion rule evaluation records an outcome: scheduled, deleted, or evaluated-but-not-triggered.
    /// Captured at decision time so the record stays accurate after the object type's deletion configuration
    /// changes; null for RPEIs with no deletion evaluation (#119).
    /// </summary>
    public string? DeletionPolicySnapshotJson { get; set; }

    /// <summary>
    /// The structured causal graph of sync outcomes for this RPEI.
    /// Each root outcome can have nested children forming a tree that tells the complete
    /// story of what happened when this CSO was processed.
    /// </summary>
    public List<ActivityRunProfileExecutionItemSyncOutcome> SyncOutcomes { get; set; } = [];

    /// <summary>
    /// Causal edges recorded against this item, naming what caused the changes it describes (#1223).
    /// </summary>
    /// <remarks>
    /// This is a hand-off buffer to the flush, not a loaded collection. A cascade seam adds an edge here at the
    /// moment it creates the effect, and the flush writes the edges in the same transaction as the item, then
    /// empties the list. Two consequences follow, both deliberate. The seams stay declarative: they never need
    /// to know which flush path a batch will take or where the transaction boundary is. And an RPEI object that
    /// outlives its flush (confirming imports revisit them) carries no already-written edges, so a later flush
    /// cannot duplicate them.
    ///
    /// Deliberately unmapped, rather than the inverse of the edge's own foreign key. EF must never load, track
    /// or cascade through this list: it is a write-side queue whose contents are gone the moment they are
    /// persisted, and a mapped collection navigation would additionally be walked by <c>DbSet.Add</c>, giving
    /// the edges a second, untransacted insert path. Read paths query the edge table directly, since the whole
    /// point of an edge is to reach records a single item cannot.
    /// </remarks>
    [NotMapped]
    public List<CausalEdge> CausalEdges { get; set; } = [];

    public ConnectedSystemObjectAttributeValue? GetExternalIdAttributeValue()
    {
        // try and get an external id for the target object
        // one should exist for updates and deletes, but isn't guaranteed for creates if the Connected System is
        // responsible for generating it and a confirming import hasn't been completed.
        return ConnectedSystemObject != null ?
            ConnectedSystemObject.ExternalIdAttributeValue :
            ConnectedSystemObjectChange?.DeletedObjectExternalIdAttributeValue;
    }

    /// <summary>
    /// Gets the external ID as a string, using the snapshot as fallback if the CSO has been deleted.
    /// This ensures historical RPEIs remain useful even after CSO deletion.
    /// </summary>
    public string? GetExternalIdString()
    {
        // First try to get from the live CSO
        var attrValue = GetExternalIdAttributeValue();
        if (attrValue?.StringValue != null)
            return attrValue.StringValue;

        // Fall back to snapshot (preserved when CSO was deleted)
        return ExternalIdSnapshot;
    }

    /// <summary>
    /// Which Connected System this item's record belongs to, surviving the record's own deletion
    /// where that can be answered honestly.
    /// </summary>
    /// <remarks>
    /// The record itself is the authority while it exists. Once it is deleted, the item-level change
    /// snapshot is the only other row carrying a system id, and it is only the record's own on
    /// record-side items (imports, export executions, drift corrections and Pending Export
    /// surfacing). A synchronisation-side item's change row describes a record the run created
    /// elsewhere: the provisioned stub in a target system. Trusting it there labelled a projection
    /// item's source record with its provisioning target's system, and every consumer links by this
    /// id, so the honest degraded answer is no system rather than the wrong one (#1495).
    /// </remarks>
    public int? GetConnectedSystemId()
    {
        if (ConnectedSystemObject?.ConnectedSystemId is { } liveSystemId)
            return liveSystemId;

        return ObjectChangeType switch
        {
            Enums.ObjectChangeType.Added
                or Enums.ObjectChangeType.Updated
                or Enums.ObjectChangeType.Deleted
                or Enums.ObjectChangeType.Exported
                or Enums.ObjectChangeType.Deprovisioned
                or Enums.ObjectChangeType.DriftCorrection
                or Enums.ObjectChangeType.NoChange
                or Enums.ObjectChangeType.PendingExport
                or Enums.ObjectChangeType.PendingExportConfirmed
                => ConnectedSystemObjectChange?.ConnectedSystemId,
            _ => null
        };
    }

    /// <summary>
    /// Populates the ExternalIdSnapshot, DisplayNameSnapshot, and ObjectTypeSnapshot fields
    /// from the given CSO. Call this when creating or linking an RPEI to a CSO so the display
    /// data is preserved even if the CSO is later deleted.
    /// </summary>
    public void SnapshotCsoDisplayFields(ConnectedSystemObject cso)
    {
        ExternalIdSnapshot ??= cso.ExternalIdAttributeValue?.ToStringNoName();
        // Name, not NameOrId: the external id has its own snapshot field directly above, and a name
        // field echoing it would render as "<id> (<id>)" wherever the two are shown together.
        DisplayNameSnapshot ??= cso.Name;
        ObjectTypeSnapshot ??= cso.Type?.Name;
    }
}
