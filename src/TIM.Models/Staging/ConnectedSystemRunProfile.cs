using TIM.Models.Transactional;

namespace TIM.Models.Staging
{
    public class ConnectedSystemRunProfile
    {
        public Guid Id { get; set; }
        public Guid ConnectedSystemId { get; set; }
        public SyncRunType RunType { get; set; }
    }
}
