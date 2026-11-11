// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Staging new Pending Exports and their attribute value changes (#2919).
/// </summary>
/// <remarks>
/// <para>
/// Every staging path writes through here: a synchronisation's page flush and the passes after its page loop, deletion
/// staging, reference recall, Synchronised Deprovisioning and Synchronisation Rule deletion recall. A page of 500 groups
/// carries one change per member to add or remove, 86,774 rows a page in the 500,000-object Full Synchronisation, and on
/// one connection their COPY was 0.75 s of each group page: 37.3 s of the run, at a size the other bulk writers already
/// spread across the parallel connections (#2872). So a call large enough to gain goes parallel as theirs do.
/// </para>
/// <para>
/// No caller needs to stay on one connection, so there is no opt-out (unlike <see cref="PersistCsoChangesAsync"/>, whose
/// export callers write several batches at once and would each take the parallelism's share of the pool). Every caller
/// is a Worker task writing one thing at a time on its own context: none runs this beside another parallel writer, and a
/// synchronisation's history writer, the one other connection a run holds, is waited for before the page's state writes
/// begin. A call therefore holds the context's connection plus the parallel connections, as every other parallel writer
/// does. Export execution, whose batches do run at once, never creates Pending Exports.
/// </para>
/// </remarks>
public partial class SyncRepository
{
    /// <summary>
    /// Stages new Pending Exports and their attribute value changes via COPY binary import: in one transaction on the
    /// context's connection (the caller's transaction, when it holds one), or, for a call large enough to gain from it,
    /// across the parallel connections (see <see cref="CreatePendingExportsInParallelAsync"/>). Either way the call is
    /// all or nothing: a failure leaves none of its rows.
    /// </summary>
    public async Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        var exports = pendingExports.ToList();
        if (exports.Count == 0)
            return;

        // Inserted after any queued delete for the same objects (the Pending Export batch scope, #2492). That delete runs
        // on the context's connection and commits now unless the caller holds a transaction, in which case the call
        // stays on that connection, so the unique index on ConnectedSystemObjectId sees it gone either way.
        await ExecuteQueuedDeletesForAsync(exports.Select(pe => pe.ConnectedSystemObjectId));

        foreach (var pe in exports)
        {
            if (pe.Id == Guid.Empty)
                pe.Id = Guid.NewGuid();

            foreach (var change in pe.AttributeValueChanges)
            {
                if (change.Id == Guid.Empty)
                    change.Id = Guid.NewGuid();
            }
        }

        // Each change beside the id of the export holding it, export by export, so the rows partition evenly across
        // connections however unevenly the changes fall across the exports (one group can be most of a page).
        var changeRows = exports
            .SelectMany(pe => pe.AttributeValueChanges.Select(change => (PendingExportId: pe.Id, Change: change)))
            .ToList();

        using var span = MvoActivitySource.StartActivity("PendingExport.Create");
        span?.SetTag("count", exports.Count);
        span?.SetTag("changeCount", changeRows.Count);

        // Gated on every row the call writes, as the other bulk writers are (#2872).
        if (ShouldWriteInParallel(exports.Count + changeRows.Count))
        {
            span?.SetTag("connections", WriteParallelism);
            await CreatePendingExportsInParallelAsync(exports, changeRows);
            return;
        }

