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
/// <param name="ExcludedContainerIds">
/// The containers that would be carved out of the selection around them (#1255), exactly as
/// <see cref="ConnectedSystemContainer.Excluded"/> records it. Null and empty mean the same thing: nothing carved
/// out. A proposal that omits an exclusion the Connected System currently carries is proposing to remove it, so
/// callers building a partial proposal must carry the current exclusions forward rather than leaving this unset.
/// </param>
public record ConnectedSystemScopeSelectionProposal(
    IReadOnlyList<int> SelectedPartitionIds,
    IReadOnlyList<int> SelectedContainerIds,
    IReadOnlyList<int>? ExcludedContainerIds = null)
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
                .SelectMany(p => CollectContainerIds(p.Containers!, container => container.Selected))],
            [.. partitions
                .Where(p => p.Containers != null)
                .SelectMany(p => CollectContainerIds(p.Containers!, container => container.Excluded))]);
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
               SelectedContainerIds.Order().SequenceEqual(other.SelectedContainerIds.Order()) &&
               (ExcludedContainerIds ?? []).Order().SequenceEqual((other.ExcludedContainerIds ?? []).Order());
    }

    private static IEnumerable<int> CollectContainerIds(
        IEnumerable<ConnectedSystemContainer> containers,
        Func<ConnectedSystemContainer, bool> statesSomething)
    {
        foreach (var container in containers)
        {
            if (statesSomething(container))
                yield return container.Id;

            foreach (var descendantId in CollectContainerIds(container.ChildContainers, statesSomething))
                yield return descendantId;
        }
    }
}
