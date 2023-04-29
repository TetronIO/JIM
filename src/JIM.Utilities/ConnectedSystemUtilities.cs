using JIM.Models.Staging;

namespace JIM.Utilities
{
    public static class ConnectedSystemUtilities
    {
        /// <summary>
        /// Generates a list of all selected containers in a partition container hierarchy. Uses recursion to walk the hierarchy.
        /// </summary>
        public static List<ConnectedSystemContainer> GetAllSelectedContainers(ConnectedSystemPartition connectedSystemPartition)
        {
            if (connectedSystemPartition == null)
                throw new ArgumentNullException(nameof(connectedSystemPartition));

            if (connectedSystemPartition.Containers == null)
                throw new ArgumentException("ConnectedSystemContainer.Containers is null", nameof(connectedSystemPartition.Containers));

            var selectedContainers = new List<ConnectedSystemContainer>();
            foreach (var rootContainer in connectedSystemPartition.Containers)
            {
                if (rootContainer.Selected)
                    selectedContainers.Add(rootContainer);

                SearchForSelectedChildContainers(rootContainer, selectedContainers);
            }

            return selectedContainers;
        }

        private static void SearchForSelectedChildContainers(ConnectedSystemContainer container, List<ConnectedSystemContainer> selectedContainers)
        {
            if (container == null || container.ChildContainers == null || container.ChildContainers.Count == 0)
                return;

            foreach (var childContainer in container.ChildContainers)
            {
                if (childContainer.Selected)
                    selectedContainers.Add(childContainer);

                SearchForSelectedChildContainers(childContainer, selectedContainers);
            }
        }
    }
}
