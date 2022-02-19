using TIM.Models.Staging;

namespace TIM.Models.Transactional
{
    public class SyncRun
    {
        public Guid Id { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public DateTime Created { get; set; }
        public SyncRunType RunType { get; set; }
        public List<SyncRunObject> Objects { get; set; }
        public string? ConnectedSystemErrorMessage { get; set; }
        public string? ConnectedSystemStackTrace { get; set; }

        public SyncRun(ConnectedSystem connectedSystem)
        {
            ConnectedSystem = connectedSystem;
            Objects = new List<SyncRunObject>();
        }
    }
}
