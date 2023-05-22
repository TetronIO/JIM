using JIM.Models.Staging;

namespace JIM.Models.History
{
    /// <summary>
    /// Provides information on the item of a synchronisation run, i.e. a pending export object, or import object.
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
        /// If settings allow during run execution, a JSON representation of the data imported, or exported can be accessed here for investigative purposes in the event of an error.
        /// </summary>
        public string? DataSnapshot { get; set; }

        public SynchronisationRunHistoryDetailItemError? Error { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorStackTrace { get; set; }
    }
}
