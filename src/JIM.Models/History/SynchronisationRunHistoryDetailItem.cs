using JIM.Models.Staging;

namespace JIM.Models.History
{
    /// <summary>
    /// Represents the result of an import or export operation, i.e. result, errors, snapshot, etc. 
    /// </summary>
    public class SynchronisationRunHistoryDetailItem
    {
        public Guid Id { get; set; }

        /// <summary>
        /// What connected system object does this history detail item relate to?
        /// The history item may outlive the object in question, so a reference isn't always required, 
        /// or there may have been a problem during priovisioning and a connected system object may not have been created yet.
        /// </summary>
        public ConnectedSystemObject? ConnectedSystemObject { get; set; }

        /// <summary>
        /// The parent for this detail item.
        /// </summary>
        public SynchronisationRunHistoryDetail SynchronisationRunHistoryDetail { get; set; }

        /// <summary>
        /// If settings allow during run execution, a JSON representation of the data imported, or exported can be accessed here for investigative purposes.
        /// </summary>
        public string? DataSnapshot { get; set; }

        public string? ErrorMessage { get; set; }

        public string? ErrorCode { get; set; } 

        public string? ErrorStackTrace { get; set; }
    }
}
