using JIM.Models.Transactional;

namespace JIM.Models.Staging
{
    public class ConnectedSystemRunProfile
    {
        public int Id { get; set; }
        public ConnectedSystem ConnectedSystem { get; set; }
        public SyncRunType RunType { get; set; }
    }
}
