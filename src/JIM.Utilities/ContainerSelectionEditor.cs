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
    /// by <see cref="ConnectedSystemContainer.Included"/>. Ticking one would only restate what that ancestor already
    /// says, so it is left unselected; the coverage itself is the ancestor's to give up.
    /// </remarks>
    public static void ToggleSelected(ConnectedSystemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.Selected = !container.Included && !container.Selected;

        // A Container states one thing about itself, so selecting it replaces any exclusion it carried rather than
        // leaving both standing. Selecting a Container an exclusion had carved out is how a branch is brought back
        // into scope, and is the reason ticking one of those is meaningful where ticking a covered one is not.
        if (container.Selected)
            container.Excluded = false;

        // Selecting a Container as a whole subtree makes any selection beneath it redundant, and deselecting one
        // takes its descendants out of scope with it; either way they stop stating anything of their own.
        foreach (var child in container.ChildContainers)
            ClearSelectionsDownBranch(child);

        if (container.ParentContainer != null)
            RollUpToParent(container.ParentContainer);

        SelectPartitionOfSelectedContainer(container);

        SettleCoverage(container);
    }

    /// <summary>
    /// Carves a Container out of the selection an ancestor made, or hands it back to that ancestor.
    /// </summary>
    /// <remarks>
    /// Only meaningful on a Container something above it already reaches; excluding one nothing covers changes
    /// nothing, exactly as selecting one an ancestor already covers changes nothing. Clearing an exclusion does not
    /// select the Container: it restores the state before the exclusion was made, which is whatever the ancestors
    /// say.
    /// </remarks>
    public static void ToggleExcluded(ConnectedSystemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.Excluded = !container.Excluded;

        // Mutually exclusive with the selection, for the reason given in ToggleSelected.
        if (container.Excluded)
            container.Selected = false;

        SettleCoverage(container);
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

        SettleCoverage(container);
    }

    /// <summary>
    /// Re-derives what every Container in the partition has had decided about it, after an edit somewhere in it.
    /// </summary>
    /// <remarks>
    /// Every edit ends here rather than maintaining <see cref="ConnectedSystemContainer.Included"/> and
    /// <see cref="ConnectedSystemContainer.ExcludedByAncestor"/> along the way, because only the full
    /// nearest-ancestor walk knows which statement now governs a Container. Nudging the flags instead was how a
    /// re-inclusion deselected inside an excluded branch ended up claiming nothing had been decided about it, when
    /// the exclusion above had just taken it back.
    /// </remarks>
    private static void SettleCoverage(ConnectedSystemContainer container)
    {
        var partition = FindPartition(container);
        if (partition != null)
            RecalculateCoverage(partition);
    }

    /// <summary>
    /// The Container above this one whose statement decides its fate: the nearest ancestor that says something about
    /// itself and whose statement reaches this far. Null where nothing above has an opinion.
    /// </summary>
    /// <remarks>
    /// This is what a row names when it says "Covered by ou=Corp" or "Excluded from ou=Service Accounts", and it has
    /// to agree with <see cref="ConnectedSystemUtilities.ApplyContainerInclusion"/>, which is what actually set the
    /// row's state. Two rules follow from that walk: an ancestor stating nothing is skipped, and so is one whose
    /// statement is OneLevel, because such a statement reaches the objects held directly in that Container and no
    /// Container beneath it, leaving whatever came from further up still governing.
    /// </remarks>
    public static ConnectedSystemContainer? DecidingAncestor(ConnectedSystemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        for (var ancestor = container.ParentContainer; ancestor != null; ancestor = ancestor.ParentContainer)
        {
            if ((ancestor.Selected || ancestor.Excluded) && ancestor.Scope == ConnectedSystemContainerScope.Subtree)
                return ancestor;
        }

        return null;
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
            container.Excluded = false;
            container.ExcludedByAncestor = false;
        }
    }

    /// <summary>
    /// How many Containers in a partition are selected, at any depth.
    /// </summary>
    public static int CountSelected(ConnectedSystemPartition partition) => Flatten(partition).Count(c => c.Selected);

    /// <summary>
    /// How many Containers in a partition are excluded, at any depth.
    /// </summary>
    public static int CountExcluded(ConnectedSystemPartition partition) => Flatten(partition).Count(c => c.Excluded);

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

    /// <summary>
    /// Clears the selections a Container's own statement has made redundant, down its branch.
    /// </summary>
    /// <remarks>
    /// It stops at an exclusion. The selections inside a carved-out branch are re-inclusions, and a statement made
    /// above the exclusion does not reach past it to make them redundant; clearing them would take that branch back
    /// out of scope while leaving the exclusion standing, which is the opposite of what the administrator asked for.
    /// </remarks>
    private static void ClearSelectionsDownBranch(ConnectedSystemContainer container)
    {
        if (container.Excluded)
            return;

        container.Selected = false;

        foreach (var child in container.ChildContainers)
            ClearSelectionsDownBranch(child);
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
        // Never roll a selection up onto an excluded Container. Doing so would both break the rule that a Container
        // states one thing about itself and silently undo the exclusion: the children being rolled up are precisely
        // the re-inclusions an administrator made inside the branch they had carved out.
        if (parentContainer.Excluded)
            return;

        if (parentContainer.ChildContainers.All(c => c.Selected && c.Scope == ConnectedSystemContainerScope.Subtree))
        {
            parentContainer.Selected = true;

            foreach (var childContainer in parentContainer.ChildContainers)
                ClearSelectionsDownBranch(childContainer);
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
