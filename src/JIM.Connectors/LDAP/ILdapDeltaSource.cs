// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

/// <summary>
/// How a Delta Import learns what changed since the last import, for one family of directory. Each directory
/// family keeps its changes somewhere different (uSNChanged and the Deleted Objects container, cn=accesslog,
/// cn=changelog) and the import used to know all three; now it knows this contract, and
/// <see cref="LdapDeltaSources"/> picks the implementation from the directory type.
/// <para>
/// Every position is a Partition's own (#2715): JIM holds an <see cref="LdapPartitionWatermark"/> for each Partition
/// and hands the run's back at its start, so a run that reads one Partition moves only that Partition's watermark.
/// </para>
/// <para>
/// The order the import calls these in, on the first page of a Delta Import: <see cref="CaptureWatermarksAsync"/>
/// inside the rootDSE read; then, when every target Partition has a watermark this source can read from
/// (<see cref="HasBaseline"/>), <see cref="VerifyContinuity"/> once for each distinct watermark; then
/// <see cref="VerifyReadinessAsync"/>, whose findings either stop the run or become its warning
/// (<see cref="LdapDeltaSourceFindings"/>); then <see cref="ReadChangesAsync"/>. When any target Partition has none,
/// the import reads every object in its Partitions instead, and no continuity is verified. Later pages call only
/// <see cref="HasBaseline"/> and <see cref="ReadChangesAsync"/>. A Full Import calls
/// <see cref="CaptureWatermarksAsync"/> alone, to leave a baseline for each Partition's first Delta Import. Schema
/// Discovery calls <see cref="VerifyReadinessAsync"/> alone, to warn.
/// </para>
/// <para>Implementations hold no per-run state: what they record goes on the context's notes, the rootDSE record or the watermarks they return.</para>
/// </summary>
internal interface ILdapDeltaSource
{
    /// <summary>
    /// Takes the watermark each target Partition's next Delta Import reads from, reading whatever the rootDSE entry
    /// does not itself carry. Runs on every import, Full and Delta, before anything is read.
    /// </summary>
    /// <param name="rootDse">The record about to be persisted for the Connected System; a source may note on it what
    /// the directory says about itself (the domain controller's invocationId), never a position.</param>
    /// <param name="previous">The watermark JIM handed back for each target Partition, by Partition id: null where it
    /// holds none, or none this Connector can read. A source may use them to narrow its search for the new watermarks.
    /// Narrowing may leave a watermark earlier than an unnarrowed search would, which costs that Partition's next Delta
    /// Import a re-read, but never later, which would cost it a change.</param>
    /// <param name="targetPartitions">The Partitions this run imports from, the ones its change read is confined to.</param>
    /// <returns>
    /// A watermark for every target Partition and no other, by Partition id: null where none could be taken (a change
    /// source that cannot be read), so that Partition's next Delta Import reads every object in it rather than nothing.
    /// Empty when the run targets no Partition.
    /// </returns>
    Task<IReadOnlyDictionary<int, LdapPartitionWatermark?>> CaptureWatermarksAsync(SearchResultEntry rootDseEntry, LdapConnectorRootDse rootDse,
        IReadOnlyDictionary<int, LdapPartitionWatermark?> previous, IReadOnlyList<ConnectedSystemPartition> targetPartitions, TimeSpan searchTimeout);

    /// <summary>
    /// Establishes that a watermark still means something against the directory just reached, and throws when it does
    /// not: a USN read back from a different domain controller, a changelog trimmed past the watermark. Called once for
    /// each distinct watermark among the run's Partitions, with every Partition that holds it, so a refusal names them.
    /// A source whose watermark cannot be invalidated this way does nothing.
    /// </summary>
    /// <param name="partitions">The target Partitions holding <paramref name="previous"/>; at least one.</param>
    /// <param name="previous">The watermark they hold, one <see cref="HasBaseline"/> accepts.</param>
    /// <param name="current">The directory as this run's rootDSE read found it.</param>
    /// <exception cref="JIM.Models.Exceptions.CannotPerformDeltaImportException">The watermark no longer applies.</exception>
    void VerifyContinuity(IReadOnlyList<ConnectedSystemPartition> partitions, LdapPartitionWatermark previous, LdapConnectorRootDse current);

    /// <summary>
    /// Establishes whether the account JIM connects as can read where this source keeps its changes, without
    /// reading any. One finding per subject checked; empty when there is nothing to check.
    /// </summary>
    /// <param name="namingContexts">The partitions in question; empty at Schema Discovery before any are selected, when a source falls back to the directory's default naming context or checks nothing.</param>
    Task<IReadOnlyList<LdapDeltaSourceFinding>> VerifyReadinessAsync(LdapConnectorRootDse rootDse, IReadOnlyCollection<string> namingContexts, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a Partition's watermark is one this source can read its changes from: present, of this source's kind,
    /// and carrying a position. A Partition without one is read in full, as a Full Import reads it.
    /// </summary>
    bool HasBaseline(LdapPartitionWatermark? watermark);

    /// <summary>
    /// Reads this page's changes into the result, each target Partition's from its own watermark. A source that pages
    /// across calls adds its pagination tokens to the result and the import calls again with them; one that reads
    /// everything in one call adds none.
    /// </summary>
    Task ReadChangesAsync(LdapDeltaReadContext context, ConnectedSystemImportResult result, CancellationToken cancellationToken);
}
