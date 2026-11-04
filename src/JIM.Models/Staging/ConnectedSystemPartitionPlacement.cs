// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;

namespace JIM.Models.Staging;

/// <summary>
/// Which Partition JIM records for a Connected System Object, from where its Connector says the object is (#2723).
/// </summary>
/// <remarks>
/// <para>
/// The Connector owns where an object is (<see cref="IConnectorPartitionPlacement"/>); JIM owns which of the Partitions
/// holding it to record. JIM prefers the most specific selected Partition holding the object, then the most specific of
/// any: an object inside a deselected Partition nested beneath a selected one is recorded against the selected one,
/// because that is the Partition a Run Profile reads, and so the only one whose Full Import can notice the object has
/// gone. An object no selected Partition holds is still recorded truthfully, against the deselected Partition it is in.
/// </para>
/// <para>
/// Prepared once per run: the Connector parses its Partitions when prepared, so placing each object costs only reading
/// its identifier. No answer (the object is in no Partition, or its identifier cannot be read) means the caller keeps the
/// Partition it already holds; a Connector that cannot place objects at all has no placement, and the caller keeps its
/// rule from before placement existed: the Run Profile's Partition.
/// </para>
/// </remarks>
public sealed class ConnectedSystemPartitionPlacement
{
    /// <summary>Prepared over the selected Partitions; null when none is selected.</summary>
    private readonly IConnectorPartitionPlacer? _selected;

    /// <summary>Prepared over every Partition; null when every Partition is selected, so it would answer as the first.</summary>
    private readonly IConnectorPartitionPlacer? _all;

    private ConnectedSystemPartitionPlacement(IConnectorPartitionPlacer? selected, IConnectorPartitionPlacer? all)
    {
        _selected = selected;
        _all = all;
    }

    /// <summary>
    /// Prepares the placement of a run's objects among a Connected System's Partitions.
    /// </summary>
    /// <param name="connector">The Connected System's Connector.</param>
    /// <param name="partitions">Every Partition of the Connected System, selected or not.</param>
    /// <returns>
    /// The placement, or null where there is none to make: the Connector cannot place objects, or the Connected System
    /// has no Partitions.
    /// </returns>
    public static ConnectedSystemPartitionPlacement? Prepare(IConnector connector, IEnumerable<ConnectedSystemPartition>? partitions)
    {
        ArgumentNullException.ThrowIfNull(connector);

        if (connector is not IConnectorPartitionPlacement placement || partitions == null)
            return null;

        var all = partitions.ToList();
        if (all.Count == 0)
            return null;

        var selected = all.Where(partition => partition.Selected).ToList();
        return new ConnectedSystemPartitionPlacement(
            selected.Count > 0 ? placement.PreparePartitionPlacement(selected) : null,
            selected.Count < all.Count ? placement.PreparePartitionPlacement(all) : null);
    }

    /// <summary>
    /// The Partition to record for an object: the most specific selected Partition its Connector says holds it, else the
    /// most specific of any.
    /// </summary>
    /// <param name="objectIdentifier">The object's identifier in the Connected System's own terms; the Distinguished Name
    /// for a directory.</param>
    /// <returns>The Partition, or null when no Partition holds the object or its identifier cannot be read.</returns>
    public ConnectedSystemPartition? Place(string? objectIdentifier) =>
        _selected?.GetPartition(objectIdentifier) ?? _all?.GetPartition(objectIdentifier);
}
