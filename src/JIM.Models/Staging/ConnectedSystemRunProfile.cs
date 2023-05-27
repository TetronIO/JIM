using JIM.Models.Transactional;

namespace JIM.Models.Staging
{
    public class ConnectedSystemRunProfile
    {
        public int Id { get; set; }

        public string Name { get; set; }
        
        public ConnectedSystem ConnectedSystem { get; set; }
        
        /// <summary>
        /// If the connected system implements partitions, then a run profile needs to target a partition.
        /// </summary>
        public ConnectedSystemPartition? Partition { get; set; }

        public ConnectedSystemRunType RunType { get; set; }

        /// <summary>
        /// How many items to process in one go via the Connector.
        /// </summary>
        public int PageSize { get; set; }

        public override string ToString() => Name;
    }
}
