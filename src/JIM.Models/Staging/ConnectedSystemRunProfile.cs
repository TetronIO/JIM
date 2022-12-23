using JIM.Models.Transactional;

namespace JIM.Models.Staging
{
    public class ConnectedSystemRunProfile
    {
        public int Id { get; set; }
        
        public ConnectedSystem ConnectedSystem { get; set; }
        
        /// <summary>
        /// If the connected system implements partitions, then a run profile needs to target a partition.
        /// </summary>
        public ConnectedSystemPartition? Partition { get; set; }

        public SyncRunType RunType { get; set; }
    }
}
