using JIM.Models.Staging;
namespace JIM.Utilities;

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

    /// <summary>
    /// Generates a list of selected containers that are not already covered by an ancestor container.
    /// For subtree searches, if a parent container is selected, its selected children are redundant
    /// and would cause duplicate results. This method returns only the "top-level" selected containers.
    /// </summary>
    /// <remarks>
    /// Example: If OU=Corp is selected AND OU=Users,OU=Corp is selected:
    /// - GetAllSelectedContainers returns both
    /// - GetTopLevelSelectedContainers returns only OU=Corp (since OU=Users is covered by the subtree search)
    /// </remarks>
    public static List<ConnectedSystemContainer> GetTopLevelSelectedContainers(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        if (connectedSystemPartition.Containers == null)
            throw new ArgumentException("ConnectedSystemContainer.Containers is null", nameof(connectedSystemPartition.Containers));

        var selectedContainers = new List<ConnectedSystemContainer>();
        foreach (var rootContainer in connectedSystemPartition.Containers)
        {
            if (rootContainer.Selected)
            {
                // This root container is selected - add it and skip all its descendants
                // (they're covered by the subtree search)
                selectedContainers.Add(rootContainer);
            }
            else
            {
                // This root container is not selected - search its children
                SearchForTopLevelSelectedChildContainers(rootContainer, selectedContainers);
            }
        }

        return selectedContainers;
    }

    private static void SearchForSelectedChildContainers(ConnectedSystemContainer container, ICollection<ConnectedSystemContainer> selectedContainers)
    {
        if (container.ChildContainers.Count == 0)
            return;

        foreach (var childContainer in container.ChildContainers)
        {
            if (childContainer.Selected)
                selectedContainers.Add(childContainer);

            SearchForSelectedChildContainers(childContainer, selectedContainers);
        }
    }

    private static void SearchForTopLevelSelectedChildContainers(ConnectedSystemContainer container, ICollection<ConnectedSystemContainer> selectedContainers)
    {
        if (container.ChildContainers.Count == 0)
            return;

        foreach (var childContainer in container.ChildContainers)
        {
            if (childContainer.Selected)
            {
                // This child is selected - add it and skip its descendants
                // (they're covered by this container's subtree search)
                selectedContainers.Add(childContainer);
            }
            else
            {
                // This child is not selected - continue searching its children
                SearchForTopLevelSelectedChildContainers(childContainer, selectedContainers);
            }
        }
    }
}