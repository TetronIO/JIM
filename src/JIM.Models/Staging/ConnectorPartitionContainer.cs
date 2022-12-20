namespace JIM.Models.Staging
{
    public class ConnectorPartitionContainer
    {
        /// <summary>
        /// The unique identifier for the container.
        /// For LDAP systems, this would be the Distinguished Name (DN).
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// The human-readable name for the container.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// An optional description for the container
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Some systems enable containers to be hidden by default, to reduce the risk of exposing internal objects to end-users.
        /// </summary>
        public bool Hidden { get; set; }

        /// <summary>
        /// If this is a top-level container, then it may reside in a connector partition, though this isn't required if the connector doesn't implement partitions.
        /// </summary>
        public ConnectorPartition? ConnectorPartition { get; set; }

        /// <summary>
        /// Containers can container children containers.
        /// Enables a hierarchy of containers to be built out, i.e a directory DIT.
        /// </summary>
        public List<ConnectorPartitionContainer> ChildContainers { get; }

        public ConnectorPartitionContainer(string id, string name, bool hidden = false)
        {
            Id = id;
            Name = name;
            Hidden = hidden;
            ChildContainers = new List<ConnectorPartitionContainer>();
        }

        public ConnectorPartitionContainer(string id, string name, string description, bool hidden = false)
        {
            Id = id;
            Name = name;
            Description = description;
            Hidden = hidden;
            ChildContainers = new List<ConnectorPartitionContainer>();
        }
    }
}
