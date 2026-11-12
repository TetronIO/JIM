// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    public async Task UpdateActivityProgressOutOfBandAsync(Activity activity)
    {
        // Open an independent connection to bypass any in-flight transaction on the main DbContext.
        // This ensures progress updates are immediately visible to other sessions (e.g., UI polling).
        var connectionString = _context.Database.GetConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE ""Activities"" SET ""ObjectsProcessed"" = @processed, ""ObjectsToProcess"" = @toProcess, ""Message"" = @message WHERE ""Id"" = @id";
        command.Parameters.AddWithValue("processed", activity.ObjectsProcessed);
        command.Parameters.AddWithValue("toProcess", activity.ObjectsToProcess);
        command.Parameters.AddWithValue("message", (object?)activity.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("id", activity.Id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis, IReadOnlySet<Guid>? rowsAlreadyWritten = null)
    {
        if (rpeis.Count == 0)
            return true;

        // Everything the write needs, collected and given its ids before anything is written: the items' ids, time-ordered
        // so the rows land on the growing end of each index (#2807); their outcome trees, flattened; their causal edges
        // (#1223) and ambiguous match candidates (#2052); the change records the outcomes carry; and the stat counter
        // deltas (#1078). The same collection, written by the same writer, is what a synchronisation's history write
        // takes (#2874).
        var batch = CollectExecutionItemHistory(rpeis, rowsAlreadyWritten, [], [], [], page: 0, totalPages: 0);
        var (rowsToInsert, rowsToUpdate) = SplitRowsAlreadyWritten(batch);
        var allOutcomes = AsList(batch.Outcomes);
        var outcomeCsoChanges = AsList(batch.OutcomeChangeRecords);

        // Increase command timeout for large RPEI persistence. With 5K+ RPEIs, the bulk
        // insert of RPEIs, sync outcomes, and CSO change records can exceed the default 30s.
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        // Parallel writes only for calls with rows enough to gain from them, counted as what the COPYs write (the items
        // and their outcomes), not as the number of items (#2872), across connections in proportion to them (#2669).
        var parallelism = ConnectionsForWrite(rowsToInsert.Count + allOutcomes.Count);

        if (parallelism > 1)
        {
            var connectionString = ConnectionStringForParallelWrites!;
            Log.Information("BulkInsertRpeisAsync: Writing {RpeiCount} RPEIs and {OutcomeCount} outcomes across {Parallelism} parallel connections",
                rpeis.Count, allOutcomes.Count, parallelism);

            // The items and their outcomes are written on connections of their own, each committing separately, and
            // the CSO change records the outcomes reference must commit before the outcomes can: the write is not one
            // transaction, as the single-connection path's is. A failure at any step therefore removes everything the
            // call committed before it propagates (#2872), so the caller finds nothing of the batch, as after the
            // single-connection path's rollback: the CSO change records, the outcomes, then the items, whose deletion
            // takes their causal edges and match candidates with them. Everything written on the main connection that
            // depends on nothing later (the rows written already, brought up to date; the edges; the candidates; the
            // stat counters) is written last, in one transaction, so a failure before it leaves none of it behind.
            var committedRpeiIds = new ConcurrentQueue<Guid>();
            var committedOutcomeIds = new ConcurrentQueue<Guid>();
            var csoChangesCommitted = false;
            var written = false;
            try
            {
                // Step 1: Insert RPEIs in parallel (no FK dependencies on other new rows)
                if (rowsToInsert.Count > 0)
                {
                    await ParallelBatchWriter.ExecuteAsync(
                        rowsToInsert,
                        parallelism,
                        connectionString,
                        async (connection, partition) =>
                        {
                            await using var tx = await connection!.BeginTransactionAsync();
                            await BulkInsertRpeisOnConnectionAsync(connection, tx, partition.ToList());
                            await tx.CommitAsync();
                            foreach (var rpei in partition)
                                committedRpeiIds.Enqueue(rpei.Id);
                        });
                }

                // Step 2: Persist CSO change records via raw SQL (same approach as PersistRpeiCsoChangesAsync), in a
                // transaction of their own that commits before the outcomes referencing them are written elsewhere.
                // EF AddRange + SaveChangesAsync is not safe here: it traverses navigation properties into
                // shared ConnectedSystemObjectTypeAttribute entities causing identity conflicts at scale,
                // and SaveChangesAsync flushes all dirty entities in the tracker (not just CSO changes). The outcomes
                // were pointed at the records' ids when the batch was collected.
                if (outcomeCsoChanges.Count > 0)
                {
                    await using (var changeTransaction = await _context.Database.BeginTransactionAsync())
                    {
                        await InsertCsoChangeHierarchyRawAsync(outcomeCsoChanges);
                        await changeTransaction.CommitAsync();
                    }

                    csoChangesCommitted = true;
                }

                // Step 3: Insert sync outcomes in parallel (RPEIs and CSO changes now exist).
                // Outcomes have a self-referencing FK (ParentSyncOutcomeId) forming a tree per RPEI.
                // We must partition by RPEI to keep each outcome tree on one connection; otherwise
                // a child on connection 2 may commit before its parent on connection 1, causing
                // an FK violation. An item carries a handful of outcomes, never one per value, so
                // partitioning by item is balanced.
                if (allOutcomes.Count > 0)
                {
                    var outcomesByRpei = allOutcomes
                        .GroupBy(o => o.ActivityRunProfileExecutionItemId)
                        .Select(g => g.ToList())
                        .ToList();

                    await ParallelBatchWriter.ExecuteAsync(
                        outcomesByRpei,
                        parallelism,
                        connectionString,
                        async (connection, partition) =>
                        {
                            var flatOutcomes = partition.SelectMany(g => g).ToList();
                            await using var tx = await connection!.BeginTransactionAsync();
                            await BulkInsertSyncOutcomesOnConnectionAsync(connection, tx, flatOutcomes);
                            await tx.CommitAsync();
                            foreach (var outcome in flatOutcomes)
                                committedOutcomeIds.Enqueue(outcome.Id);
                        });
                }

                // Step 4: on the main EF connection, in one transaction: the rows written already brought up to date
                // (their temporary table lives for one transaction), the causal edges and ambiguous match candidates
                // (#2052), whose only foreign key is to the RPEIs step 1 committed, and the batch's stat counter deltas.
                await using (var finalTransaction = await _context.Database.BeginTransactionAsync())
                {
                    if (rowsToUpdate.Count > 0)
                        await BulkUpdateRpeiFieldsRawAsync(rowsToUpdate);

                    if (batch.CausalEdges.Count > 0)
                        await BulkInsertCausalEdgesAsync(AsList(batch.CausalEdges));

                    if (batch.MatchCandidates.Count > 0)
                        await BulkInsertMatchCandidatesAsync(AsList(batch.MatchCandidates));

                    await ActivityStatCounterWriter.UpsertDeltasAsync(_context, batch.CounterDeltas);
                    await finalTransaction.CommitAsync();
                }

                written = true;
            }
            finally
            {
                // The outcomes by id as well as by cascade from their items, because those of the items a deletion's
                // transaction wrote already (#2807) belong to rows this call did not insert and must not remove.
                if (!written && (csoChangesCommitted || !committedRpeiIds.IsEmpty || !committedOutcomeIds.IsEmpty))
                {
                    await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(BulkInsertRpeisAsync),
                    [
                        new ParallelWriteRemoval("ConnectedSystemObjectChanges", "Id",
                            csoChangesCommitted ? [.. outcomeCsoChanges.Select(c => c.Id)] : []),
                        new ParallelWriteRemoval("ActivityRunProfileExecutionItemSyncOutcomes", "Id", [.. committedOutcomeIds]),
                        new ParallelWriteRemoval("ActivityRunProfileExecutionItems", "Id", [.. committedRpeiIds])
                    ]);
                }
            }
        }
        else
        {
            // Small batch: single-connection path, on the EF connection's own transaction, or the caller's when it holds
            // one. The same writer a synchronisation's history write uses (#2874): COPY binary for the item and outcome
            // rows, reusing the on-connection writers the parallel branch above uses, which is markedly faster than
            // parameterised multi-row INSERT even on a single connection.
            IDbContextTransaction? transaction = null;
            var existingTransaction = _context.Database.CurrentTransaction;
            if (existingTransaction == null)
                transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                await WriteExecutionItemHistoryAsync(batch);

                if (transaction != null)
                    await transaction.CommitAsync();
            }
            finally
            {
                if (transaction != null)
                    await transaction.DisposeAsync();
            }
        }

        // Empty the edge and candidate buffers now they are written, so a later flush of the same RPEI objects
        // (confirming imports revisit them) cannot re-insert the same rows and fail on the primary key. Only reached on
        // success: a throw above leaves the buffers intact, which is what a retry needs.
        DrainCausalEdgeAndMatchCandidateBuffers(rpeis);

        _context.Database.SetCommandTimeout(previousTimeout);
        return true; // Raw SQL used — RPEIs persisted outside EF change tracker
    }

    /// <summary>
    /// Collects what a write of the given execution items records, and assigns every id it needs (#2874): the items'
    /// own (time-ordered, #2807), their outcome trees' (flattened, each node pointed at its item and parent), the change
    /// records the outcomes carry (and the outcomes pointed at them), their causal edges' (each reference resolved to an
    /// id, #1223) and their match candidates' (#2052); then the stat counter deltas (#1078), read while the items still
    /// name the objects they describe. Writes nothing, and leaves the items' buffers as they are.
    /// </summary>
    private static SynchronisationHistoryBatch CollectExecutionItemHistory(
        IReadOnlyList<ActivityRunProfileExecutionItem> rpeis,
        IReadOnlySet<Guid>? rowsAlreadyWritten,
        IReadOnlyList<ConnectedSystemObjectChange> itemChangeRecords,
        IReadOnlyList<MetaverseObjectChange> newMetaverseObjectChanges,
        IReadOnlyList<MetaverseObjectChange> metaverseObjectChangeAppends,
        int page,
        int totalPages)
    {
        foreach (var rpei in rpeis.Where(r => r.Id == Guid.Empty))
            rpei.Id = TimeOrderedIds.New();

        var allOutcomes = rpeis.SelectMany(FlattenSyncOutcomes).ToList();

        // Causal edges buffered on the RPEIs by the cascade seams (#1223). Collected here, after the RPEI and
        // outcome ids have been assigned, because a seam knows neither: an RPEI is created without an id and an
        // outcome likewise, so the edge names its effect by reference and the collection resolves both, mirroring
        // how an outcome's own ConnectedSystemObjectChangeId is resolved below.
        var allEdges = new List<CausalEdge>();
        foreach (var rpei in rpeis.Where(r => r.CausalEdges.Count > 0))
        {
            foreach (var edge in rpei.CausalEdges)
                edge.ResolveTransientReferences(rpei.Id);

            allEdges.AddRange(rpei.CausalEdges);
        }

        // Ambiguous match candidates buffered on the RPEIs (#2052), written beside the edges for the same reason.
        var allCandidates = CollectMatchCandidates(rpeis);

        // The change records the outcomes carry (PendingExportCreated snapshots), given their ids now (time-ordered, so
        // the rows land on the growing end of each index, #2669) so each outcome can name its record by id.
        var outcomeCsoChanges = allOutcomes
            .Where(o => o.ConnectedSystemObjectChange != null)
            .Select(o => o.ConnectedSystemObjectChange!)
            .ToList();
        foreach (var change in outcomeCsoChanges)
            TimeOrderedIds.AssignMissing(change);
        foreach (var outcome in allOutcomes.Where(o => o.ConnectedSystemObjectChange != null))
            outcome.ConnectedSystemObjectChangeId = outcome.ConnectedSystemObjectChange!.Id;

        return new SynchronisationHistoryBatch
        {
            Items = rpeis,
            ItemIdsWrittenWithDeletion = rowsAlreadyWritten ?? new HashSet<Guid>(),
            Outcomes = allOutcomes,
            OutcomeChangeRecords = outcomeCsoChanges,
            ItemChangeRecords = itemChangeRecords,
            CausalEdges = allEdges,
            MatchCandidates = allCandidates,
            MetaverseObjectChanges = newMetaverseObjectChanges,
            MetaverseObjectChangeAppends = metaverseObjectChangeAppends,
            // Stat counter deltas for this batch (#1078): computed once from the in-memory RPEIs and outcomes, upserted
            // alongside the batch so the Activity stats read stays O(counter rows).
            CounterDeltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, allOutcomes),
            Page = page,
            TotalPages = totalPages
        };
    }

    /// <summary>
    /// The batch's items split into those to insert and those a deletion's transaction wrote already (#2807), whose rows
    /// are only brought up to date, since outcomes were added after the delete.
    /// </summary>
    private static (List<ActivityRunProfileExecutionItem> RowsToInsert, List<ActivityRunProfileExecutionItem> RowsToUpdate) SplitRowsAlreadyWritten(
        SynchronisationHistoryBatch batch)
    {
        if (batch.ItemIdsWrittenWithDeletion.Count == 0)
            return (AsList(batch.Items), []);

        return (batch.Items.Where(r => !batch.ItemIdsWrittenWithDeletion.Contains(r.Id)).ToList(),
            batch.Items.Where(r => batch.ItemIdsWrittenWithDeletion.Contains(r.Id)).ToList());
    }

    /// <summary>
    /// Writes the execution item part of a collected batch on the context's connection, inside the transaction the
    /// caller holds: the items' rows (and the rows a deletion wrote already, brought up to date), the change records the
    /// outcomes carry, the outcomes, the causal edges, the match candidates and the stat counter deltas, in that order,
    /// each written after what it references.
    /// </summary>
    private async Task WriteExecutionItemHistoryAsync(SynchronisationHistoryBatch batch)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction)(_context.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("WriteExecutionItemHistoryAsync: the caller must hold the transaction the rows are written in."));
        var (rowsToInsert, rowsToUpdate) = SplitRowsAlreadyWritten(batch);

        using (var rowsSpan = MvoActivitySource.StartActivity("RpeiHistory.Rows"))
        {
            rowsSpan?.SetTag("rowCount", rowsToInsert.Count);
            rowsSpan?.SetTag("updatedRowCount", rowsToUpdate.Count);
            if (rowsToInsert.Count > 0)
                await BulkInsertRpeisOnConnectionAsync(npgsqlConn, npgsqlTx, rowsToInsert);

            // The rows a deletion's transaction wrote already, brought up to date (#2807).
            if (rowsToUpdate.Count > 0)
                await BulkUpdateRpeiFieldsRawAsync(rowsToUpdate);
        }

        if (batch.OutcomeChangeRecords.Count > 0)
        {
            // Raw SQL rather than EF AddRange: see the parallel path's comment in BulkInsertRpeisAsync.
            using var changesSpan = MvoActivitySource.StartActivity("RpeiHistory.CsoChanges");
            changesSpan?.SetTag("rowCount", batch.OutcomeChangeRecords.Count);
            await InsertCsoChangeHierarchyRawAsync(AsList(batch.OutcomeChangeRecords), batch.ConnectedSystemObjectReferencesFound);
        }

        if (batch.Outcomes.Count > 0)
        {
            using var outcomesSpan = MvoActivitySource.StartActivity("RpeiHistory.Outcomes");
            outcomesSpan?.SetTag("rowCount", batch.Outcomes.Count);
            await BulkInsertSyncOutcomesOnConnectionAsync(npgsqlConn, npgsqlTx, AsList(batch.Outcomes));
        }

        if (batch.CausalEdges.Count > 0)
        {
            using var edgesSpan = MvoActivitySource.StartActivity("RpeiHistory.Edges");
            edgesSpan?.SetTag("rowCount", batch.CausalEdges.Count);
            await BulkInsertCausalEdgesAsync(AsList(batch.CausalEdges));
        }

        if (batch.MatchCandidates.Count > 0)
        {
            using var candidatesSpan = MvoActivitySource.StartActivity("RpeiHistory.Candidates");
            candidatesSpan?.SetTag("rowCount", batch.MatchCandidates.Count);
            await BulkInsertMatchCandidatesAsync(AsList(batch.MatchCandidates));
        }

        using (var countersSpan = MvoActivitySource.StartActivity("RpeiHistory.Counters"))
        {
            countersSpan?.SetTag("rowCount", batch.CounterDeltas.Count);
            await ActivityStatCounterWriter.UpsertDeltasAsync(_context, batch.CounterDeltas);
        }
    }

    /// <summary>
    /// Empties the items' causal edge and match candidate buffers once what they held is written, or owned by a batch,
    /// so a later flush of the same items (confirming imports revisit them) cannot write them a second time.
    /// </summary>
    private static void DrainCausalEdgeAndMatchCandidateBuffers(IEnumerable<ActivityRunProfileExecutionItem> rpeis)
    {
        foreach (var rpei in rpeis)
        {
            if (rpei.CausalEdges.Count > 0)
                rpei.CausalEdges.Clear();
            if (rpei.MatchCandidates.Count > 0)
                rpei.MatchCandidates.Clear();
        }
    }

    private static List<T> AsList<T>(IReadOnlyList<T> source) => source as List<T> ?? [.. source];

    /// <inheritdoc />
    public async Task InsertRpeiRowsAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        if (rpeis.Count == 0)
            return;

        foreach (var rpei in rpeis)
        {
            if (rpei.ActivityId == Guid.Empty)
                throw new InvalidOperationException(
                    "InsertRpeiRowsAsync: every execution item must carry its Activity's id before its row is written.");
            if (rpei.Id == Guid.Empty)
                rpei.Id = TimeOrderedIds.New();
        }

        // On the context's own connection, so the rows join whatever transaction the caller holds: a deletion's, whose
        // change records name these items (#2807). Without one, the rows get a transaction of their own.
        await using var transaction = _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync()
            : null;
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction)_context.Database.CurrentTransaction!.GetDbTransaction();
        await BulkInsertRpeisOnConnectionAsync(npgsqlConn, npgsqlTx, rpeis);
        if (transaction != null)
            await transaction.CommitAsync();
    }

    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis)
    {
        foreach (var rpei in rpeis)
        {
            try
            {
                var entry = _context.Entry(rpei);
                if (entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;
            }
            catch (NullReferenceException)
            {
                // Mocked DbContext in tests — Entry() not available, nothing to detach
            }
        }
    }

    /// <summary>
    /// Inserts a CSO change record hierarchy (parent + attribute changes + value changes) via raw SQL.
    /// Pre-generates IDs so FK relationships are known before building SQL statements.
    /// This avoids EF AddRange graph traversal which causes identity conflicts with shared
    /// ConnectedSystemObjectTypeAttribute entities at scale.
    /// </summary>
    /// <param name="changes">The change records.</param>
    /// <param name="referencesFound">The referenced Connected System Objects found to exist when a synchronisation history
    /// batch was handed off (#2924); null to look them up as the values are written.</param>
    private async Task InsertCsoChangeHierarchyRawAsync(List<ConnectedSystemObjectChange> changes, IReadOnlySet<Guid>? referencesFound = null)
    {
        // Pre-generate IDs for all entities in the hierarchy; time-ordered, so the rows land on
        // the growing end of each index rather than a random page of it (#2669).
        foreach (var change in changes)
            TimeOrderedIds.AssignMissing(change);

        // Step 1: INSERT parent ConnectedSystemObjectChange rows
        await BulkInsertCsoChangesRawAsync(changes);

        // Step 2: INSERT ConnectedSystemObjectChangeAttribute rows
        var allAttrChanges = changes
            .SelectMany(c => c.AttributeChanges.Select(ac =>
                (ChangeId: c.Id, AttributeId: RequiredAttributeId(ac), AttrChange: ac)))
            .ToList();

        if (allAttrChanges.Count > 0)
            await BulkInsertCsoChangeAttributesRawAsync(allAttrChanges);

        // Step 3: INSERT ConnectedSystemObjectChangeAttributeValue rows
        var allValueChanges = changes
            .SelectMany(c => c.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => (AttrChange: ac, Value: vc))))
            .ToList();

        if (allValueChanges.Count > 0)
            await BulkInsertCsoChangeAttributeValuesRawAsync(allValueChanges, referencesFound);
    }

    /// <inheritdoc />
    public Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis, bool allowParallelWrite = false)
    {
        var changes = rpeis
            .Where(r => r.ConnectedSystemObjectChange != null)
            .Select(r => r.ConnectedSystemObjectChange!)
            .ToList();

        return PersistCsoChangesAsync(changes, allowParallelWrite);
    }

    /// <inheritdoc />
    public async Task PersistCsoChangesAsync(List<ConnectedSystemObjectChange> changes, bool allowParallelWrite = false)
    {
        if (changes.Count == 0)
            return;

        // Pre-generate IDs for all entities in the graph so FK relationships are known
        // before we build the raw SQL statements; time-ordered, so the rows land on the growing
        // end of each index rather than a random page of it (#2669).
        foreach (var change in changes)
            TimeOrderedIds.AssignMissing(change);

        var attributeRows = CsoChangeAttributeRows(changes);
        var valueRows = CsoChangeValueRows(changes);

        using var persistSpan = MvoActivitySource.StartActivity("CsoChange.Persist");
        persistSpan?.SetTag("changeCount", changes.Count);
        persistSpan?.SetTag("attributeCount", attributeRows.Count);
        persistSpan?.SetTag("valueCount", valueRows.Count);

        // Gated on the rows written, as the other bulk writers are (#2872). A change record of a large group carries a
        // value row per member, which on one connection took 16.8 s of two Full Import batches while the same rows'
        // attribute values were written across 16 (#2909). Only for a caller that asks: one writing several batches at
        // once (an export) would take the parallelism's share of the connection pool for each of them.
        var connections = allowParallelWrite ? ConnectionsForWrite(changes.Count + attributeRows.Count + valueRows.Count) : 1;
        if (connections > 1)
        {
            persistSpan?.SetTag("connections", connections);
            await PersistCsoChangesInParallelAsync(changes, attributeRows, valueRows, connections);
            return;
        }

        persistSpan?.SetTag("connections", 1);

        // Increase command timeout for large change record persistence. With 5K+ CSOs and
        // 20+ attributes each, the three-table bulk insert can generate 100K+ rows.
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        // Use an EF transaction for atomicity — COPY binary imports on the underlying
        // NpgsqlConnection participate in the active transaction automatically. A caller already inside a transaction
        // keeps it and this joins it, as the other bulk writers do (Npgsql does not nest transactions).
        await using var transaction = _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync()
            : null;

        await WriteCsoChangeRecordsAsync(changes);

        if (transaction != null)
            await transaction.CommitAsync();
        _context.Database.SetCommandTimeout(previousTimeout);
    }

    /// <summary>
    /// The parallel path of <see cref="PersistCsoChangesAsync"/> (#2909): the change rows on one connection, then their
    /// attribute rows and value rows each partitioned by row across the parallel connections, as the Connected System
    /// Object create partitions its values (#2872). Each phase commits before the next, since each row's foreign key is to
    /// a row of the phase before. The phases commit separately, so a failure in any of them removes the change rows the
    /// call committed, and with them (by their cascade) every attribute and value row written under them, before it
    /// propagates: the caller finds nothing of the write, as after the single-connection path's rollback.
    /// </summary>
    private async Task PersistCsoChangesInParallelAsync(
        List<ConnectedSystemObjectChange> changes,
        List<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> attributeRows,
        List<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> valueRows,
        int parallelism)
    {
        var connectionString = ConnectionStringForParallelWrites!;
        Log.Information("PersistCsoChangesAsync: Writing {ChangeCount} change records, {AttributeCount} attribute changes and {ValueCount} value changes across {Parallelism} parallel connections",
            changes.Count, attributeRows.Count, valueRows.Count, parallelism);

        var changesCommitted = false;
        var written = false;
        try
        {
            HashSet<Guid> writtenReferenceIds;
            using (var changesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Changes"))
            {
                changesSpan?.SetTag("rowCount", changes.Count);
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                // The referenced objects whose rows exist now, read before the values are written, as the
                // single-connection writer reads them (see BulkInsertCsoChangeAttributeValuesRawAsync).
                writtenReferenceIds = await GetExistingConnectedSystemObjectIdsAsync(connection, null, ReferencedConnectedSystemObjectIds(valueRows));

                await using var transaction = await connection.BeginTransactionAsync();
                await BulkInsertCsoChangesOnConnectionAsync(connection, changes);
                await transaction.CommitAsync();
            }

            changesCommitted = true;
            await WriteCsoChangeAttributesAndValuesInParallelAsync(attributeRows, valueRows, writtenReferenceIds, parallelism, connectionString);
            written = true;
        }
        finally
        {
            if (!written && changesCommitted)
                await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(PersistCsoChangesAsync),
                    [CsoChangeRecordRemoval(changes)]);
        }
    }

    /// <summary>
    /// Writes change records' attribute rows, then their value rows, each partitioned by row across up to
    /// <paramref name="parallelism"/> connections of their own, each connection committing its own part (#2909). The
    /// change rows they belong to must be committed already. Value rows are partitioned by row, not by change, so a large
    /// group's members spread across every connection: its attribute row is committed before any of them is written.
    /// </summary>
    /// <param name="writtenReferenceIds">The referenced Connected System Objects whose ids are written; see
    /// <see cref="BulkInsertCsoChangeAttributeValuesOnConnectionAsync"/>.</param>
    private static async Task WriteCsoChangeAttributesAndValuesInParallelAsync(
        IReadOnlyList<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> attributeRows,
        IReadOnlyList<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> valueRows,
        IReadOnlySet<Guid> writtenReferenceIds,
        int parallelism,
        string connectionString)
    {
        using (var attributesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Attributes"))
        {
            attributesSpan?.SetTag("rowCount", attributeRows.Count);
            attributesSpan?.SetTag("connections", Math.Min(parallelism, attributeRows.Count));
            await ParallelBatchWriter.ExecuteAsync(attributeRows, parallelism, connectionString, async (connection, partition) =>
            {
                await using var transaction = await connection!.BeginTransactionAsync();
                await BulkInsertCsoChangeAttributesOnConnectionAsync(connection, partition);
                await transaction.CommitAsync();
            });
        }

        using (var valuesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Values"))
        {
            valuesSpan?.SetTag("rowCount", valueRows.Count);
            valuesSpan?.SetTag("connections", Math.Min(parallelism, valueRows.Count));
            await ParallelBatchWriter.ExecuteAsync(valueRows, parallelism, connectionString, async (connection, partition) =>
            {
                await using var transaction = await connection!.BeginTransactionAsync();
                await BulkInsertCsoChangeAttributeValuesOnConnectionAsync(connection, partition, writtenReferenceIds);
                await transaction.CommitAsync();
            });
        }
    }

    /// <summary>
    /// Removing change records a failed parallel write committed: by their own ids, which takes their attribute and value
    /// rows with them by the cascade those rows' foreign keys carry.
    /// </summary>
    private static ParallelWriteRemoval CsoChangeRecordRemoval(IEnumerable<ConnectedSystemObjectChange> changes) =>
        new("ConnectedSystemObjectChanges", "Id", [.. changes.Select(c => c.Id)]);

    /// <summary>
    /// The attribute rows of the given change records, each naming its change and attribute, change by change.
    /// </summary>
    private static List<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> CsoChangeAttributeRows(
        IEnumerable<ConnectedSystemObjectChange> changes) =>
        changes
            .SelectMany(c => c.AttributeChanges.Select(ac => (ChangeId: c.Id, AttributeId: RequiredAttributeId(ac), AttrChange: ac)))
            .ToList();

    /// <summary>
    /// The attribute a Connected System Object change record's attribute row names, by plain id (#2931). Every such row
    /// is built from a loaded attribute, so one naming none is a fault in its builder: refused, rather than written as a
    /// row no reader could place, now that no foreign key would refuse it.
    /// </summary>
    private static int RequiredAttributeId(ConnectedSystemObjectChangeAttribute attributeChange) =>
        attributeChange.AttributeIdForWrite() ?? throw new InvalidOperationException(
            $"The change record attribute row '{attributeChange.AttributeName}' names no attribute; a Connected System Object " +
            "change record's attribute rows are built from a loaded attribute.");

    /// <summary>
    /// The value rows of the given change records, each with the attribute row it belongs to, change by change and
    /// attribute by attribute.
    /// </summary>
    private static List<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> CsoChangeValueRows(
        IEnumerable<ConnectedSystemObjectChange> changes) =>
        changes
            .SelectMany(c => c.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => (AttrChange: ac, Value: vc))))
            .ToList();

    /// <summary>
    /// Writes Connected System Object change records whose ids are all assigned, with their attribute and value rows, in
    /// the transaction the caller holds: <see cref="PersistCsoChangesAsync"/>'s, a synchronisation history write's
    /// (#2874), or an import create batch's (#2909).
    /// </summary>
    /// <param name="changes">The change records.</param>
    /// <param name="referencesFound">The referenced Connected System Objects found to exist when a synchronisation history
    /// batch was handed off (#2924); null to look them up as the values are written.</param>
    private async Task WriteCsoChangeRecordsAsync(IReadOnlyList<ConnectedSystemObjectChange> changes, IReadOnlySet<Guid>? referencesFound = null)
    {
        // Step 1: INSERT parent ConnectedSystemObjectChange rows
        using (var changesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Changes"))
        {
            changesSpan?.SetTag("rowCount", changes.Count);
            await BulkInsertCsoChangesRawAsync(AsList(changes));
        }

        // Step 2: INSERT ConnectedSystemObjectChangeAttribute rows
        var allAttrChanges = CsoChangeAttributeRows(changes);
        if (allAttrChanges.Count > 0)
        {
            using var attributesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Attributes");
            attributesSpan?.SetTag("rowCount", allAttrChanges.Count);
            attributesSpan?.SetTag("connections", 1);
            await BulkInsertCsoChangeAttributesRawAsync(allAttrChanges);
        }

        // Step 3: INSERT ConnectedSystemObjectChangeAttributeValue rows
        var allValueChanges = CsoChangeValueRows(changes);
        if (allValueChanges.Count > 0)
        {
            using var valuesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Values");
            valuesSpan?.SetTag("rowCount", allValueChanges.Count);
            valuesSpan?.SetTag("connections", 1);
            await BulkInsertCsoChangeAttributeValuesRawAsync(allValueChanges, referencesFound);
        }
    }

    /// <remarks>
    /// Replaces the earlier two-call pattern (EF-projected RPEIs + Include(SyncOutcomes),
    /// followed by a separate raw-SQL RPEI→MvoChange-id map lookup) with a single
    /// <see cref="NpgsqlBatch"/> that ships two statements in one network round-trip:
    /// <list type="number">
    ///   <item>RPEI scalars LEFT JOINed to <c>MetaverseObjectChanges</c> (one row per RPEI).</item>
    ///   <item>Matching <c>ActivityRunProfileExecutionItemSyncOutcomes</c> stitched onto the RPEIs in memory.</item>
    /// </list>
    /// Measured sibling comparison (<c>GetRpeiToMvoChangeIdMapAsync</c>) showed raw Npgsql ~3.5×
    /// faster than EF projection on the same workload; the gap widens with row count because
    /// EF materialisation scales harder than the query itself. Carries only the columns the
    /// worker actually reads and mutates — no navigation properties, no change tracking.
    /// </remarks>
    public async Task<List<CrossPageMergeRpei>> GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
        Guid activityId, IReadOnlyCollection<Guid> csoIds)
    {
        if (csoIds.Count == 0)
            return [];

        var csoIdArray = csoIds as Guid[] ?? csoIds.ToArray();

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        // Ship both statements in one round-trip. Statement 1 = RPEI scalars LEFT JOIN
        // MvoChange.Id (one row per RPEI). Statement 2 = the SyncOutcomes for those RPEIs
        // (stitched in memory below). Both filter by the same (ActivityId, CsoIds) predicate
        // so we can parameterise them independently.
        await using var batch = new NpgsqlBatch(npgsqlConn, npgsqlTx);

        var rpeiCommand = new NpgsqlBatchCommand(
            """
            SELECT rpei."Id", rpei."ActivityId", rpei."ObjectChangeType", rpei."NoChangeReason",
                   rpei."ConnectedSystemObjectId", rpei."ExternalIdSnapshot", rpei."DisplayNameSnapshot",
                   rpei."ObjectTypeSnapshot", rpei."ErrorType", rpei."ErrorMessage", rpei."ErrorStackTrace",
                   rpei."AttributeFlowCount", rpei."OutcomeSummary",
                   mvoc."Id" AS "MvoChangeId"
            FROM "ActivityRunProfileExecutionItems" rpei
            LEFT JOIN "MetaverseObjectChanges" mvoc
                ON mvoc."ActivityRunProfileExecutionItemId" = rpei."Id"
            WHERE rpei."ActivityId" = @activityId
              AND rpei."ConnectedSystemObjectId" = ANY(@csoIds)
            """);
        rpeiCommand.Parameters.Add(new NpgsqlParameter("activityId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = activityId });
        rpeiCommand.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = csoIdArray });
        batch.BatchCommands.Add(rpeiCommand);

        var outcomesCommand = new NpgsqlBatchCommand(
            """
            SELECT so."Id", so."ActivityRunProfileExecutionItemId", so."ParentSyncOutcomeId",
                   so."OutcomeType", so."TargetEntityId", so."TargetEntityDescription",
                   so."DetailCount", so."DetailMessage", so."Ordinal", so."ConnectedSystemObjectChangeId",
                   so."SyncRuleId", so."SyncRuleName", so."StagedChangeType",
                   so."ObjectMatchingRuleId", so."ObjectMatchingRuleDescriptionSnapshot", so."MatchedValue"
            FROM "ActivityRunProfileExecutionItemSyncOutcomes" so
            INNER JOIN "ActivityRunProfileExecutionItems" rpei
                ON rpei."Id" = so."ActivityRunProfileExecutionItemId"
            WHERE rpei."ActivityId" = @activityId
              AND rpei."ConnectedSystemObjectId" = ANY(@csoIds)
            """);
        outcomesCommand.Parameters.Add(new NpgsqlParameter("activityId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = activityId });
        outcomesCommand.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = csoIdArray });
        batch.BatchCommands.Add(outcomesCommand);

        var resultsByRpeiId = new Dictionary<Guid, CrossPageMergeRpei>();
        var results = new List<CrossPageMergeRpei>();

        await using var reader = await batch.ExecuteReaderAsync();

        // Result set 1 — RPEI row per RPEI (LEFT JOIN guarantees one row per RPEI since the
        // MvoChange→RPEI FK is unique).
        while (await reader.ReadAsync())
        {
            var rpei = new ActivityRunProfileExecutionItem
            {
                Id = reader.GetGuid(0),
                ActivityId = reader.GetGuid(1),
                ObjectChangeType = (ObjectChangeType)reader.GetInt32(2),
                NoChangeReason = reader.IsDBNull(3) ? null : (NoChangeReason)reader.GetInt32(3),
                ConnectedSystemObjectId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                ExternalIdSnapshot = reader.IsDBNull(5) ? null : reader.GetString(5),
                DisplayNameSnapshot = reader.IsDBNull(6) ? null : reader.GetString(6),
                ObjectTypeSnapshot = reader.IsDBNull(7) ? null : reader.GetString(7),
                ErrorType = reader.IsDBNull(8) ? null : (ActivityRunProfileExecutionItemErrorType)reader.GetInt32(8),
                ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
                ErrorStackTrace = reader.IsDBNull(10) ? null : reader.GetString(10),
                AttributeFlowCount = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                OutcomeSummary = reader.IsDBNull(12) ? null : reader.GetString(12)
            };
            var existingMvoChangeId = reader.IsDBNull(13) ? (Guid?)null : reader.GetGuid(13);

            var entry = new CrossPageMergeRpei
            {
                Rpei = rpei,
                ExistingMvoChangeId = existingMvoChangeId
            };
            results.Add(entry);
            resultsByRpeiId[rpei.Id] = entry;
        }

        // Result set 2 — SyncOutcome rows, attached to their parent RPEI.
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            var rpeiId = reader.GetGuid(1);
            if (!resultsByRpeiId.TryGetValue(rpeiId, out var entry))
                continue;

            entry.Rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
            {
                Id = reader.GetGuid(0),
                ActivityRunProfileExecutionItemId = rpeiId,
                ParentSyncOutcomeId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                OutcomeType = (ActivityRunProfileExecutionItemSyncOutcomeType)reader.GetInt32(3),
                TargetEntityId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                TargetEntityDescription = reader.IsDBNull(5) ? null : reader.GetString(5),
                DetailCount = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                DetailMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                Ordinal = reader.GetInt32(8),
                ConnectedSystemObjectChangeId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
                SyncRuleId = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                SyncRuleName = reader.IsDBNull(11) ? null : reader.GetString(11),
                StagedChangeType = reader.IsDBNull(12) ? null : (PendingExportChangeType)reader.GetInt32(12),
                ObjectMatchingRuleId = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                ObjectMatchingRuleDescriptionSnapshot = reader.IsDBNull(14) ? null : reader.GetString(14),
                MatchedValue = reader.IsDBNull(15) ? null : reader.GetString(15)
            });
        }

        return results;
    }

    public async Task BulkUpdateRpeiOutcomesAsync(
        List<ActivityRunProfileExecutionItem> rpeis,
        List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
    {
        if (rpeis.Count == 0)
            return;

        IDbContextTransaction? transaction = null;
        var existingTransaction = _context.Database.CurrentTransaction;
        if (existingTransaction == null)
            transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // Bulk UPDATE OutcomeSummary and error fields on existing RPEIs
            await BulkUpdateRpeiFieldsRawAsync(rpeis);

            // Bulk INSERT new sync outcomes, counting them into the stat counters (#1078).
            // The field updates above are not counter-adjusted: post-insert ErrorType changes
            // drift the advisory in-flight counters slightly and completion-time finalisation
            // reconciles them exactly.
            if (newOutcomes.Count > 0)
            {
                await BulkInsertSyncOutcomesRawAsync(newOutcomes);
                await ActivityStatCounterWriter.UpsertDeltasAsync(
                    _context, ActivityStatCounterCalculator.CalculateOutcomeInsertDeltas(rpeis, newOutcomes));
            }

            // Drain any causal edges buffered on these items (#1223). Confirming imports merge their outcomes
            // onto already-persisted items through here rather than through either insert path, so an edge
            // written at the export-confirmation seam reaches the database only if this path writes it. The
            // items already have ids; the outcomes were assigned theirs above.
            var edges = new List<CausalEdge>();
            foreach (var rpei in rpeis.Where(r => r.CausalEdges.Count > 0))
            {
                foreach (var edge in rpei.CausalEdges)
                    edge.ResolveTransientReferences(rpei.Id);

                edges.AddRange(rpei.CausalEdges);
            }

            if (edges.Count > 0)
                await BulkInsertCausalEdgesAsync(edges);

            var candidates = CollectMatchCandidates(rpeis);
            if (candidates.Count > 0)
                await BulkInsertMatchCandidatesAsync(candidates);

            if (transaction != null)
                await transaction.CommitAsync();

            // Emptied only once committed, so a rolled-back update leaves them to be retried.
            foreach (var rpei in rpeis.Where(r => r.CausalEdges.Count > 0))
                rpei.CausalEdges.Clear();
            foreach (var rpei in rpeis.Where(r => r.MatchCandidates.Count > 0))
                rpei.MatchCandidates.Clear();
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    #region Private RPEI Bulk Helpers

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChange rows using COPY binary import, on the context's connection.
    /// </summary>
    private async Task BulkInsertCsoChangesRawAsync(List<ConnectedSystemObjectChange> changes)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        await BulkInsertCsoChangesOnConnectionAsync(npgsqlConn, changes);
    }

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChange rows using COPY binary import, on the given connection inside whatever
    /// transaction it holds: the context's own, or a connection of a parallel write's own (#2909).
    /// </summary>
    private static async Task BulkInsertCsoChangesOnConnectionAsync(NpgsqlConnection connection, IReadOnlyList<ConnectedSystemObjectChange> changes)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ConnectedSystemObjectChanges" (
                {BulkSqlHelpers.ToQuotedList(CsoChangeBulkColumns.ConnectedSystemObjectChanges)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var c in changes)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(c.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            if (c.ActivityRunProfileExecutionItemId.HasValue)
                await writer.WriteAsync(c.ActivityRunProfileExecutionItemId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(c.ConnectedSystemId, NpgsqlTypes.NpgsqlDbType.Integer);
            if (c.ConnectedSystemObjectId.HasValue)
                await writer.WriteAsync(c.ConnectedSystemObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(c.ChangeTime, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync((int)c.ChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)c.InitiatedByType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (c.InitiatedById.HasValue)
                await writer.WriteAsync(c.InitiatedById.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (c.InitiatedByName is not null)
                await writer.WriteAsync(c.InitiatedByName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (c.DeletedObjectType?.Id is { } deletedTypeId)
                await writer.WriteAsync(deletedTypeId, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (c.DeletedObjectExternalId is not null)
                await writer.WriteAsync(c.DeletedObjectExternalId, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (c.DeletedObjectDisplayName is not null)
                await writer.WriteAsync(c.DeletedObjectDisplayName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (c.DeletedConnectedSystemObjectId is { } deletedCsoId)
                await writer.WriteAsync(deletedCsoId, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChangeAttribute rows using COPY binary import, on the context's connection.
    /// </summary>
    private async Task BulkInsertCsoChangeAttributesRawAsync(List<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> attrChanges)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        await BulkInsertCsoChangeAttributesOnConnectionAsync(npgsqlConn, attrChanges);
    }

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChangeAttribute rows using COPY binary import, on the given connection inside
    /// whatever transaction it holds.
    /// </summary>
    private static async Task BulkInsertCsoChangeAttributesOnConnectionAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> attrChanges)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ConnectedSystemObjectChangeAttributes" ({BulkSqlHelpers.ToQuotedList(CsoChangeBulkColumns.ConnectedSystemObjectChangeAttributes)})
            FROM STDIN (FORMAT binary)
            """);

        foreach (var (changeId, attributeId, attrChange) in attrChanges)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(attrChange.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(changeId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(attributeId, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(attrChange.AttributeName, NpgsqlTypes.NpgsqlDbType.Text);
            await writer.WriteAsync((int)attrChange.AttributeType, NpgsqlTypes.NpgsqlDbType.Integer);
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Which of the given Connected System Object ids have a row, read on the given connection inside the given
    /// transaction (if any), so rows that transaction wrote count.
    /// </summary>
    private static async Task<HashSet<Guid>> GetExistingConnectedSystemObjectIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IEnumerable<Guid> candidateIds)
    {
        var candidates = candidateIds.Distinct().ToArray();
        if (candidates.Length == 0)
            return [];

        await using var command = new NpgsqlCommand(
            """SELECT "Id" FROM "ConnectedSystemObjects" WHERE "Id" = ANY(@ids)""", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = candidates });

        var existing = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            existing.Add(reader.GetGuid(0));
        return existing;
    }

    /// <summary>
    /// The Connected System Objects the given change values reference by id: the candidates for having their id written.
    /// </summary>
    private static IEnumerable<Guid> ReferencedConnectedSystemObjectIds(
        IEnumerable<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> valueChanges) =>
        valueChanges
            .Select(vc => vc.Value.ReferenceValueId)
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value);

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChangeAttributeValue rows using COPY binary import, each under the attribute
    /// change it belongs to, whose recorded data type decides whether the value is marked as a reference still to
    /// resolve.
    /// </summary>
    /// <param name="valueChanges">The value rows, each under its attribute change.</param>
    /// <param name="referencesFound">The referenced Connected System Objects found to exist when a synchronisation history
    /// batch was handed off (#2924), used instead of a look-up here: the next page's changes, saved while the batch is
    /// written, may have created or deleted some since. Null to look them up now.</param>
    private async Task BulkInsertCsoChangeAttributeValuesRawAsync(
        List<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> valueChanges,
        IReadOnlySet<Guid>? referencesFound = null)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        // An import pre-generates every new object's id before writing them in batches, so a reference can point at
        // an object a later batch has yet to write (a manager, a nested group): its id is known, its row is not. Its
        // foreign key used to fail the whole write with 23503 (#2492, found by Scenario 032). The key is gone (#2669),
        // but writing every known id is still to come, so only references to
        // rows that exist are written; the rest keep their DN in StringValue, are marked IsUnresolvedReference (#2690),
        // and FixupCrossBatchChangeRecordReferenceIdsAsync resolves them once every batch has been written. Must run
        // before the COPY below opens, since the connection cannot run another command while it is open.
        var writtenReferenceIds = referencesFound ?? await GetExistingConnectedSystemObjectIdsAsync(npgsqlConn,
            (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction(), ReferencedConnectedSystemObjectIds(valueChanges));

        await BulkInsertCsoChangeAttributeValuesOnConnectionAsync(npgsqlConn, valueChanges, writtenReferenceIds);
    }

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChangeAttributeValue rows using COPY binary import, on the given connection
    /// inside whatever transaction it holds, writing a reference's id only where it is among
    /// <paramref name="writtenReferenceIds"/>: the referenced objects whose rows exist, or will by the time the write
    /// that holds these values is complete (#2909).
    /// </summary>
    private static async Task BulkInsertCsoChangeAttributeValuesOnConnectionAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> valueChanges,
        IReadOnlySet<Guid> writtenReferenceIds)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ConnectedSystemObjectChangeAttributeValues" (
                {BulkSqlHelpers.ToQuotedList(CsoChangeBulkColumns.ConnectedSystemObjectChangeAttributeValues)}
            ) FROM STDIN (FORMAT binary)
            """);

        // Values below are written in exactly the order of CsoChangeBulkColumns.ConnectedSystemObjectChangeAttributeValues.
        foreach (var (attrChange, v) in valueChanges)
        {
            // Write the ReferenceValueId only when the referenced CSO's row exists (see the callers' lookups). Within-batch
            // references are written, since the batch's own objects are written with them; references to a future batch
            // are written as null. A Reference value holding a DN whose id is not written, whether its object is in a
            // later batch or named no object at all (an import reference that matched nothing yet), is marked for
            // FixupCrossBatchChangeRecordReferenceIdsAsync to resolve; nothing else is.
            Guid? writtenReferenceId = v.ReferenceValueId is { } refId && writtenReferenceIds.Contains(refId) ? refId : null;
            v.SetUnresolvedReferenceMark(attrChange.AttributeType, referenceIdWritten: writtenReferenceId.HasValue);

            await writer.StartRowAsync();
            await writer.WriteAsync(v.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(attrChange.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync((int)v.ValueChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (v.StringValue is not null)
                await writer.WriteAsync(v.StringValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (v.DateTimeValue.HasValue)
                await writer.WriteAsync(DateTimeAttributeValue.ToCanonical(v.DateTimeValue.Value), NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (v.IntValue.HasValue)
                await writer.WriteAsync(v.IntValue.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (v.LongValue.HasValue)
                await writer.WriteAsync(v.LongValue.Value, NpgsqlTypes.NpgsqlDbType.Bigint);
            else
                await writer.WriteNullAsync();
            if (v.DecimalValue.HasValue)
                await writer.WriteAsync(v.DecimalValue.Value, NpgsqlTypes.NpgsqlDbType.Numeric);
            else
                await writer.WriteNullAsync();
            if (v.ByteValueLength.HasValue)
                await writer.WriteAsync(v.ByteValueLength.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (v.GuidValue.HasValue)
                await writer.WriteAsync(v.GuidValue.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (v.BoolValue.HasValue)
                await writer.WriteAsync(v.BoolValue.Value, NpgsqlTypes.NpgsqlDbType.Boolean);
            else
                await writer.WriteNullAsync();
            if (writtenReferenceId is { } referenceId)
                await writer.WriteAsync(referenceId, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(v.IsUnresolvedReference, NpgsqlTypes.NpgsqlDbType.Boolean);
            await writer.WriteAsync(v.IsPendingExportStub, NpgsqlTypes.NpgsqlDbType.Boolean);
            if (v.SyncRuleId.HasValue)
                await writer.WriteAsync(v.SyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (v.SyncRuleName is not null)
                await writer.WriteAsync(v.SyncRuleName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Bulk updates OutcomeSummary, error fields, and AttributeFlowCount on already-persisted RPEIs
    /// using a temporary table with COPY binary import for efficient bulk transfer, then a single
    /// UPDATE ... FROM join. Chunks into batches of 10,000 rows to bound temp table memory usage.
    /// </summary>
    private async Task BulkUpdateRpeiFieldsRawAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        const int copyChunkSize = 10_000;
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        // Get the current EF-managed transaction (if any) so raw commands participate in it
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var chunk in BulkSqlHelpers.ChunkList(rpeis, copyChunkSize))
        {
            // Create a temp table (IF NOT EXISTS handles subsequent chunks within the same transaction)
            await using (var createCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                createCmd.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS _rpei_bulk_update (
                        "Id" uuid NOT NULL,
                        "OutcomeSummary" text,
                        "ErrorType" int,
                        "ErrorMessage" text,
                        "ErrorStackTrace" text,
                        "AttributeFlowCount" int,
                        "DeletionPolicySnapshotJson" text
                    ) ON COMMIT DROP
                    """;
                await createCmd.ExecuteNonQueryAsync();
            }

            // Truncate between chunks (temp table persists within transaction due to ON COMMIT DROP)
            await using (var truncateCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                truncateCmd.CommandText = @"TRUNCATE _rpei_bulk_update";
                await truncateCmd.ExecuteNonQueryAsync();
            }

            // COPY binary import — streams rows without SQL parsing or parameter limits
            // Writer order below MUST match RpeiBulkColumns.ActivityRunProfileExecutionItemsUpdate exactly.
            await using (var writer = await npgsqlConn.BeginBinaryImportAsync(
                $@"COPY _rpei_bulk_update (""Id"", {BulkSqlHelpers.ToQuotedList(RpeiBulkColumns.ActivityRunProfileExecutionItemsUpdate)}) FROM STDIN (FORMAT binary)"))
            {
                foreach (var rpei in chunk)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(rpei.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
                    if (rpei.OutcomeSummary is not null)
                        await writer.WriteAsync(rpei.OutcomeSummary, NpgsqlTypes.NpgsqlDbType.Text);
                    else
                        await writer.WriteNullAsync();
                    if (rpei.ErrorType.HasValue)
                        await writer.WriteAsync((int)rpei.ErrorType.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                    else
                        await writer.WriteNullAsync();
                    if (rpei.ErrorMessage is not null)
                        await writer.WriteAsync(rpei.ErrorMessage, NpgsqlTypes.NpgsqlDbType.Text);
                    else
                        await writer.WriteNullAsync();
                    // ErrorStackTrace is co-mutated with ErrorType/ErrorMessage at every worker error
                    // site; the three must be written together as a unit.
                    if (rpei.ErrorStackTrace is not null)
                        await writer.WriteAsync(rpei.ErrorStackTrace, NpgsqlTypes.NpgsqlDbType.Text);
                    else
                        await writer.WriteNullAsync();
                    if (rpei.AttributeFlowCount.HasValue)
                        await writer.WriteAsync(rpei.AttributeFlowCount.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                    else
                        await writer.WriteNullAsync();
                    if (rpei.DeletionPolicySnapshotJson is not null)
                        await writer.WriteAsync(rpei.DeletionPolicySnapshotJson, NpgsqlTypes.NpgsqlDbType.Text);
                    else
                        await writer.WriteNullAsync();
                }
                await writer.CompleteAsync();
            }

            // Single UPDATE join — PostgreSQL uses the primary key index on ActivityRunProfileExecutionItems
            await using (var updateCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                updateCmd.CommandText = $"""
                    UPDATE "ActivityRunProfileExecutionItems" t
                    SET {string.Join(", ", RpeiBulkColumns.ActivityRunProfileExecutionItemsUpdate.Select(c => $"\"{c}\" = v.\"{c}\""))}
                    FROM _rpei_bulk_update v
                    WHERE t."Id" = v."Id"
                    """;
                await updateCmd.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>
    /// Bulk inserts causal edges (#1223) using parameterised multi-row INSERT, chunked to stay within the
    /// PostgreSQL parameter limit. Edges are append-only, so there is no matching update path.
    /// </summary>
    public async Task BulkInsertCausalEdgesAsync(List<CausalEdge> edges)
    {
        if (edges.Count == 0)
            return;

        const int columnsPerRow = 19;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        // One builder reused across chunks rather than one per iteration; a deletion cascade can produce many.
        var sql = new System.Text.StringBuilder();

        foreach (var chunk in BulkSqlHelpers.ChunkList(edges, chunkSize))
        {
            sql.Clear();
            // Parameter order below MUST match CausalEdgeBulkColumns.CausalEdges exactly.
            sql.Append($@"INSERT INTO ""CausalEdges"" ({BulkSqlHelpers.ToQuotedList(CausalEdgeBulkColumns.CausalEdges)}) VALUES ");

            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"(@p{offset}, @p{offset + 1}, @p{offset + 2}, @p{offset + 3}, @p{offset + 4}, @p{offset + 5}, @p{offset + 6}, @p{offset + 7}, @p{offset + 8}, @p{offset + 9}, @p{offset + 10}, @p{offset + 11}, @p{offset + 12}, @p{offset + 13}, @p{offset + 14}, @p{offset + 15}, @p{offset + 16}, @p{offset + 17}, @p{offset + 18})");

                var edge = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = edge.Id });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = edge.EffectRunProfileExecutionItemId });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)edge.EffectSyncOutcomeId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)edge.CauseRunProfileExecutionItemId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 4}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)edge.CauseSyncOutcomeId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 5}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)edge.CauseMetaverseObjectId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 6}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)edge.CauseConnectedSystemObjectId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 7}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)edge.CausePendingExportId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 8}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)edge.CauseDisplayName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 9}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)edge.CauseObjectTypeName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 10}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)edge.CauseObjectTypePluralName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 11}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)edge.EffectAttributeName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 12}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)edge.EdgeType });
                parameters.Add(new NpgsqlParameter($"p{offset + 13}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)edge.ReasonCode });
                parameters.Add(new NpgsqlParameter($"p{offset + 14}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)edge.ConnectedSystemId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 15}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)edge.ConnectedSystemName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 16}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)edge.SyncRuleId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 17}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)edge.SyncRuleName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 18}", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = edge.Created });
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// The ambiguous match candidates buffered on <paramref name="rpeis"/> (#2052), each pointed at its item and given an
    /// id. The items' own ids exist by now; the candidates were recorded before them.
    /// </summary>
    private static List<ActivityRunProfileExecutionItemMatchCandidate> CollectMatchCandidates(IReadOnlyList<ActivityRunProfileExecutionItem> rpeis)
    {
        var candidates = new List<ActivityRunProfileExecutionItemMatchCandidate>();
        foreach (var rpei in rpeis.Where(r => r.MatchCandidates.Count > 0))
        {
            foreach (var candidate in rpei.MatchCandidates)
            {
                if (candidate.Id == Guid.Empty)
                    candidate.Id = TimeOrderedIds.New();
                candidate.ActivityRunProfileExecutionItemId = rpei.Id;
            }

            candidates.AddRange(rpei.MatchCandidates);
        }

        return candidates;
    }

    /// <summary>
    /// Bulk inserts ambiguous match candidates (#2052) using parameterised multi-row INSERT, chunked to stay within
    /// the PostgreSQL parameter limit. Candidates are append-only, so there is no matching update path.
    /// </summary>
    internal async Task BulkInsertMatchCandidatesAsync(List<ActivityRunProfileExecutionItemMatchCandidate> candidates)
    {
        if (candidates.Count == 0)
            return;

        var columnsPerRow = MatchCandidateBulkColumns.MatchCandidates.Length;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;
        var sql = new System.Text.StringBuilder();

        foreach (var chunk in BulkSqlHelpers.ChunkList(candidates, chunkSize))
        {
            sql.Clear();
            // Parameter order below MUST match MatchCandidateBulkColumns.MatchCandidates exactly.
            sql.Append($@"INSERT INTO ""ActivityRunProfileExecutionItemMatchCandidates"" ({BulkSqlHelpers.ToQuotedList(MatchCandidateBulkColumns.MatchCandidates)}) VALUES ");

            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append('(').Append(string.Join(", ", Enumerable.Range(offset, columnsPerRow).Select(p => $"@p{p}"))).Append(')');

                var candidate = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = candidate.Id });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = candidate.ActivityRunProfileExecutionItemId });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)candidate.CandidateKind });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = candidate.CandidateId });
                parameters.Add(new NpgsqlParameter($"p{offset + 4}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)candidate.DisplayNameSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 5}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)candidate.TypeNameSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 6}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)candidate.ConnectedSystemId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 7}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)candidate.ConnectedSystemNameSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 8}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)candidate.MatchedValue ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 9}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)candidate.ObjectMatchingRuleId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 10}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)candidate.ObjectMatchingRuleDescriptionSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 11}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = candidate.Ordinal });
                parameters.Add(new NpgsqlParameter($"p{offset + 12}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = candidate.CandidateTotal });
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItemSyncOutcome rows using parameterised multi-row INSERT.
    /// Chunks automatically to stay within the PostgreSQL parameter limit. Used solely by
    /// <see cref="BulkUpdateRpeiOutcomesAsync"/> (new outcomes merged onto already-persisted RPEIs);
    /// the create path (<see cref="BulkInsertRpeisAsync"/>) uses the COPY binary writer
    /// <see cref="BulkInsertSyncOutcomesOnConnectionAsync"/> instead, on both its parallel and
    /// single-connection branches.
    /// </summary>
    private async Task BulkInsertSyncOutcomesRawAsync(List<ActivityRunProfileExecutionItemSyncOutcome> outcomes)
    {
        // One parameter per column in the shared list, so a column added there cannot leave this writer short.
        var columnsPerRow = RpeiBulkColumns.ActivityRunProfileExecutionItemSyncOutcomes.Length;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(outcomes, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            // Parameter order below MUST match RpeiBulkColumns.ActivityRunProfileExecutionItemSyncOutcomes exactly.
            sql.Append($@"INSERT INTO ""ActivityRunProfileExecutionItemSyncOutcomes"" ({BulkSqlHelpers.ToQuotedList(RpeiBulkColumns.ActivityRunProfileExecutionItemSyncOutcomes)}) VALUES ");

            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append('(').AppendJoin(", ", Enumerable.Range(offset, columnsPerRow).Select(n => $"@p{n}")).Append(')');

                var outcome = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = outcome.Id });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = outcome.ActivityRunProfileExecutionItemId });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)outcome.ParentSyncOutcomeId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)outcome.OutcomeType });
                parameters.Add(new NpgsqlParameter($"p{offset + 4}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)outcome.TargetEntityId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 5}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.TargetEntityDescription ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 6}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)outcome.DetailCount ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 7}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.DetailMessage ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 8}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = outcome.Ordinal });
                parameters.Add(new NpgsqlParameter($"p{offset + 9}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)outcome.ConnectedSystemObjectChangeId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 10}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)outcome.SyncRuleId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 11}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.SyncRuleName ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 12}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = outcome.StagedChangeType.HasValue ? (object)(int)outcome.StagedChangeType.Value : DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 13}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)outcome.ObjectMatchingRuleId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 14}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.ObjectMatchingRuleDescriptionSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 15}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.MatchedValue ?? DBNull.Value });
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Inserts RPEI rows on an independent NpgsqlConnection using COPY binary import.
    /// </summary>
    private static async Task BulkInsertRpeisOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<ActivityRunProfileExecutionItem> rpeis)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ActivityRunProfileExecutionItems" (
                {BulkSqlHelpers.ToQuotedList(RpeiBulkColumns.ActivityRunProfileExecutionItems)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var rpei in rpeis)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(rpei.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(rpei.ActivityId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync((int)rpei.ObjectChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (rpei.NoChangeReason.HasValue)
                await writer.WriteAsync((int)rpei.NoChangeReason.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            // The id set, else the unmapped working reference's (#2669).
            if ((rpei.ConnectedSystemObjectId ?? rpei.ConnectedSystemObject?.Id) is { } csoId && csoId != Guid.Empty)
                await writer.WriteAsync(csoId, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (rpei.ExternalIdSnapshot is not null)
                await writer.WriteAsync(rpei.ExternalIdSnapshot, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.DisplayNameSnapshot is not null)
                await writer.WriteAsync(rpei.DisplayNameSnapshot, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.ObjectTypeSnapshot is not null)
                await writer.WriteAsync(rpei.ObjectTypeSnapshot, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.ErrorType.HasValue)
                await writer.WriteAsync((int)rpei.ErrorType.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (rpei.ErrorMessage is not null)
                await writer.WriteAsync(rpei.ErrorMessage, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.ErrorStackTrace is not null)
                await writer.WriteAsync(rpei.ErrorStackTrace, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.AttributeFlowCount.HasValue)
                await writer.WriteAsync(rpei.AttributeFlowCount.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (rpei.OutcomeSummary is not null)
                await writer.WriteAsync(rpei.OutcomeSummary, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.PendingExportId.HasValue)
                await writer.WriteAsync(rpei.PendingExportId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (rpei.DeletionPolicySnapshotJson is not null)
                await writer.WriteAsync(rpei.DeletionPolicySnapshotJson, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (rpei.MetaverseObjectId.HasValue)
                await writer.WriteAsync(rpei.MetaverseObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (rpei.UnresolvedReferenceCount.HasValue)
                await writer.WriteAsync(rpei.UnresolvedReferenceCount.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (rpei.AttributeValueErrorsJson is not null)
                await writer.WriteAsync(rpei.AttributeValueErrorsJson, NpgsqlTypes.NpgsqlDbType.Jsonb);
            else
                await writer.WriteNullAsync();
            if (rpei.SourceLocation is not null)
                await writer.WriteAsync(rpei.SourceLocation, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts sync outcome rows on an independent NpgsqlConnection using COPY binary import.
    /// </summary>
    private static async Task BulkInsertSyncOutcomesOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<ActivityRunProfileExecutionItemSyncOutcome> outcomes)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ActivityRunProfileExecutionItemSyncOutcomes" (
                {BulkSqlHelpers.ToQuotedList(RpeiBulkColumns.ActivityRunProfileExecutionItemSyncOutcomes)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var outcome in outcomes)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(outcome.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(outcome.ActivityRunProfileExecutionItemId, NpgsqlTypes.NpgsqlDbType.Uuid);
            if (outcome.ParentSyncOutcomeId.HasValue)
                await writer.WriteAsync(outcome.ParentSyncOutcomeId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync((int)outcome.OutcomeType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (outcome.TargetEntityId.HasValue)
                await writer.WriteAsync(outcome.TargetEntityId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (outcome.TargetEntityDescription is not null)
                await writer.WriteAsync(outcome.TargetEntityDescription, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (outcome.DetailCount.HasValue)
                await writer.WriteAsync(outcome.DetailCount.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (outcome.DetailMessage is not null)
                await writer.WriteAsync(outcome.DetailMessage, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(outcome.Ordinal, NpgsqlTypes.NpgsqlDbType.Integer);
            if (outcome.ConnectedSystemObjectChangeId.HasValue)
                await writer.WriteAsync(outcome.ConnectedSystemObjectChangeId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (outcome.SyncRuleId.HasValue)
                await writer.WriteAsync(outcome.SyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (outcome.SyncRuleName is not null)
                await writer.WriteAsync(outcome.SyncRuleName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (outcome.StagedChangeType.HasValue)
                await writer.WriteAsync((int)outcome.StagedChangeType.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (outcome.ObjectMatchingRuleId.HasValue)
                await writer.WriteAsync(outcome.ObjectMatchingRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (outcome.ObjectMatchingRuleDescriptionSnapshot is not null)
                await writer.WriteAsync(outcome.ObjectMatchingRuleDescriptionSnapshot, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (outcome.MatchedValue is not null)
                await writer.WriteAsync(outcome.MatchedValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Flattens the sync outcome tree for an RPEI into a list, pre-generating IDs
    /// and setting FK references for bulk insertion.
    /// </summary>
    private static List<ActivityRunProfileExecutionItemSyncOutcome> FlattenSyncOutcomes(ActivityRunProfileExecutionItem rpei)
    {
        var result = new List<ActivityRunProfileExecutionItemSyncOutcome>();

        // SyncOutcomeBuilder adds all nodes (root + children) to rpei.SyncOutcomes as a flat list,
        // AND also builds a parent→Children tree. If we pass the full flat list to
        // FlattenOutcomesRecursive, children get visited twice (once from the flat list, once from
        // parent.Children recursion), causing duplicate inserts.
        // Start from root outcomes only and let recursion reach children via parent.Children.
        var roots = rpei.SyncOutcomes.Where(o => o.ParentSyncOutcome == null && o.ParentSyncOutcomeId == null).ToList();
        FlattenOutcomesRecursive(roots, rpei.Id, null, result);
        return result;
    }

    private static void FlattenOutcomesRecursive(
        List<ActivityRunProfileExecutionItemSyncOutcome> outcomes,
        Guid rpeiId,
        Guid? parentId,
        List<ActivityRunProfileExecutionItemSyncOutcome> result)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.Id == Guid.Empty)
                outcome.Id = TimeOrderedIds.New();

            outcome.ActivityRunProfileExecutionItemId = rpeiId;
            outcome.ParentSyncOutcomeId = parentId;
            result.Add(outcome);

            if (outcome.Children.Count > 0)
                FlattenOutcomesRecursive(outcome.Children, rpeiId, outcome.Id, result);
        }
    }

    #endregion
}
