// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Preview;

/// <summary>
/// The partitions and containers an administrator is proposing to manage on a Connected System, as the partition and
/// container deselection preview adapter receives them (#1251).
/// </summary>
/// <remarks>
/// Two id sets rather than an edited <see cref="ConnectedSystem"/>, for the reason the framework states generally
/// and which bites hardest here: a proposal may be evaluated in JIM.Worker, so it has to survive a JSON round trip,
/// and the Partitions tab edits <see cref="ConnectedSystemPartition.Selected"/> and
/// <see cref="ConnectedSystemContainer.Selected"/> in place on the loaded entity graph. Handing that graph to an
/// adapter would give it an object where the proposed selection has already overwritten the current one, and the
/// preview would compare the proposal against itself and report that nothing would change.
/// </remarks>
/// <param name="SelectedPartitionIds">The partitions that would be managed.</param>
/// <param name="SelectedContainerIds">
/// The containers that would be managed. Selecting a container selects its whole subtree, so a descendant need not
/// appear here to be in scope; this is the set of containers explicitly ticked, exactly as
/// <see cref="ConnectedSystemContainer.Selected"/> records it.
/// </param>
public record ConnectedSystemScopeSelectionProposal(
    IReadOnlyList<int> SelectedPartitionIds,
    IReadOnlyList<int> SelectedContainerIds)
{
    /// <summary>
    /// The selection currently in force on <paramref name="connectedSystem"/>, as a proposal. What "no change"
    /// looks like, and the baseline an adapter evaluates a proposal against.
    /// </summary>
    public static ConnectedSystemScopeSelectionProposal FromCurrentSelection(ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var partitions = connectedSystem.Partitions ?? [];
        return new ConnectedSystemScopeSelectionProposal(
            [.. partitions.Where(p => p.Selected).Select(p => p.Id)],
            [.. partitions
                .Where(p => p.Containers != null)
                .SelectMany(p => CollectSelectedContainerIds(p.Containers!))]);
    }

    /// <summary>
    /// Whether <paramref name="other"/> proposes the same selection as this one. What decides whether a preview an
    /// administrator is looking at still answers the question they are about to ask.
    /// </summary>
    /// <remarks>
    /// Not the record's own equality: the two id lists are compared by reference by the generated <c>Equals</c>, so
    /// an editor rebuilding the proposal on every render would report a change that never happened. A selection is
    /// a set, so the order the tick boxes were clicked in is not a configuration change either.
    /// </remarks>
    public bool DescribesSameSelectionAs(ConnectedSystemScopeSelectionProposal? other)
    {
        if (other is null)
            return false;

        return SelectedPartitionIds.Order().SequenceEqual(other.SelectedPartitionIds.Order()) &&
               SelectedContainerIds.Order().SequenceEqual(other.SelectedContainerIds.Order());
    }

    private static IEnumerable<int> CollectSelectedContainerIds(IEnumerable<ConnectedSystemContainer> containers)
    {
        foreach (var container in containers)
        {
            if (container.Selected)
                yield return container.Id;

            foreach (var descendantId in CollectSelectedContainerIds(container.ChildContainers))
                yield return descendantId;
        }
    }
}
