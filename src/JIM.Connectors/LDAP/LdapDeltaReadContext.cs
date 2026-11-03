// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Everything a change source needs to read one page of changes: the watermark each target Partition reads from, the
/// directory as it is now, what the Run Profile targets, and the way back into the import for conversion and reporting.
/// </summary>
internal sealed class LdapDeltaReadContext
{
    /// <summary>
    /// The record the last import left for the Connected System, as JIM handed it at the start of the run: the
    /// directory's facts (its type, where its changelog is). It holds no position; those are <see cref="Watermarks"/>.
    /// </summary>
    internal required LdapConnectorRootDse PreviousRootDse { get; init; }

    /// <summary>
    /// The watermark each target Partition reads its changes after, by Partition id: one for every Partition in
    /// <see cref="TargetPartitions"/>, each one this source's <see cref="ILdapDeltaSource.HasBaseline"/> accepts. The
    /// import reads every object instead when any target Partition has none, so a change read never lacks one.
    /// </summary>
    internal required IReadOnlyDictionary<int, LdapPartitionWatermark> Watermarks { get; init; }

    /// <summary>
    /// The directory as read at the start of this import; on later pages, the record JIM handed the run at its start.
    /// Null only when that record could not be deserialised, in which case a source assumes the directory's
    /// defaults (paging supported).
    /// </summary>
    internal required LdapConnectorRootDse? CurrentRootDse { get; init; }

    /// <summary>The partitions this run imports from, already reduced to the Run Profile's selection; at least one.</summary>
    internal required IReadOnlyList<ConnectedSystemPartition> TargetPartitions { get; init; }

    /// <summary>Every Container stating something about scope for this run, for sources whose log is directory-wide.</summary>
    internal required IReadOnlyList<ConnectedSystemContainer> ScopeDecidingContainers { get; init; }

    /// <summary>All of the Connected System's Object Types; a source filters to the selected ones where it searches per type.</summary>
    internal required IReadOnlyList<ConnectedSystemObjectType> ObjectTypes { get; init; }

    /// <summary>The pagination tokens the previous page left; empty on the first page.</summary>
    internal required IReadOnlyList<ConnectedSystemPaginationToken> PaginationTokens { get; init; }

    /// <summary>The Run Profile's page size, for sources that page.</summary>
    internal required int PageSize { get; init; }

    /// <summary>How long to wait for the directory to answer a search.</summary>
    internal required TimeSpan SearchTimeout { get; init; }

    /// <summary>Where a source records what it could not detect and why; the run carries the notes as its warning.</summary>
    internal required LdapDeltaSourceNotes Notes { get; init; }

    internal required ILdapDeltaImportHost Host { get; init; }

    /// <summary>
    /// The oldest of the target Partitions' positions, by the order the given position sorts in: where a source whose
    /// one log every Partition shares (the changelog) starts its read, so no Partition misses a change. The source then
    /// passes over, entry by entry, what a Partition with a later watermark has already read.
    /// </summary>
    internal T OldestWatermark<T>(Func<LdapPartitionWatermark, T> position, IComparer<T> order)
    {
        if (Watermarks.Count == 0)
            throw new InvalidOperationException("A change read was started with no Partition watermark to read from; the import reads every object instead when a target Partition has none.");

        return Watermarks.Values.Select(position).Min(order)!;
    }
}
