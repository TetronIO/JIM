namespace JIM.Models.Staging
{
    /// <summary>
    /// Represents a partition within a Connected System, i.e. an LDAP partition.
    /// </summary>
    public class ConnectedSystemPartition
    {
        /// <summary>
        /// The JIM-assigned unique identifier for this partition.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The ConnectedSystem this Partition relates to. For EF navigation.
        /// </summary>
        public ConnectedSystem ConnectedSystem { get; set; }

        /// <summary>
        /// The unique identifier for this partition in the connected system.
        /// For example, with LDAP systems this would be the DN (Distinguished Name).
        /// </summary>
        public string ExternalId { get; set; }

        /// <summary>
        /// the human-readable name for this partition.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Indicates whether or not this partition has been selected to be managed, i.e. whether or not objects will be imported from it.
        /// </summary>
        public bool Selected { get; set; }

        /// <summary>
        /// A system that implements partitions may have the concept of a hierarchy of containers within the partition, i.e. as with an LDAP system.
        /// It's not required that a partition have containers though. If so, leave the Containers list empty.
        /// </summary>
        public HashSet<ConnectedSystemContainer>? Containers { get; set; }
    }
}
