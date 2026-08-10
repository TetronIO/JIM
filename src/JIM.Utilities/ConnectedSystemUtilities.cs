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
    /// Recalculates every container's <see cref="ConnectedSystemContainer.Included"/> and
    /// <see cref="ConnectedSystemContainer.ExcludedByAncestor"/> display flags from the current selections,
    /// exclusions and scopes, for the whole of a partition's container hierarchy.
    /// </summary>
    /// <remarks>
    /// Both flags answer one question: what has already been decided about this container from above? A container is
    /// "included" when a selected ancestor's search covers it, so the administrator does not need to (and cannot)
    /// select it in its own right; it is "excluded by ancestor" when an excluded ancestor has carved it out, which
    /// the administrator can overrule by selecting it. Only a <see cref="ConnectedSystemContainerScope.Subtree"/>
    /// statement reaches past its own container: beneath a <see cref="ConnectedSystemContainerScope.OneLevel"/> one
    /// nothing is decided at all, so those containers stay open to statements of their own.
    ///
    /// A container that states something itself carries neither flag, whichever an ancestor said, because its own
    /// statement is what governs it. That is the same rule the import applies when it asks which container decides
    /// an object's fate (<c>ContainerSpecificity</c>), one level up: nearest statement wins. This is the
    /// display-side counterpart of the coverage rule <see cref="GetTopLevelSelectedContainers"/> applies, and all
    /// three must agree; if they do not, the portal shows one scope and the import performs another.
    /// </remarks>
    public static void ApplyContainerInclusion(ConnectedSystemPartition connectedSystemPartition)
    {
        if (connectedSystemPartition == null)
            throw new ArgumentNullException(nameof(connectedSystemPartition));

        // A root container has no ancestor, so nothing has been decided about it from above.
        foreach (var rootContainer in connectedSystemPartition.Containers ?? [])
            ApplyContainerInclusion(rootContainer, AncestorStatement.None);
    }

    /// <summary>
    /// What the nearest ancestor holding an opinion has said about a container.
    /// </summary>
    private enum AncestorStatement
    {
        /// <summary>No ancestor's statement reaches this container.</summary>
        None,

        /// <summary>A selected ancestor's search already covers it.</summary>
        Covered,

        /// <summary>An excluded ancestor has carved it out.</summary>
        Excluded
    }

    private static void ApplyContainerInclusion(ConnectedSystemContainer container, AncestorStatement fromAncestors)
    {
        var statesSomethingItself = container.Selected || container.Excluded;
        container.Included = !statesSomethingItself && fromAncestors == AncestorStatement.Covered;
        container.ExcludedByAncestor = !statesSomethingItself && fromAncestors == AncestorStatement.Excluded;

        var forDescendants = StatementForDescendants(container, fromAncestors);
        foreach (var childContainer in container.ChildContainers)
            ApplyContainerInclusion(childContainer, forDescendants);
    }

    /// <summary>
    /// What a container's children have had decided about them: this container's own statement where it makes one
    /// that reaches them, and otherwise whatever reached this container from above.
    /// </summary>
    private static AncestorStatement StatementForDescendants(ConnectedSystemContainer container, AncestorStatement fromAncestors)
    {
        if (container.Scope != ConnectedSystemContainerScope.Subtree)
            return fromAncestors;

        if (container.Excluded)
            return AncestorStatement.Excluded;

        return container.Selected ? AncestorStatement.Covered : fromAncestors;
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