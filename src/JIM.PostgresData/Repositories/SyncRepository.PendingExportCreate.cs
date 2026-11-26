// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Utility;
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
/// is a Worker task writing one thing at a time on its own context: none runs this beside another parallel writer. A
/// synchronisation's history writer holds one more connection while a page's state is written (#2924), so a call holds
/// the context's connection plus the parallel connections, beside that one. Export execution, whose batches do run at
/// once, never creates Pending Exports. A caller holding a transaction keeps the parallel path too (#2848): see
/// <see cref="CreatePendingExportsBesideCallerTransactionAsync"/>.
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
        // on the context's connection and commits now unless the caller holds a transaction. Either way the unique index on
        // ConnectedSystemObjectId sees it gone: a row staged on another connection holds no object until it is made live on
        // the context's.
        await ExecuteQueuedDeletesForAsync(exports.Select(pe => pe.ConnectedSystemObjectId));

        // Time-ordered, so the rows land on the growing end of each index keyed on them (#2929).
        foreach (var pe in exports)
            TimeOrderedIds.AssignMissing(pe);

        // Each change beside the id of the export holding it, export by export, so the rows partition evenly across
        // connections however unevenly the changes fall across the exports (one group can be most of a page).
        var changeRows = exports
            .SelectMany(pe => pe.AttributeValueChanges.Select(change => (PendingExportId: pe.Id, Change: change)))
            .ToList();

        using var span = MvoActivitySource.StartActivity("PendingExport.Create");
        span?.SetTag("count", exports.Count);
        span?.SetTag("changeCount", changeRows.Count);

        // Gated on every row the call writes, as the other bulk writers are (#2872). Each parallel step then takes the
        // connections its own rows earn (#2669), so a people page's 5,500 changes take four, a group page's every one, and
        // either page's few hundred exports one: spread across five, a people page's exports cost 23 ms for a 2.2 ms COPY.
        // A caller holding a transaction (a synchronisation page, #2848) is no reason to stay on one connection here, as it
        // is for every other bulk writer: the exports are staged on the parallel connections as Incomplete, which nothing
        // reads, and made live in the caller's transaction (CreatePendingExportsBesideCallerTransactionAsync).
        var callerHoldsTransaction = _context.Database.CurrentTransaction != null;
        var connections = callerHoldsTransaction
            ? ConnectionsForStagingBesideCallerTransaction(exports.Count + changeRows.Count)
            : ConnectionsForWrite(exports.Count + changeRows.Count);
        if (connections > 1)
        {
            span?.SetTag("connections", connections);
            if (callerHoldsTransaction)
                await CreatePendingExportsBesideCallerTransactionAsync(exports, connections);
            else
                await CreatePendingExportsInParallelAsync(exports, changeRows, connections);
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
    /// The Pending Exports this repository staged across parallel connections for a transaction it holds (#2848), whatever
    /// became of them: made live with the transaction, rolled back with it to Incomplete, or never made live because their
    /// staging failed. <see cref="RemovePendingExportsStagedForTransactionAsync"/> removes those still Incomplete once the
    /// transaction has ended.
    /// </summary>
    private readonly List<Guid> _pendingExportIdsStagedForTransaction = [];

    /// <summary>
    /// How long a connection staging exports beside a caller's transaction waits for a lock before giving up (#2848). It
    /// should never wait at all: it writes rows under fresh ids that nothing else can hold. The one thing it could wait on
    /// is an id the caller's transaction deleted moments ago and has not committed, which would wait for good; an export
    /// reusing a replaced export's id is written on the caller's connection for that reason (see
    /// <see cref="PendingExport.ReusesReplacedExportId"/>), and this bounds the wait for any that is not.
    /// </summary>
    private const string StagingLockTimeout = "5s";

    /// <summary>
    /// A call large enough to gain from parallel connections, made while the caller holds a transaction (a synchronisation
    /// page, #2848). Exports that keep a replaced export's id are written on the caller's connection, in its transaction,
    /// since the row they replace was deleted in it. The rest are staged as <see cref="CreatePendingExportsInParallelAsync"/>
    /// stages them, across the parallel connections as Incomplete, and made live in the caller's transaction, so they come
    /// into effect when it commits, and a rollback leaves them Incomplete for
    /// <see cref="RemovePendingExportsStagedForTransactionAsync"/> to remove. Should a staging connection meet a lock all
    /// the same (an id reused without the mark), what it staged is removed and the call is written on the caller's
    /// connection instead, rather than waiting on the caller for good.
    /// </summary>
    private async Task CreatePendingExportsBesideCallerTransactionAsync(List<PendingExport> exports, int parallelism)
    {
        var reusingIds = exports.Where(pe => pe.ReusesReplacedExportId).ToList();
        var staged = reusingIds.Count == 0 ? exports : exports.Where(pe => !pe.ReusesReplacedExportId).ToList();

        if (reusingIds.Count > 0)
            await CreatePendingExportsInOneTransactionAsync(reusingIds, ChangeRowsOf(reusingIds));
        if (staged.Count == 0)
            return;

        var stagedChangeRows = ChangeRowsOf(staged);
        var connections = Math.Min(parallelism, ConnectionsForStagingBesideCallerTransaction(staged.Count + stagedChangeRows.Count));
        if (connections < 2)
        {
            await CreatePendingExportsInOneTransactionAsync(staged, stagedChangeRows);
            return;
        }

        _pendingExportIdsStagedForTransaction.AddRange(staged.Select(pe => pe.Id));
        try
        {
            await CreatePendingExportsInParallelAsync(staged, stagedChangeRows, connections, besideCallerTransaction: true);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            Log.Warning(ex, "CreatePendingExportsAsync: Staging {Count} Pending Exports beside the caller's transaction waited on a lock (an export " +
                "reusing a replaced export's id without the mark?); writing them on the transaction's connection instead", staged.Count);

            // Nothing of the call is live: what the other connections staged is Incomplete and locked by nobody.
            await RemoveRowsOfFailedParallelWriteAsync(ConnectionStringForParallelWrites!, nameof(CreatePendingExportsAsync),
                IncompletePendingExportRemovals([.. staged.Select(pe => pe.Id)]));
            await CreatePendingExportsInOneTransactionAsync(staged, stagedChangeRows);
        }
    }

    private static List<(Guid PendingExportId, PendingExportAttributeValueChange Change)> ChangeRowsOf(List<PendingExport> exports) =>
        exports.SelectMany(pe => pe.AttributeValueChanges.Select(change => (PendingExportId: pe.Id, Change: change))).ToList();

    /// <summary>
    /// Removes the Pending Exports this repository staged for a transaction it held that the transaction did not make live
    /// (#2848): all of them once it has rolled back, and any whose staging failed once it has committed. Call when the
    /// transaction has ended, whichever way: before then its locks would hold this up.
    /// </summary>
    /// <returns>The Pending Exports removed.</returns>
    public async Task<int> RemovePendingExportsStagedForTransactionAsync()
    {
        if (_pendingExportIdsStagedForTransaction.Count == 0)
            return 0;
        if (_context.Database.CurrentTransaction != null)
            throw new InvalidOperationException(
                "RemovePendingExportsStagedForTransactionAsync must be called once the transaction the exports were staged for has ended.");

        var ids = _pendingExportIdsStagedForTransaction.ToArray();
        _pendingExportIdsStagedForTransaction.Clear();

        // One statement, the changes going by their foreign key's cascade, as RemoveIncompletePendingExportsAsync removes
        // them; only those still Incomplete, so an export the transaction made live is left alone.
        return await _context.Database.ExecuteSqlRawAsync(
            """DELETE FROM "PendingExports" WHERE "Id" = ANY({0}) AND "Status" = {1}""", ids, (int)PendingExportStatus.Incomplete);
    }

    /// <summary>
    /// Where a parallel create has got to, for <see cref="PendingExportCreateStepReached"/>.
    /// </summary>
    internal const string StepExportsWritten = "ExportsWritten";

    /// <inheritdoc cref="StepExportsWritten"/>
    internal const string StepChangesWritten = "ChangesWritten";

    /// <inheritdoc cref="StepExportsWritten"/>
    internal const string StepMakingLive = "MakingLive";

    /// <inheritdoc cref="StepExportsWritten"/>
    internal const string StepRemovingFailedCall = "RemovingFailedCall";

    /// <summary>
    /// Called as a parallel create reaches each step (<see cref="StepExportsWritten"/>, <see cref="StepChangesWritten"/>,
    /// <see cref="StepMakingLive"/> once the exports are made live and before that commits, and
    /// <see cref="StepRemovingFailedCall"/>), for a test to look at the database between them; null otherwise.
    /// </summary>
    internal Func<string, Task>? PendingExportCreateStepReached { get; set; }

    /// <summary>
    /// A call large enough to gain from parallel connections (#2919), written in three steps, the parallel ones across up
    /// to <paramref name="parallelism"/> connections (<see cref="ConnectionsForWrite"/>, #2669):
    /// <list type="number">
    /// <item>The Pending Export rows, across the parallel connections, each committing its part, as
    /// <see cref="PendingExportStatus.Incomplete"/> and without their Connected System Object. Every one is committed before
    /// any change is written, because each change holds a foreign key to its export and may be written on a connection
    /// other than the one that wrote the export.</item>
    /// <item>The changes, across the parallel connections, partitioned by row rather than by export, so a large group's
    /// member changes spread across every connection.</item>
    /// <item>One statement, in one transaction, giving every export its own status and Connected System Object.</item>
    /// </list>
    /// Until the last step commits, no reader sees any of the call: an Incomplete export is filtered out of every query
    /// through the model, has no Connected System Object for a raw query to find it by, and holds no place in the unique
    /// index on the object. So an export run, an import, a count or another staging sees the call's exports all at once
    /// and complete, or not at all, exactly as after the single-connection path's one transaction; never an export with
    /// some of its changes, which an export run would send as it found it.
    /// </summary>
    /// <remarks>
    /// On any failure the call waits for every connection to finish, then removes what is left of it before the failure
    /// propagates: every export of the call's own ids that is still Incomplete, with its changes. By the call's ids, so
    /// nothing of another call is touched; whether or not a connection's commit was acknowledged, since a row whose commit
    /// was lost is still found. Still Incomplete, so an export another call holds under the same id (which is why this
    /// call's insert failed) is left alone, and nothing is removed once the last step has made the exports live. A Worker
    /// that stops part-way leaves Incomplete rows no reader sees; <see cref="RemoveIncompletePendingExportsAsync"/>
    /// removes them when it next starts.
    /// </remarks>
    /// <param name="exports">The exports to write.</param>
    /// <param name="changeRows">Their changes, each beside its export's id.</param>
    /// <param name="parallelism">The most connections the call may use.</param>
    /// <param name="besideCallerTransaction">Whether the caller holds a transaction the exports are made live in (#2848):
    /// see <see cref="CreatePendingExportsBesideCallerTransactionAsync"/>. The staging connections then give up on any lock
    /// after <see cref="StagingLockTimeout"/>, and a failure leaves the call's Incomplete rows to
    /// <see cref="RemovePendingExportsStagedForTransactionAsync"/>, since the caller's transaction may hold locks on them
    /// that a removal from here would wait on.</param>
    private async Task CreatePendingExportsInParallelAsync(
        List<PendingExport> exports,
        List<(Guid PendingExportId, PendingExportAttributeValueChange Change)> changeRows,
        int parallelism,
        bool besideCallerTransaction = false)
    {
        var connectionString = ConnectionStringForParallelWrites!;
        // Beside a caller's transaction, each step sizes itself as it would with none open; ConnectionsForWrite would keep
        // it on one connection.
        Func<int, int> connectionsFor = besideCallerTransaction ? ConnectionsForStagingBesideCallerTransaction : ConnectionsForWrite;
        Log.Information("CreatePendingExportsAsync: Writing {Count} Pending Exports and {ChangeCount} attribute value changes across {Parallelism} parallel connections",
            exports.Count, changeRows.Count, parallelism);

        var written = false;
        try
        {
            using (var exportsSpan = MvoActivitySource.StartActivity("PendingExport.Create.Exports"))
            {
                // Still on connections of its own, never the context's: each change's foreign key names its export, and
                // the changes are written on other connections, so every export must be committed first.
                var exportConnections = Math.Min(parallelism, connectionsFor(exports.Count));
                exportsSpan?.SetTag("rowCount", exports.Count);
                exportsSpan?.SetTag("connections", exportConnections);
                await ParallelBatchWriter.ExecuteAsync(exports, exportConnections, connectionString, async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    if (besideCallerTransaction)
                        await SetStagingLockTimeoutAsync(connection, transaction);
                    await BulkInsertPendingExportsOnConnectionAsync(connection, transaction, partition, incomplete: true);
                    await transaction.CommitAsync();
                });
            }

            await StepReachedAsync(StepExportsWritten);

            if (changeRows.Count > 0)
            {
                // Every export is committed now, so a change may belong to one any connection wrote.
                using var changesSpan = MvoActivitySource.StartActivity("PendingExport.Create.Changes");
                var changeConnections = Math.Min(parallelism, connectionsFor(changeRows.Count));
                changesSpan?.SetTag("rowCount", changeRows.Count);
                changesSpan?.SetTag("connections", changeConnections);
                await ParallelBatchWriter.ExecuteAsync(changeRows, changeConnections, connectionString, async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    if (besideCallerTransaction)
                        await SetStagingLockTimeoutAsync(connection, transaction);
                    await BulkInsertPendingExportAttributeValueChangesOnConnectionAsync(connection, transaction, partition);
                    await transaction.CommitAsync();
                });
            }

            await StepReachedAsync(StepChangesWritten);

            using (var liveSpan = MvoActivitySource.StartActivity("PendingExport.Create.Live"))
            {
                liveSpan?.SetTag("rowCount", exports.Count);
                await MakeIncompletePendingExportsLiveAsync(exports, besideCallerTransaction);
            }

            written = true;
        }
        finally
        {
            if (!written && !besideCallerTransaction)
            {
                await StepReachedAsync(StepRemovingFailedCall);
                await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(CreatePendingExportsAsync),
                    IncompletePendingExportRemovals([.. exports.Select(pe => pe.Id)]));
            }
        }
    }

    private Task StepReachedAsync(string step) => PendingExportCreateStepReached?.Invoke(step) ?? Task.CompletedTask;

    private static async Task SetStagingLockTimeoutAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL lock_timeout = '{StagingLockTimeout}'", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The last step of a parallel create: on the context's connection, in one transaction, every Incomplete export of the
    /// call takes its own status and Connected System Object, so all of them come into effect at once. Only rows still
    /// Incomplete are touched, and every one must be: a row missing (removed by another hand) fails the call, which then
    /// removes the rest. Taking the object can meet the unique index on it exactly where the single-connection insert
    /// would have.
    /// </summary>
    /// <remarks>
    /// The statement rewrites every export of the call, and since it changes indexed columns (the status and the object)
    /// none of those updates is HOT: each leaves a dead row version and a new entry in every index on the table. That is
    /// one per export per parallel call, cheap beside the changes on a group page, but a call with many exports and few
    /// changes (a deprovisioning wave of Deletes) passes the row gate too and pays it for every export.
    /// </remarks>
    /// <param name="exports">The call's exports.</param>
    /// <param name="inCallerTransaction">Whether to make them live in the transaction the caller holds (#2848), to come
    /// into effect when it commits, rather than in one of the call's own.</param>
    private async Task MakeIncompletePendingExportsLiveAsync(List<PendingExport> exports, bool inCallerTransaction)
    {
        await using var ownTransaction = inCallerTransaction ? null : await _context.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var npgsqlTransaction = (NpgsqlTransaction)_context.Database.CurrentTransaction!.GetDbTransaction();

        // A status mark and the one foreign key withheld while incomplete, set from values the call holds: the
        // targeted-mutation exemption of the raw SQL column-list rules, not a PendingExportBulkColumns writer.
        await using var command = new NpgsqlCommand(
            """
            UPDATE "PendingExports" AS pe
            SET "Status" = live.status, "ConnectedSystemObjectId" = live.cso_id
            FROM unnest(@ids, @statuses, @csoIds) AS live(id, status, cso_id)
            WHERE pe."Id" = live.id AND pe."Status" = @incomplete
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
        command.Parameters.Add(new NpgsqlParameter("incomplete", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportStatus.Incomplete });

        var madeLive = await command.ExecuteNonQueryAsync();
        if (madeLive != exports.Count)
            throw new InvalidOperationException(
                $"CreatePendingExportsAsync: {exports.Count} Pending Exports were written to be made live together, but only {madeLive} were still " +
                "waiting; nothing of the call is kept.");

        await StepReachedAsync(StepMakingLive);
        if (ownTransaction != null)
            await ownTransaction.CommitAsync();
    }

    /// <summary>
    /// What removing a failed parallel create takes: one statement deleting every export of the ids the call wrote under
    /// that is still Incomplete (see <see cref="CreatePendingExportsInParallelAsync"/> for why by every id and only
    /// Incomplete), its changes going by their foreign key's cascade. One statement, so its test of the status and its
    /// removal of the changes see the same rows: a statement of its own for the changes, as the other writers' removals
    /// make, would read the status a moment earlier, and could strip the changes from an export made live in between.
    /// </summary>
    private static ParallelWriteRemoval[] IncompletePendingExportRemovals(Guid[] exportIds) =>
        [new ParallelWriteRemoval("PendingExports", "Id", exportIds, $"\"Status\" = {(int)PendingExportStatus.Incomplete}")];

    /// <summary>
    /// Removes every Pending Export still <see cref="PendingExportStatus.Incomplete"/>, with its changes: what a Worker that
    /// stopped part-way through a parallel create left behind (#2919). For the Worker's start, when nothing should be
    /// writing one; no reader sees them meanwhile, and none holds a place in the unique index on its object.
    /// </summary>
    /// <remarks>
    /// One statement, the changes going by their foreign key's cascade. Should a call be making its exports live as this
    /// runs, the statement waits for it on their rows and then finds them no longer Incomplete, so they are left whole; a
    /// statement of its own for the changes would have read them as Incomplete a moment earlier, waited for nothing, and
    /// left the exports live without their changes.
    /// </remarks>
    /// <returns>The Pending Exports removed.</returns>
    public Task<int> RemoveIncompletePendingExportsAsync()
        => _context.Database.ExecuteSqlRawAsync("""DELETE FROM "PendingExports" WHERE "Status" = {0}""", (int)PendingExportStatus.Incomplete);
}
