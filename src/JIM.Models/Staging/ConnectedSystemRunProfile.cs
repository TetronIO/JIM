using JIM.Models.Activities;

namespace JIM.Models.Staging
{
    public class ConnectedSystemRunProfile
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        /// <summary>
        /// Unique identifier for the parent object.
        /// </summary>
        public int ConnectedSystemId { get; set; }
        
        /// <summary>
        /// If the connected system implements partitions, then a run profile needs to target a partition.
        /// </summary>
        public ConnectedSystemPartition? Partition { get; set; }

        public ConnectedSystemRunType RunType { get; set; }

        /// <summary>
        /// How many items to process in one go via the Connector.
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Back-link to depedent activity objects. 
        /// Optional relationship.
        /// Used by EntityFramework.
        /// </summary>
        public List<Activity>? Activities { get; set; }

        public override string ToString() => Name;
    }
}
