using JIM.Models.Staging;

namespace JIM.Models.History
{
    public class SynchronisationRunHistoryDetail
    {
        public Guid Id { get; set; }

        public RunHistoryItem? RunHistoryItem { get; set; }

        public ConnectedSystem? ConnectedSystem { get; set; }

        public string? ConnectedSystemName { get; set; }

        public ConnectedSystemRunProfile? RunProfile { get; set; }

        public string? RunProfileName { get; set; }

        // results:
        // not sure that a full delta change log is needed, as mv changes will be logged, i.e. old value, new value when syncs are performed or workflows execute, user changes performed, etc.
        // what would be useful here is to capture two levels of stats, depending on system settings:
        // - result item with operation type (create/update/delete) and link to mv object
        // - result item with operation type (create/update/delete) and link to mv object and json snapshot of imported/exported object

        // errors:
        // two-tiers of error logging, depending on system settings:
        // - individual error items with detailed error info
        // - individual error items with detailed error info and json snapshot of exported/imported object

        public List<SynchronisationRunHistoryDetailItem> Items { get; set; } = new List<SynchronisationRunHistoryDetailItem>();
    }
}
