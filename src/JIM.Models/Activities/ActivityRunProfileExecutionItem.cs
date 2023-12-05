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

    /// <summary>
    /// What happened with the ConnectedSystemObject? Was it created/updated/deleted?
    /// </summary>
    public ObjectChangeType ObjectChangeType { get; set; }

    /// <summary>
    /// What CSO does this sync run history detail item relate to?
    /// Note: If the change was a delete, then there will be no CSO to reference.
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// What change(s), if any were made to the connected system object in question?
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
}
