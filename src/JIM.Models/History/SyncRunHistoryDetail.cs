using JIM.Models.Staging;

namespace JIM.Models.History
{
    /// <summary>
    /// Provides information on the execution of a synchronisation run event.
    /// </summary>
    public class SyncRunHistoryDetail
    {
        public Guid Id { get; set; }

        public RunHistoryItem? RunHistoryItem { get; set; }

        public ConnectedSystem? ConnectedSystem { get; set; }

        public string? ConnectedSystemName { get; set; }

        /// <summary>
        /// The run-profile that caused the synchronisation run.
        /// </summary>
        public ConnectedSystemRunProfile? RunProfile { get; set; }

        /// <summary>
        /// If the run profile has been deleted, the name of the run profile can be accessed here still.
        /// </summary>
        public string? RunProfileName { get; set; }

        /// <summary>
        /// If the run profile has been deleted, the type of sync run this was can be accessed here still.
        /// </summary>
        public ConnectedSystemRunType RunType { get; set; }

        public SyncRunHistoryDetailError? Error { get; set; }

        public string? ErrorMessage { get; set; }
        
        public string? ErrorStackTrace { get; set; }

        // results:
        // not sure that a full delta change log is needed, as mv changes will be logged, 
        // i.e. old value, new value when syncs are performed or workflows execute, user changes performed, etc.
        // what would be useful here is to capture two levels of stats, depending on system settings:
        // - result item with operation type (create/update/delete) and link to mv object
        // - result item with operation type (create/update/delete) and link to mv object and json snapshot of imported/exported object

        public List<SyncRunHistoryDetailItem> Items { get; set; } = new List<SyncRunHistoryDetailItem>();
    }
}
