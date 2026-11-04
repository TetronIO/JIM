// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Places objects among the Partitions it was prepared with (<see cref="IConnectorPartitionPlacement"/>, #2723).
/// </summary>
public interface IConnectorPartitionPlacer
{
    /// <summary>
    /// The Partition, of those this placer was prepared with, that the object is in: where Partitions nest (a directory
    /// hosting one suffix beneath another), the most specific one.
    /// </summary>
    /// <param name="objectIdentifier">
    /// The object's identifier in the Connected System's own terms; the Distinguished Name for a directory.
    /// </param>
    /// <returns>
    /// The Partition, or null when the object is in none of them, or its identifier is empty or cannot be read. Must not
    /// throw: an object JIM cannot place keeps the Partition JIM already holds for it, which is better than a failed run.
    /// </returns>
    ConnectedSystemPartition? GetPartition(string? objectIdentifier);
}
