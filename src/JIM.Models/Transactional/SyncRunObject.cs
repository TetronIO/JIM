using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    public class SyncRunObject
    {
        public Guid Id { get; set; }
        public SyncRun SynchronisationRun { get; set; }
        public DateTime Created { get; set; }
        public ConnectedSystemObject? ConnectedSystemObject { get; set; }
        public SyncRunItemResult Result { get; set; }
        public string? ConnectedSystemErrorMessage { get; set; }
        public string? ConnectedSystemStackTrace { get; set; }

        public SyncRunObject(SyncRun synchronisationRun)
        {
            SynchronisationRun = synchronisationRun;
        }
    }
}
