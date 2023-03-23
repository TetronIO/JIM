using System.ComponentModel.DataAnnotations.Schema;

namespace JIM.Models.Staging
{
    public class ConnectedSystemContainer
    {
        public int Id { get; set; }

        /// <summary>
        /// If this is a top-level container, then it may reside in a Connected System Partition, though this isn't required if the connector doesn't implement partitions.
        /// </summary>
        public ConnectedSystemPartition? Partition { get; set; }

        /// <summary>
        /// If this is a top-level container and the connector doesn't implement partitions then it'll be a child of a Connected System.
        /// If partitions are implemented, then a Partition reference is required on top-level containers.
        /// </summary>
        public ConnectedSystem? ConnectedSystem { get; set; }

        /// <summary>
        /// The unique identifier for this container in the connected system.
        /// For LDAP systems, this would be the DN (Distinguished Name).
        /// </summary>
        public string ExternalId { get; set; }

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
        /// Indicates whether or not the container has been selected to be managed, i.e. whether or not objects are
        /// imported from here or not.
        /// </summary>
        public bool Selected { get; set; }

        /// <summary>
        /// Containers can container children containers.
        /// Enables a hierarchy of containers to be built out, i.e a directory DIT.
        /// </summary>
        public HashSet<ConnectedSystemContainer> ChildContainers { get; }

        #region For MudBlazor TreeView
        public ConnectedSystemContainer? ParentContainer { get; set; }

        [NotMapped]
        public bool Expanded { get; set; }
        #endregion

        public ConnectedSystemContainer()
        {
            ChildContainers = new HashSet<ConnectedSystemContainer>();
        }

        public void AddChildContainer(ConnectedSystemContainer container)
        {
            container.ParentContainer = this;
            ChildContainers.Add(container);
        }

        public bool HasPartialChildContainersSelection()
        {
            var childrenSelectedCount = GetChildrenSelectedCount(this);
            return ChildContainers.Count > 0 && childrenSelectedCount > 0 && childrenSelectedCount < GetChildrenCount(this);
        }

        private int GetChildrenSelectedCount(ConnectedSystemContainer connectedSystemContainer)
        {
            var count = 0;
            foreach (var childContainer in connectedSystemContainer.ChildContainers)
            {
                if (childContainer.Selected)
                    count++;

                count += GetChildrenSelectedCount(childContainer);
            }

            return count;
        }

        private int GetChildrenCount(ConnectedSystemContainer connectedSystemContainer)
        {
            var count = 0;
            foreach (var childContainer in connectedSystemContainer.ChildContainers)
            {
                count++;
                count += GetChildrenCount(childContainer);
            }

            return count;
        }
    }
}
