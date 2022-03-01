using JIM.Models.Staging;

namespace JIM.Models.Transactional
{
    public class SyncRun
    {
        public int Id { get; set; }
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
