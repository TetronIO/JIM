using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

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
    /// What CSO does this sync operation item relate to?
    /// Note: If the change was a delete, then there will be no CSO to reference.
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// What changes, if any were made to the connected system object in question?
    /// This needs populating for update and delete scenarios.
    /// </summary>
    public ConnectedSystemObjectChange? ConnectedSystemObjectChange { get; set; }

    // errors:
    // two-tiers of error logging, depending on system settings:
    // - individual error items with detailed error info
    // - individual error items with detailed error info and json snapshot of exported/imported object

    /// <summary>
    /// If settings allow during run execution, a JSON representation of the data imported, or exported can be accessed here for investigative purposes in the event of an error.
    /// </summary>
    public string? DataSnapshot { get; set; }

    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }

    public string? ErrorMessage { get; set; }
    
    public string? ErrorStackTrace { get; set; }

    public ConnectedSystemObjectAttributeValue? GetExternalIdAttributeValue()
    {
        // try and get an external id for the target object
        // one should exist for updates and deletes, but isn't guaranteed for creates if the connected system is responsible for generating it and a confirming import hasn't been completed.
        if (ConnectedSystemObject != null)
            return ConnectedSystemObject.ExternalIdAttributeValue;
        else if (ConnectedSystemObjectChange != null)
            return ConnectedSystemObjectChange.DeletedObjectExternalIdAttributeValue; 
        else return null;
    }

    public int? GetConnectedSystemId()
    {
        if (ConnectedSystemObject != null)
            return ConnectedSystemObject.ConnectedSystemId;
        else if (ConnectedSystemObjectChange != null)
            return ConnectedSystemObjectChange.ConnectedSystemId;
        else
            return null;
    }
}
