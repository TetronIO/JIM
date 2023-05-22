using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    /// <summary>
    /// An instance of an object representing an object being processed during a synchronisation run.
    /// Enables additional metadata to be associated with the synchronisation of a connector space object.
    /// </summary>
    public class SyncRunObject
    {
        public Guid Id { get; set; }
        /// <summary>
        /// The parent synchronisation run this object relates to.
        /// </summary>
        public SyncRun SynchronisationRun { get; set; }
        public DateTime Created { get; set; }
        /// <summary>
        /// The connected system object this sync run relates to.
        /// </summary>
        public ConnectedSystemObject? ConnectedSystemObject { get; set; }
        /// <summary>
        /// If the sync run is if type synchronisation and the change relates to an export that has not been re-imported, then a Pending Export will be associated
        /// to help with linking to the issue.
        /// </summary>
        public PendingExport? PendingExport { get; set; }
        public SyncRunItemResult Result { get; set; }
        public string? ConnectedSystemErrorMessage { get; set; }
        public string? ConnectedSystemStackTrace { get; set; }
    }
}
