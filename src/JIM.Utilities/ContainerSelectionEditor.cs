// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
namespace JIM.Utilities;

/// <summary>
/// The rules that decide what a Container hierarchy looks like after an administrator ticks a box or changes a
/// Container's scope: the cascade down a branch, the roll-up to a parent, the partition auto-selection, and the
/// recalculation of which Containers a selection now covers.
/// </summary>
/// <remarks>
/// These are pure operations over the Container graph, deliberately separated from the control that renders it.
/// They decide what a Full Import will and will not return, so they are the part worth testing, and they lived in a
/// Razor <c>@code</c> block where no test could reach them. Coverage itself is not redefined here:
/// <see cref="ConnectedSystemUtilities.ApplyContainerInclusion"/> owns that rule for the import search roots and the
/// portal alike, and this class calls it, so the tree can never show one scope while the import performs another.
/// </remarks>
public static class ContainerSelectionEditor
{
    /// <summary>
    /// Selects or deselects a Container, then settles the rest of the hierarchy around the change.
    /// </summary>
    /// <remarks>
    /// A Container the administrator can see but not tick is one a selected ancestor already covers, shown that way
    /// by <see cref="ConnectedSystemContainer.Included"/>. Clicking one means "stop covering me", not "select me",
    /// so it clears both flags rather than selecting it.
    /// </remarks>
    public static void ToggleSelected(ConnectedSystemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.Selected = !container.Included && !container.Selected;
        container.Included = false;

        // A Subtree selection covers everything beneath it, so those Containers can no longer be selected
        // separately; a OneLevel selection covers nothing beneath it, so they stay selectable in their own right.
        var coversDescendants = container.Selected && container.Scope == ConnectedSystemContainerScope.Subtree;
        foreach (var child in container.ChildContainers)
        {
            child.Included = coversDescendants;
            child.Selected = false;
            ApplyDownBranch(child, coversDescendants);
        }

        if (container.ParentContainer != null)
            RollUpToParent(container.ParentContainer);

        SelectPartitionOfSelectedContainer(container);
    }

    /// <summary>
    /// Switches a selected Container between importing its whole subtree and importing only the objects held
    /// directly in it, then recalculates which Containers the selection now covers.
    /// </summary>
    public static void ToggleScope(ConnectedSystemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.Scope = container.Scope == ConnectedSystemContainerScope.Subtree
            ? ConnectedSystemContainerScope.OneLevel
            : ConnectedSystemContainerScope.Subtree;

        // Narrowing releases the Containers beneath this one and widening takes them back, so the whole branch's
        // coverage has to be recalculated rather than nudged.
        var partition = FindPartition(container);
        if (partition != null)
            RecalculateCoverage(partition);
    }

    /// <summary>
    /// Recalculates every Container's coverage flag in a partition from the current selections and scopes.
    /// </summary>
    public static void RecalculateCoverage(ConnectedSystemPartition partition) =>
        ConnectedSystemUtilities.ApplyContainerInclusion(partition);

    /// <summary>
    /// Deselects every Container in a partition, at any depth, and clears the coverage that followed from it.
    /// </summary>
    public static void ClearSelection(ConnectedSystemPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);

        foreach (var container in Flatten(partition))
        {
            container.Selected = false;
            container.Included = false;
        }
    }

    /// <summary>
    /// How many Containers in a partition are selected, at any depth.
    /// </summary>
    public static int CountSelected(ConnectedSystemPartition partition) => Flatten(partition).Count(c => c.Selected);

    /// <summary>
    /// How many Containers a partition holds, at any depth.
    /// </summary>
    public static int CountAll(ConnectedSystemPartition partition) => Flatten(partition).Count();

    /// <summary>
    /// Whether a Container should be shown when the tree is filtered: it matches, or something beneath it does.
    /// </summary>
    /// <remarks>
    /// The second half is what stops a match being orphaned from the branch that says where it sits. Matching the
    /// external id as well as the name lets an administrator paste a Distinguished Name from elsewhere and find the
    /// Container, which is how they usually arrive with one.
    /// </remarks>
    public static bool MatchesFilter(ConnectedSystemContainer container, string? filter)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return MatchesSelf(container, filter.Trim()) ||
               container.ChildContainers.Any(c => MatchesFilter(c, filter));
    }

    private static bool MatchesSelf(ConnectedSystemContainer container, string filter) =>
        (container.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (container.ExternalId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Every Container in a partition, at any depth, in hierarchy order.
    /// </summary>
    public static IEnumerable<ConnectedSystemContainer> Flatten(ConnectedSystemPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);

        return Flatten(partition.Containers ?? []);
    }

    private static IEnumerable<ConnectedSystemContainer> Flatten(IEnumerable<ConnectedSystemContainer> containers) =>
        containers.SelectMany(c => new[] { c }.Concat(Flatten(c.ChildContainers)));

    private static void ApplyDownBranch(ConnectedSystemContainer container, bool included)
    {
        foreach (var child in container.ChildContainers)
        {
            child.Included = included;
            child.Selected = false;
            ApplyDownBranch(child, included);
        }
    }

    /// <summary>
    /// Replaces a complete set of sibling selections with one selection on their parent, where doing so says exactly
    /// the same thing.
    /// </summary>
    /// <remarks>
    /// It only says the same thing when every sibling is selected as a whole subtree. A sibling narrowed to One
    /// Level is a deliberate statement that the Containers beneath it are out of scope, and rolling up would replace
    /// it with a Subtree selection on the parent that silently brings them back in.
    /// </remarks>
    private static void RollUpToParent(ConnectedSystemContainer parentContainer)
    {
        if (parentContainer.ChildContainers.All(c => c.Selected && c.Scope == ConnectedSystemContainerScope.Subtree))
        {
            parentContainer.Selected = true;
            parentContainer.Included = false;

            foreach (var childContainer in parentContainer.ChildContainers)
            {
                childContainer.Selected = false;
                childContainer.Included = true;
                ApplyDownBranch(childContainer, true);
            }
        }

        if (parentContainer.ParentContainer != null)
            RollUpToParent(parentContainer.ParentContainer);
    }

    /// <summary>
    /// Selects the partition holding a Container that has just been selected, because a Container cannot be in scope
    /// while the partition around it is not.
    /// </summary>
    private static void SelectPartitionOfSelectedContainer(ConnectedSystemContainer container)
    {
        var partition = FindPartition(container);
        if (partition == null || partition.Selected)
            return;

        // The Container just toggled may have been deselected, and a sibling elsewhere in the partition may still be
        // selected, so the question is about the partition as a whole rather than about this Container.
        if (Flatten(partition).Any(c => c.Selected))
            partition.Selected = true;
    }

    /// <summary>
    /// The partition a Container belongs to. Only root Containers carry the back-reference, so a nested one reaches
    /// its partition through its ancestors.
    /// </summary>
    private static ConnectedSystemPartition? FindPartition(ConnectedSystemContainer container)
    {
        var current = container;
        while (current.ParentContainer != null)
            current = current.ParentContainer;

        return current.Partition;
    }
}
