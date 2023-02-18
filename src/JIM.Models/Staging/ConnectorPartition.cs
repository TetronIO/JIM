namespace JIM.Models.Staging
{
    /// <summary>
    /// Represents a partition within a Connected System, i.e. an LDAP partition.
    /// </summary>
    public class ConnectorPartition
    {
        /// <summary>
        /// The unique identifier for this partition in the connected system.
        /// For example, with LDAP systems this would be the DN (Distinguished Name).
        /// </summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// Should the partition be hidden by default? For example, Active Directory Configuration and Schema partitions shouldn't be visible ordinarily.
        /// </summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// A system that implements partitions, may have the concept of a hierarchy of containers within the partition, i.e. as with an LDAP system.
        /// It's not required that a partition have containers though. If so, leave the Containers list empty.
        /// </summary>
        public List<ConnectorContainer> Containers { get; set; }

        public ConnectorPartition()
        {
            Containers = new List<ConnectorContainer>();
        }
    }
}
