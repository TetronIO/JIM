using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    /// <summary>
    /// Represents an instance of a synchronisation run, i.e. an import/export/synchronisation.
    /// </summary>
    public class SyncRun
    {
        public Guid Id { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public DateTime Created { get; set; }
        public SyncRunType RunType { get; set; }
        public List<SyncRunObject> Objects { get; set; }
        public string? ConnectedSystemErrorMessage { get; set; }
        public string? ConnectedSystemStackTrace { get; set; }

        public SyncRun()
        {
            Objects = new List<SyncRunObject>();
        }
    }
}