        span?.SetTag("connections", 1);
        await CreatePendingExportsInOneTransactionAsync(exports, changeRows);
    }

    /// <summary>
    /// A call too small to gain from parallel connections, or whose caller holds a transaction: the exports, then their
    /// changes, on the context's connection in one transaction. The caller's, when it holds one (Npgsql does not nest
    /// transactions), so the caller's rollback takes them with it.
    /// </summary>
    private async Task CreatePendingExportsInOneTransactionAsync(
        List<PendingExport> exports,
        List<(Guid PendingExportId, PendingExportAttributeValueChange Change)> changeRows)
    {
        await using var transaction = _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync()
            : null;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var npgsqlTransaction = (NpgsqlTransaction)_context.Database.CurrentTransaction!.GetDbTransaction();

        using (var exportsSpan = MvoActivitySource.StartActivity("PendingExport.Create.Exports"))
        {
            exportsSpan?.SetTag("rowCount", exports.Count);
            exportsSpan?.SetTag("connections", 1);
            await BulkInsertPendingExportsOnConnectionAsync(connection, npgsqlTransaction, exports);
        }

        if (changeRows.Count > 0)
        {
            using var changesSpan = MvoActivitySource.StartActivity("PendingExport.Create.Changes");
            changesSpan?.SetTag("rowCount", changeRows.Count);
            changesSpan?.SetTag("connections", 1);
            await BulkInsertPendingExportAttributeValueChangesOnConnectionAsync(connection, npgsqlTransaction, changeRows);
        }

        if (transaction != null)
            await transaction.CommitAsync();
    }

    /// <summary>
    /// A call large enough to gain from parallel connections (#2919), written in two phases, each across the parallel
    /// connections, each connection committing its own part, as the Connected System Object create writes its objects
    /// and their values (#2872):
    /// <list type="number">
    /// <item>The Pending Export rows. Every one is committed before any change is written, because each change holds a
    /// foreign key to its export: a change written on one connection may belong to an export another connection
    /// wrote.</item>
    /// <item>The changes, partitioned by row rather than by export, so a large group's member changes spread across every
    /// connection.</item>
    /// </list>
    /// The phases commit separately, so unlike the single-connection path the call is not one transaction. A failure in
    /// either therefore waits for every connection to finish, then removes what the call committed before it propagates:
    /// the caller is told the staging failed and finds nothing of it, as after the single-connection path's rollback,
    /// rather than exports holding some of their changes, which the next export run would send as they are.
    /// </summary>
    private async Task CreatePendingExportsInParallelAsync(
        List<PendingExport> exports,
        List<(Guid PendingExportId, PendingExportAttributeValueChange Change)> changeRows)
    {
        var parallelism = WriteParallelism;
        var connectionString = ConnectionStringForParallelWrites!;
        Log.Information("CreatePendingExportsAsync: Writing {Count} Pending Exports and {ChangeCount} attribute value changes across {Parallelism} parallel connections",
            exports.Count, changeRows.Count, parallelism);

        var committedExportIds = new ConcurrentQueue<Guid>();
        var written = false;
        try
        {
            using (var exportsSpan = MvoActivitySource.StartActivity("PendingExport.Create.Exports"))
            {
                exportsSpan?.SetTag("rowCount", exports.Count);
                exportsSpan?.SetTag("connections", Math.Min(parallelism, exports.Count));
                await ParallelBatchWriter.ExecuteAsync(exports, parallelism, connectionString, async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    await BulkInsertPendingExportsOnConnectionAsync(connection, transaction, partition);
                    await transaction.CommitAsync();
                    foreach (var pe in partition)
                        committedExportIds.Enqueue(pe.Id);
                });
            }

            if (changeRows.Count > 0)
            {
                // Every export is committed now, so a change may belong to one any connection wrote.
                using var changesSpan = MvoActivitySource.StartActivity("PendingExport.Create.Changes");
                changesSpan?.SetTag("rowCount", changeRows.Count);
                changesSpan?.SetTag("connections", Math.Min(parallelism, changeRows.Count));
                await ParallelBatchWriter.ExecuteAsync(changeRows, parallelism, connectionString, async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    await BulkInsertPendingExportAttributeValueChangesOnConnectionAsync(connection, transaction, partition);
                    await transaction.CommitAsync();
                });
            }

            written = true;
        }
        finally
        {
            // Only what this call committed: the exports by the ids their partitions committed (an export whose id was
            // already in the table failed its partition, so it is not in the list, and the export that already held it is
            // left alone), and their changes by the export they belong to, never by their own ids, which a change of
            // another call may hold. ParallelBatchWriter waits for every partition before it throws, so nothing of the
            // call is still being written while it is removed.
            if (!written && !committedExportIds.IsEmpty)
                await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(CreatePendingExportsAsync),
                    PendingExportRemovals([.. committedExportIds]));
        }
    }

    /// <summary>
    /// What removing the Pending Exports a failed parallel write committed takes: their changes first, by the export they
    /// belong to, then the exports. The changes' foreign key would take them by cascade; removing them first keeps the
    /// order the other writers' removals use (see <see cref="RemoveRowsOfFailedParallelWriteAsync"/>).
    /// </summary>
    private static ParallelWriteRemoval[] PendingExportRemovals(Guid[] committedExportIds) =>
    [
        new ParallelWriteRemoval("PendingExportAttributeValueChanges", "PendingExportId", committedExportIds),
        new ParallelWriteRemoval("PendingExports", "Id", committedExportIds)
    ];
}
