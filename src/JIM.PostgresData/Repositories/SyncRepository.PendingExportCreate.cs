// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// Where a parallel create has got to, for <see cref="PendingExportCreateStepReached"/>.
    /// </summary>
    internal const string StepExportsStaged = "ExportsStaged";

    /// <inheritdoc cref="StepExportsStaged"/>
    internal const string StepChangesStaged = "ChangesStaged";

    /// <inheritdoc cref="StepExportsStaged"/>
    internal const string StepRemovingFailedCall = "RemovingFailedCall";

    /// <summary>
    /// Called as a parallel create reaches each step (<see cref="StepExportsStaged"/>, <see cref="StepChangesStaged"/>,
    /// <see cref="StepRemovingFailedCall"/>), for a test to look at the database between them; null otherwise.
    /// </summary>
    internal Func<string, Task>? PendingExportCreateStepReached { get; set; }

    /// <summary>
    /// A call large enough to gain from parallel connections (#2919), written in three steps:
    /// <list type="number">
    /// <item>The Pending Export rows, across the parallel connections, each committing its part, as
    /// <see cref="PendingExportStatus.Staging"/> and without their Connected System Object. Every one is committed before
    /// any change is written, because each change holds a foreign key to its export and may be written on a connection
    /// other than the one that wrote the export.</item>
    /// <item>The changes, across the parallel connections, partitioned by row rather than by export, so a large group's
    /// member changes spread across every connection.</item>
    /// <item>One statement, in one transaction, giving every export its own status and Connected System Object.</item>
    /// </list>
    /// Until the last step commits, no reader sees any of the call: a Staging export is filtered out of every query
    /// through the model, has no Connected System Object for a raw query to find it by, and holds no place in the unique
    /// index on the object. So an export run, an import, a count or another staging sees the call's exports all at once
    /// and complete, or not at all, exactly as after the single-connection path's one transaction; never an export with
    /// some of its changes, which an export run would send as it found it.
    /// </summary>
    /// <remarks>
    /// On any failure the call waits for every connection to finish, then removes what is left of it before the failure
    /// propagates: every change and export of the call's own ids that is still Staging. By the call's ids, so nothing of
    /// another call is touched; whether or not a connection's commit was acknowledged, since a row whose commit was lost
    /// is still found. Still Staging, so an export another call holds under the same id (which is why this call's insert
    /// failed) is left alone, and nothing is removed once the last step has made the exports live. A Worker that stops
    /// part-way leaves Staging rows no reader sees; <see cref="RemoveUnfinishedPendingExportStagingAsync"/> removes them
    /// when it next starts.
    /// </remarks>
    private async Task CreatePendingExportsInParallelAsync(
        List<PendingExport> exports,
        List<(Guid PendingExportId, PendingExportAttributeValueChange Change)> changeRows)
    {
        var parallelism = WriteParallelism;
        var connectionString = ConnectionStringForParallelWrites!;
        Log.Information("CreatePendingExportsAsync: Writing {Count} Pending Exports and {ChangeCount} attribute value changes across {Parallelism} parallel connections",
            exports.Count, changeRows.Count, parallelism);

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
                    await BulkInsertPendingExportsOnConnectionAsync(connection, transaction, partition, staged: true);
                    await transaction.CommitAsync();
                });
            }

            await StepReachedAsync(StepExportsStaged);

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

            await StepReachedAsync(StepChangesStaged);

            using (var liveSpan = MvoActivitySource.StartActivity("PendingExport.Create.Live"))
            {
                liveSpan?.SetTag("rowCount", exports.Count);
                await MakeStagedPendingExportsLiveAsync(exports);
            }

            written = true;
        }
        finally
        {
            if (!written)
            {
                await StepReachedAsync(StepRemovingFailedCall);
                await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(CreatePendingExportsAsync),
                    StagedPendingExportRemovals([.. exports.Select(pe => pe.Id)]));
            }
        }
    }

    private Task StepReachedAsync(string step) => PendingExportCreateStepReached?.Invoke(step) ?? Task.CompletedTask;

    /// <summary>
    /// The last step of a parallel create: on the context's connection, in one transaction, every staged export takes its
    /// own status and Connected System Object, so all of them come into effect at once. Only rows still Staging are
    /// touched, and every one must be: a row missing (removed by another hand) fails the call, which then removes the
    /// rest. Taking the object can meet the unique index on it exactly where the single-connection insert would have.
    /// </summary>
    private async Task MakeStagedPendingExportsLiveAsync(List<PendingExport> exports)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        // A status mark and the one foreign key withheld while staging, set from values the call holds: the
        // targeted-mutation exemption of the raw SQL column-list rules, not a PendingExportBulkColumns writer.
        await using var command = new NpgsqlCommand(
            """
            UPDATE "PendingExports" AS pe
            SET "Status" = live.status, "ConnectedSystemObjectId" = live.cso_id
            FROM unnest(@ids, @statuses, @csoIds) AS live(id, status, cso_id)
            WHERE pe."Id" = live.id AND pe."Status" = @staging
            """, connection, npgsqlTransaction);
        command.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = exports.Select(pe => pe.Id).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = exports.Select(pe => (int)pe.Status).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = exports.Select(pe => pe.ConnectedSystemObjectId).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter("staging", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportStatus.Staging });

        var madeLive = await command.ExecuteNonQueryAsync();
        if (madeLive != exports.Count)
            throw new InvalidOperationException(
                $"CreatePendingExportsAsync: {exports.Count} Pending Exports were written to be made live together, but only {madeLive} were still " +
                "waiting; nothing of the call is kept.");

        await transaction.CommitAsync();
    }

    /// <summary>
    /// What removing a failed parallel create takes: by every id the call wrote under, the changes of its exports that are
    /// still Staging, then those exports (see <see cref="CreatePendingExportsInParallelAsync"/> for why by every id and
    /// only Staging). The changes would go by their foreign key's cascade; removing them first keeps the order the other
    /// writers' removals use (see <see cref="RemoveRowsOfFailedParallelWriteAsync"/>).
    /// </summary>
    private static ParallelWriteRemoval[] StagedPendingExportRemovals(Guid[] exportIds)
    {
        var staging = (int)PendingExportStatus.Staging;
        return
        [
            new ParallelWriteRemoval("PendingExportAttributeValueChanges", "PendingExportId", exportIds,
                $"""EXISTS (SELECT 1 FROM "PendingExports" pe WHERE pe."Id" = "PendingExportAttributeValueChanges"."PendingExportId" AND pe."Status" = {staging})"""),
            new ParallelWriteRemoval("PendingExports", "Id", exportIds, $"\"Status\" = {staging}")
        ];
    }

    /// <summary>
    /// Removes every Pending Export still <see cref="PendingExportStatus.Staging"/>, with its changes: what a Worker that
    /// stopped part-way through a parallel create left behind (#2919). For the Worker's start, when nothing can be
    /// writing one; no reader sees them meanwhile, and none holds a place in the unique index on its object.
    /// </summary>
    /// <returns>The Pending Exports removed.</returns>
    public async Task<int> RemoveUnfinishedPendingExportStagingAsync()
    {
        var staging = (int)PendingExportStatus.Staging;
        await using var transaction = await _context.Database.BeginTransactionAsync();
        await _context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM "PendingExportAttributeValueChanges"
            WHERE "PendingExportId" IN (SELECT "Id" FROM "PendingExports" WHERE "Status" = {0})
            """, staging);
        var removed = await _context.Database.ExecuteSqlRawAsync("""DELETE FROM "PendingExports" WHERE "Status" = {0}""", staging);
        await transaction.CommitAsync();
        return removed;
    }
}
