using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// Tracks changed made to CSOs and MVOs as a result of a Sync Run Profile being executed.
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
    /// If this was an import operation, what changes, if any were made to the Connected System Object in question?
    /// This needs populating for update and delete scenarios.
    /// </summary>
    public ConnectedSystemObjectChange? ConnectedSystemObjectChange { get; set; }

    /// <summary>
    /// If this is a full/delta sync run profile execution, what changes, if any were made to a joined Metaverse Object?
    /// This needs populating for project, join, update and delete scenarios.
    /// </summary>
    public MetaverseObjectChange? MetaverseObjectChange { get; set; }

    // errors:
    // two-tiers of error logging, depending on system settings:
    // - individual error items with detailed error info
    // - individual error items with detailed error info and json snapshot of exported/imported object

    /// <summary>
    /// If settings allow during run profile execution, a JSON representation of the data imported, or exported can be
    /// accessed ere for investigative purposes in the event of an error.
    /// </summary>
    public string? DataSnapshot { get; set; }

    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; } = ActivityRunProfileExecutionItemErrorType.NotSet;

    public string? ErrorMessage { get; set; }

    public string? ErrorStackTrace { get; set; }

    public ConnectedSystemObjectAttributeValue? GetExternalIdAttributeValue()
    {
        // try and get an external id for the target object
        // one should exist for updates and deletes, but isn't guaranteed for creates if the connected system is
        // responsible for generating it and a confirming import hasn't been completed.
        return ConnectedSystemObject != null ?
            ConnectedSystemObject.ExternalIdAttributeValue :
            ConnectedSystemObjectChange?.DeletedObjectExternalIdAttributeValue;
    }

    public int? GetConnectedSystemId()
    {
        return ConnectedSystemObject?.ConnectedSystemId ?? ConnectedSystemObjectChange?.ConnectedSystemId;
    }
}
