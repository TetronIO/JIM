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

        public ConnectedSystem ConnectedSystem { get; set; }

        /// <summary>
        /// The unique identifier for this partition in the connected system.
        /// For LDAP systems, this would be the DN (Distinguished Name).
        /// </summary>
        public string ExternalId { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// A system that implements partitions may have the concept of a hierarchy of containers within the partition, i.e. as with an LDAP system.
        /// It's not required that a partition have containers though. If so, leave the Containers list empty.
        /// </summary>
        public List<ConnectedSystemContainer>? Containers { get; set; }
    }
}
