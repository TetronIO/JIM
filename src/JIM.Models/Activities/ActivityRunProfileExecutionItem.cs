using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// Tracks changes made to CSOs and MVOs as a result of a Sync Run Profile being executed.
/// </summary>
public class ActivityRunProfileExecutionItem
{
    public Guid Id { get; set; }

    /// <summary>
    /// The parent for this run profile execution item. For EF navigation purposes.
    /// </summary>
    public Activity Activity { get; set; } = null!;
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
    /// Snapshot of the external ID value at the time the RPEI was created.
    /// This preserves the external ID even if the CSO is later deleted (e.g., due to obsolescence),
    /// which would otherwise null out the ConnectedSystemObjectId via FK cascade.
    /// </summary>
    public string? ExternalIdSnapshot { get; set; }

    /// <summary>
    /// If this is for an import operation, what changes, if any were made to the Connected System Object in question?
    /// This needs populating for update and delete scenarios.
    /// </summary>
    public ConnectedSystemObjectChange? ConnectedSystemObjectChange { get; set; }

    /// <summary>
    /// If this is for a full, or delta sync run profile execution, what changes, if any were made to a joined Metaverse Object?
    /// This needs populating for project, join, update and delete scenarios.
    /// </summary>
    public MetaverseObjectChange? MetaverseObjectChange { get; set; }

    // errors:
    // two-tiers of error logging, depending on system settings:
    // - individual error items with detailed error info
    // - individual error items with detailed error info and json snapshot of exported/imported object

    /// <summary>
    /// If settings allow during run profile execution, a JSON representation of the data imported, or exported can be
    /// accessed here for investigative purposes in the event of an error.
    /// </summary>
    public string? DataSnapshot { get; set; }

    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; } = ActivityRunProfileExecutionItemErrorType.NotSet;

    public string? ErrorMessage { get; set; }

    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// When the primary ObjectChangeType is Joined, Projected, Disconnected, or DisconnectedOutOfScope,
    /// this field records how many MVO attributes were added or removed as part of the same operation.
    /// This prevents attribute flow from being "absorbed" into the primary change type, enabling accurate
    /// attribute flow counting alongside joins, projections, and disconnections.
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
    /// The structured causal graph of sync outcomes for this RPEI.
    /// Each root outcome can have nested children forming a tree that tells the complete
    /// story of what happened when this CSO was processed.
    /// </summary>
    public List<ActivityRunProfileExecutionItemSyncOutcome> SyncOutcomes { get; set; } = [];

    public ConnectedSystemObjectAttributeValue? GetExternalIdAttributeValue()
    {
        // try and get an external id for the target object
        // one should exist for updates and deletes, but isn't guaranteed for creates if the connected system is
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

    public int? GetConnectedSystemId()
    {
        return ConnectedSystemObject?.ConnectedSystemId ?? ConnectedSystemObjectChange?.ConnectedSystemId;
    }
}
