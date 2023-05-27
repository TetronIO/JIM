using JIM.Models.Staging;

namespace JIM.Models.History
{
    /// <summary>
    /// Provides information on the item of a synchronisation run, i.e. a pending export object, or import object.
    /// </summary>
    public class SyncRunHistoryDetailItem
    {
        public Guid Id { get; set; }

        /// <summary>
        /// The parent for this detail item.
        /// For EF navigation purposes.
        /// </summary>
        public SyncRunHistoryDetail SyncRunHistoryDetail { get; set; }

        /// <summary>
        /// What change(s), if any were made to the connected system object in question?
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

        public SyncRunHistoryDetailItemError? Error { get; set; }

        public string? ErrorMessage { get; set; }
        
        public string? ErrorStackTrace { get; set; }
    }
}
