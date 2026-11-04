// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Implemented by a Connector with Partitions that can say which Partition one of its objects is in, from the object's
/// identifier in the Connected System's own terms (#2723).
/// </summary>
/// <remarks>
/// <para>
/// Where an object is, is the Connector's knowledge: a distinguished name suffix for a directory, compared as the
/// directory compares names, and JIM has no business guessing it. Which Partition JIM records is JIM's policy
/// (<see cref="ConnectedSystemPartitionPlacement"/>): it prefers a selected Partition, and keeps what it holds when the
/// Connector has no answer. JIM asks with each object an import reads, other than a delete, using the object's Secondary
/// External ID where it has one, else its External ID.
/// </para>
/// <para>
/// The answer must agree with the rule the Connector uses to attribute a change to a Partition anywhere else (for the
/// LDAP Connector, which Partition's Delta Import watermark covers a change, #2715), so the two can never disagree about
/// where an object is.
/// </para>
/// <para>
/// A Connector that reports Partitions through <see cref="IConnectorPartitions"/> should implement this. One that does
/// not is not broken: each object it creates is recorded against the Partition of the Run Profile that read it.
/// </para>
/// </remarks>
public interface IConnectorPartitionPlacement
{
    /// <summary>
    /// Prepares to place objects among <paramref name="partitions"/>. JIM calls this once per run (at most twice, to
    /// prefer the selected Partitions) and then places every object the run reads with the placer returned, so whatever
    /// the Connector must parse from a Partition is parsed here, once, not once per object.
    /// </summary>
    /// <param name="partitions">
    /// The Partitions to choose from. The collection and the Partitions in it do not change while the placer is in use.
    /// May be empty, in which case the placer places nothing.
    /// </param>
    /// <returns>A placer for those Partitions. Must not throw for a Partition whose identifier cannot be read; such a
    /// Partition simply holds nothing.</returns>
    IConnectorPartitionPlacer PreparePartitionPlacement(IReadOnlyCollection<ConnectedSystemPartition> partitions);
}
