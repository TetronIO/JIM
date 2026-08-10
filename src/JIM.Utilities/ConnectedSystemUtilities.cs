// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
namespace JIM.Utilities;

public static class ConnectedSystemUtilities
{
    /// <summary>
    /// Generates a list of the selected containers that each need searching in their own right, discarding those
    /// an ancestor's search already covers. Searching a container a selected ancestor already covers would import
    /// the same objects twice.
    /// </summary>
    /// <remarks>
    /// Coverage depends on the ancestor's <see cref="ConnectedSystemContainerScope"/>:
    /// <list type="bullet">
    /// <item>A Subtree container covers every descendant, so its selected descendants are redundant. If OU=Corp
    /// and OU=Users,OU=Corp are both selected, only OU=Corp is returned.</item>
    /// <item>A OneLevel container covers only the objects held directly within it, so its selected descendants are
    /// not redundant and are returned as search roots of their own. If OU=Corp is selected OneLevel and
    /// OU=Users,OU=Corp is selected, both are returned; dropping OU=Users would silently stop importing it.</item>
    /// </list>
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
                selectedContainers.Add(rootContainer);

            // Descendants still need searching unless this container's own search already covers them.
            if (!CoversDescendants(rootContainer))
                SearchForTopLevelSelectedChildContainers(rootContainer, selectedContainers);
        }

        return selectedContainers;
    }

    /// <summary>
    /// Whether searching this container also returns everything beneath it, making any selected descendant
    /// redundant as a search root.
    /// </summary>
    private static bool CoversDescendants(ConnectedSystemContainer container) =>
        container.Selected && container.Scope == ConnectedSystemContainerScope.Subtree;

    /// <summary>
    /// Recalculates every container's <see cref="ConnectedSystemContainer.Included"/> display flag from the current
    /// selections and scopes, for the whole of a partition's container hierarchy.
    /// </summary>
    /// <remarks>
    /// A container is "included" when a selected ancestor's search already covers it, so the administrator does not
    /// need to (and cannot) select it in its own right. Only a <see cref="ConnectedSystemContainerScope.Subtree"/>
    /// ancestor covers anything beneath it: beneath a <see cref="ConnectedSystemContainerScope.OneLevel"/> ancestor
    /// nothing is imported at all, so those containers stay selectable. This is the display-side counterpart of the
    /// coverage rule <see cref="GetTopLevelSelectedContainers"/> applies, and both must agree; if they do not, the
    /// portal shows one scope and the import performs another.
    /// </remarks>
    public static void ApplyContainerInclusion(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        foreach (var rootContainer in connectedSystemPartition.Containers ?? [])
        {
            // A root container has no ancestor, so nothing can be covering it.
            rootContainer.Included = false;
            ApplyContainerInclusion(rootContainer, CoversDescendants(rootContainer));
        }
    }

    private static void ApplyContainerInclusion(ConnectedSystemContainer container, bool coveredByAnAncestor)
    {
        foreach (var childContainer in container.ChildContainers)
        {
            childContainer.Included = coveredByAnAncestor;
            ApplyContainerInclusion(childContainer, coveredByAnAncestor || CoversDescendants(childContainer));
        }
    }

    /// <summary>
    /// Whether a container newly discovered beneath <paramref name="parentContainer"/> needs selecting in its own
    /// right for the objects held within it to be imported. Used when a Connector reports containers it created
    /// during an export, so that objects provisioned into them are imported back.
    /// </summary>
    /// <param name="parentContainer">The new container's parent, or null when it sits at the top of a partition.</param>
    /// <param name="partitionSelected">Whether the partition the new container belongs to is selected.</param>
    /// <remarks>
    /// Two rules combine, and the scope of each ancestor decides which applies:
    /// <list type="bullet">
    /// <item>A selected Subtree ancestor's search already returns everything beneath it, so selecting the new
    /// container as well would import the same objects twice.</item>
    /// <item>A selected OneLevel ancestor returns only the objects held directly within it, so it does not reach
    /// into the new container. Leaving the new container unselected there would mean the objects just provisioned
    /// into it are never imported, silently and with nothing to see in the portal.</item>
    /// </list>
    /// Beyond coverage, the new container is only wanted where the administrator has already asked for the branch it
    /// sits in: directly beneath a selected container, or at the top of a selected partition. A container created
    /// somewhere the administrator never selected must not put itself into scope.
    /// </remarks>
    public static bool NewContainerNeedsSelecting(ConnectedSystemContainer? parentContainer, bool partitionSelected)
    {
        if (IsCoveredByAnAncestorSearch(parentContainer))
            return false;

        return parentContainer?.Selected ?? partitionSelected;
    }

    /// <summary>
    /// Whether some ancestor's search already returns the objects held beneath <paramref name="container"/>, walking
    /// up from <paramref name="container"/> itself.
    /// </summary>
    private static bool IsCoveredByAnAncestorSearch(ConnectedSystemContainer? container)
    {
        for (var ancestor = container; ancestor != null; ancestor = ancestor.ParentContainer)
        {
            if (CoversDescendants(ancestor))
                return true;
        }

        return false;
    }

    private static void SearchForTopLevelSelectedChildContainers(ConnectedSystemContainer container, ICollection<ConnectedSystemContainer> selectedContainers)
    {
        if (container.ChildContainers.Count == 0)
            return;

        foreach (var childContainer in container.ChildContainers)
        {
            if (childContainer.Selected)
                selectedContainers.Add(childContainer);

            if (!CoversDescendants(childContainer))
                SearchForTopLevelSelectedChildContainers(childContainer, selectedContainers);
        }
    }
}