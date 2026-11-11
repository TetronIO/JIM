// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// An import's batch of new Connected System Objects written together with its history (#2909).
/// </summary>
/// <remarks>
/// A Full Import wrote each batch as three writes, one after another: the objects and their values (across the parallel
/// connections), then the execution items, then the change records, the last two on one connection. At 500,000 objects
/// the change records were 0.38 s of each 0.65 s batch, while neither the Worker nor the database was busy. Nothing in
/// the change records depends on the objects being written: a change record names its object by plain id (audit rows
/// hold no foreign key to the objects they describe, #2669), and the execution items it belongs to hold none either. So
/// the batch is written as two parts at once, each on connections of its own: the objects and their values, and the
/// history, whose change record rows are partitioned by row as the objects' values are (#2872).
/// </remarks>
public partial class SyncRepository
{
    /// <inheritdoc />
    public async Task<bool> CreateConnectedSystemObjectsWithHistoryAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        HashSet<Guid>? previouslyCommittedCsoIds = null)
    {
        ConnectedSystemObject.EnsureWhollyLoaded(connectedSystemObjects, nameof(CreateConnectedSystemObjectsWithHistoryAsync));
        if (connectedSystemObjects.Count == 0 && rpeis.Count == 0)
            return true;

        PrepareConnectedSystemObjectsForCreate(connectedSystemObjects, previouslyCommittedCsoIds);
        var attributeValueRows = ParallelBatchWriter.FlattenValueRows(connectedSystemObjects, cso => cso.Id, cso => cso.AttributeValues);

        // The history, every id assigned before anything is written: the items (time-ordered, #2807), their outcome trees,
        // the change records the outcomes carry, their causal edges, match candidates and stat counter deltas; then the
        // items' own change records, each naming its item.
        var history = CollectExecutionItemHistory(rpeis, rowsAlreadyWritten: null, [], [], [], page: 0, totalPages: 0);
        var changes = ItemChangeRecords(rpeis);
        var changeAttributeRows = CsoChangeAttributeRows(changes);
        var changeValueRows = CsoChangeValueRows(changes);

        var objectRows = connectedSystemObjects.Count + attributeValueRows.Count;
        var changeRows = changeAttributeRows.Count + changeValueRows.Count;
        var itemRows = history.Items.Count + history.Outcomes.Count + changes.Count;

        using var span = MvoActivitySource.StartActivity("Cso.CreateWithHistory");
        span?.SetTag("count", connectedSystemObjects.Count);
        span?.SetTag("valueCount", attributeValueRows.Count);
        span?.SetTag("itemCount", history.Items.Count);
        span?.SetTag("changeCount", changes.Count);
        span?.SetTag("changeValueCount", changeValueRows.Count);

        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);
        try
        {
            // Gated on every row the call writes, as the other bulk writers are (#2872), and given connections in
            // proportion to them (#2669).
            var connections = ConnectionsForWrite(objectRows + changeRows + itemRows);
            if (connections > 1)
            {
                await WriteBatchConcurrentlyAsync(connectedSystemObjects, attributeValueRows, previouslyCommittedCsoIds, history, changes,
                    changeAttributeRows, changeValueRows, objectRows, changeRows, connections, span);
            }
            else
            {
                span?.SetTag("connections", 1);
                await WriteBatchInOneTransactionAsync(connectedSystemObjects, attributeValueRows, history, changes);
            }
        }
        finally
        {
            _context.Database.SetCommandTimeout(previousTimeout);
        }

        // Only reached on success: a throw above leaves the buffers intact, which is what a retry needs.
        DrainCausalEdgeAndMatchCandidateBuffers(rpeis);
        foreach (var cso in connectedSystemObjects)
            cso.SynchronisationPending = true;

        return true; // Raw SQL used: the items are outside the change tracker.
    }

    /// <summary>
    /// The items' own change records, each pointed at its item by id and given its ids (time-ordered, #2669).
    /// </summary>
    private static List<ConnectedSystemObjectChange> ItemChangeRecords(IEnumerable<ActivityRunProfileExecutionItem> rpeis)
    {
        var changes = new List<ConnectedSystemObjectChange>();
        foreach (var rpei in rpeis.Where(r => r.ConnectedSystemObjectChange != null))
        {
            var change = rpei.ConnectedSystemObjectChange!;
            change.ActivityRunProfileExecutionItemId = rpei.Id;
            ChangeRecordIds.AssignMissing(change);
            changes.Add(change);
        }

        return changes;
    }

    /// <summary>
    /// A batch too small to gain from parallel connections, or whose caller holds a transaction: everything on the
    /// context's connection, in one transaction (the caller's, when it holds one), in the order the separate writes made
    /// it: the objects (already marked pending, since their values commit with them, #2092) and their values, the
    /// execution item history, then the change records.
    /// </summary>
    private async Task WriteBatchInOneTransactionAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        IReadOnlyList<(Guid OwnerId, ConnectedSystemObjectAttributeValue Value)> attributeValueRows,
        SynchronisationHistoryBatch history,
        List<ConnectedSystemObjectChange> changes)
    {
        await using var transaction = _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync()
            : null;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var npgsqlTransaction = (NpgsqlTransaction)_context.Database.CurrentTransaction!.GetDbTransaction();

        if (connectedSystemObjects.Count > 0)
        {
            using (var rowsSpan = MvoActivitySource.StartActivity("Cso.Create.Rows"))
            {
                rowsSpan?.SetTag("rowCount", connectedSystemObjects.Count);
                rowsSpan?.SetTag("connections", 1);
                await BulkInsertCsosOnConnectionAsync(connection, npgsqlTransaction, connectedSystemObjects, synchronisationPending: true);
            }

            if (attributeValueRows.Count > 0)
            {
                using var valuesSpan = MvoActivitySource.StartActivity("Cso.Create.Values");
                valuesSpan?.SetTag("rowCount", attributeValueRows.Count);
                valuesSpan?.SetTag("connections", 1);
                await BulkInsertCsoAttributeValuesOnConnectionAsync(connection, attributeValueRows, partitionCsoIds: null);
            }
        }

        if (history.Items.Count > 0)
            await WriteExecutionItemHistoryAsync(history);

        if (changes.Count > 0)
            await WriteCsoChangeRecordsAsync(changes);

        if (transaction != null)
            await transaction.CommitAsync();
    }

    /// <summary>
    /// A batch large enough to gain from parallel connections, written as two parts at once (#2909), then finished:
    /// <list type="number">
    /// <item>The objects: their rows, then their values, across their share of the connections (the first two phases of
    /// <see cref="CreateConnectedSystemObjectsAsync"/>).</item>
    /// <item>The history: on the context's connection, in one transaction, the execution items, their outcomes, the change
    /// records the outcomes carry and the items' own change record rows (which reference the items); then the change
    /// records' attribute rows and value rows, each partitioned by row across the history's share of the connections.</item>
    /// <item>Once both have committed, on the context's connection in one transaction: the objects' synchronisation-pending
    /// mark (only now every value has committed, as in <see cref="CreateConnectedSystemObjectsAsync"/>), the causal edges,
    /// the match candidates and the stat counters, which cannot be taken back by removing rows and so are written last.</item>
    /// </list>
    /// The two parts share <paramref name="parallelism"/>, the connections the batch's rows earn
    /// (<see cref="ConnectionsForWrite"/>), in proportion to their rows, so together they hold no more connections than
    /// one parallel write of the same rows would (plus the context's own, as before). Neither part depends on the other:
    /// a change record names its object by plain id, and a change value's reference to an object of this batch is written as the old serial
    /// path wrote it once the objects had committed. Nothing is one transaction, so a failure anywhere removes everything
    /// the call committed before it propagates: no object is left without its history, and no history without its object.
    /// </summary>
    private async Task WriteBatchConcurrentlyAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        IReadOnlyList<(Guid OwnerId, ConnectedSystemObjectAttributeValue Value)> attributeValueRows,
        HashSet<Guid>? previouslyCommittedCsoIds,
        SynchronisationHistoryBatch history,
        List<ConnectedSystemObjectChange> changes,
        List<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> changeAttributeRows,
        List<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> changeValueRows,
        int objectRows,
        int changeRows,
        int parallelism,
        System.Diagnostics.Activity? span)
    {
        var connectionString = ConnectionStringForParallelWrites!;
        var (objectConnections, historyConnections) = ParallelBatchWriter.SplitConnections(parallelism, objectRows, changeRows);
        span?.SetTag("connections", parallelism);
        span?.SetTag("objectConnections", objectConnections);
        span?.SetTag("historyConnections", historyConnections);
        Log.Information("CreateConnectedSystemObjectsWithHistoryAsync: Writing {Count} CSOs and {ValueCount} attribute values across {ObjectConnections} connections, " +
            "beside {ItemCount} RPEIs and {ChangeCount} change records with {ChangeValueCount} value changes across {HistoryConnections}",
            connectedSystemObjects.Count, attributeValueRows.Count, objectConnections, history.Items.Count, changes.Count, changeValueRows.Count,
            historyConnections);

        var allBatchCsoIds = BatchAndCommittedCsoIds(connectedSystemObjects, previouslyCommittedCsoIds);
        var batchCsoIds = connectedSystemObjects.Select(c => c.Id).ToHashSet();
        var committedCsoIds = new ConcurrentQueue<Guid>();
        var historyHeadCommitted = false;
        var written = false;
        try
        {
            var objectsPart = connectedSystemObjects.Count == 0
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    using var objectsSpan = MvoActivitySource.StartActivity("Cso.CreateWithHistory.Objects");
                    await WriteCsoRowsAndValuesInParallelAsync(connectedSystemObjects, attributeValueRows, allBatchCsoIds, objectConnections,
                        connectionString, committedCsoIds);
                });

            var historyPart = Task.Run(async () =>
            {
                using var historySpan = MvoActivitySource.StartActivity("Cso.CreateWithHistory.History");
                var writtenReferenceIds = await WriteHistoryHeadAsync(history, changes, changeValueRows);
                historyHeadCommitted = true;

                // The batch's own objects are written by the other part, and removed with this one should either fail, so
                // a reference to one is written as the old serial path wrote it, once the objects had committed.
                writtenReferenceIds.UnionWith(batchCsoIds);
                if (changeRows > 0)
                    await WriteCsoChangeAttributesAndValuesInParallelAsync(changeAttributeRows, changeValueRows, writtenReferenceIds,
                        historyConnections, connectionString);
            });

            // Both parts run to their end before anything is removed, whichever fails first.
            await Task.WhenAll(objectsPart, historyPart);

            using (MvoActivitySource.StartActivity("Cso.CreateWithHistory.Finish"))
                await FinishBatchAsync(connectedSystemObjects, history);

            written = true;
        }
        finally
        {
            if (!written)
            {
                // Only what this call committed: object rows by the ids their partitions committed (an object that was
                // already in the table failed its partition, and is left alone), and the history, written in full by its
                // first transaction or not at all, by its own ids. Outcomes before the change records they may name, and
                // change records before the items they name; the change records take their attribute and value rows with
                // them by cascade.
                var removals = new List<ParallelWriteRemoval>();
                if (!committedCsoIds.IsEmpty)
                    removals.AddRange(CsoRowRemovals([.. committedCsoIds]));
                if (historyHeadCommitted)
                {
                    removals.Add(new ParallelWriteRemoval("ActivityRunProfileExecutionItemSyncOutcomes", "Id", [.. history.Outcomes.Select(o => o.Id)]));
                    removals.Add(CsoChangeRecordRemoval(changes.Concat(history.OutcomeChangeRecords)));
                    removals.Add(new ParallelWriteRemoval("ActivityRunProfileExecutionItems", "Id", [.. history.Items.Select(i => i.Id)]));
                }

                if (removals.Count > 0)
                    await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(CreateConnectedSystemObjectsWithHistoryAsync), removals);
            }
        }
    }

    /// <summary>
    /// The first step of the history part, on the context's connection in one transaction: the execution items, the change
    /// records their outcomes carry, the outcomes, and the items' own change record rows, which name the items and so
    /// must follow them. Returns the objects the change values reference whose rows exist now, read in the same
    /// transaction, as the single-connection writer reads them before writing the values.
    /// </summary>
    private async Task<HashSet<Guid>> WriteHistoryHeadAsync(
        SynchronisationHistoryBatch history,
        List<ConnectedSystemObjectChange> changes,
        List<(ConnectedSystemObjectChangeAttribute AttrChange, ConnectedSystemObjectChangeAttributeValue Value)> changeValueRows)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        if (history.Items.Count > 0)
        {
            using var rowsSpan = MvoActivitySource.StartActivity("RpeiHistory.Rows");
            rowsSpan?.SetTag("rowCount", history.Items.Count);
            await BulkInsertRpeisOnConnectionAsync(connection, npgsqlTransaction, AsList(history.Items));
        }

        if (history.OutcomeChangeRecords.Count > 0)
        {
            using var outcomeChangesSpan = MvoActivitySource.StartActivity("RpeiHistory.CsoChanges");
            outcomeChangesSpan?.SetTag("rowCount", history.OutcomeChangeRecords.Count);
            await InsertCsoChangeHierarchyRawAsync(AsList(history.OutcomeChangeRecords));
        }

        if (history.Outcomes.Count > 0)
        {
            using var outcomesSpan = MvoActivitySource.StartActivity("RpeiHistory.Outcomes");
            outcomesSpan?.SetTag("rowCount", history.Outcomes.Count);
            await BulkInsertSyncOutcomesOnConnectionAsync(connection, npgsqlTransaction, AsList(history.Outcomes));
        }

        if (changes.Count > 0)
        {
            using var changesSpan = MvoActivitySource.StartActivity("CsoChange.Persist.Changes");
            changesSpan?.SetTag("rowCount", changes.Count);
            await BulkInsertCsoChangesOnConnectionAsync(connection, changes);
        }

        var writtenReferenceIds = await GetExistingConnectedSystemObjectIdsAsync(connection, npgsqlTransaction,
            ReferencedConnectedSystemObjectIds(changeValueRows));

        await transaction.CommitAsync();
        return writtenReferenceIds;
    }

    /// <summary>
    /// The last step of a concurrent batch, once both parts have committed, on the context's connection in one
    /// transaction, so a failure leaves none of it: the objects' synchronisation-pending mark, the causal edges, the match
    /// candidates and the stat counter deltas.
    /// </summary>
    private async Task FinishBatchAsync(List<ConnectedSystemObject> connectedSystemObjects, SynchronisationHistoryBatch history)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

        if (connectedSystemObjects.Count > 0)
        {
            using (MvoActivitySource.StartActivity("Cso.Create.Mark"))
                await MarkConnectedSystemObjectsSynchronisationPendingOnConnectionAsync(connection, npgsqlTransaction,
                    connectedSystemObjects.Select(cso => cso.Id).ToList());
        }

        if (history.CausalEdges.Count > 0)
        {
            using var edgesSpan = MvoActivitySource.StartActivity("RpeiHistory.Edges");
            edgesSpan?.SetTag("rowCount", history.CausalEdges.Count);
            await BulkInsertCausalEdgesAsync(AsList(history.CausalEdges));
        }

        if (history.MatchCandidates.Count > 0)
        {
            using var candidatesSpan = MvoActivitySource.StartActivity("RpeiHistory.Candidates");
            candidatesSpan?.SetTag("rowCount", history.MatchCandidates.Count);
            await BulkInsertMatchCandidatesAsync(AsList(history.MatchCandidates));
        }

        using (var countersSpan = MvoActivitySource.StartActivity("RpeiHistory.Counters"))
        {
            countersSpan?.SetTag("rowCount", history.CounterDeltas.Count);
            await ActivityStatCounterWriter.UpsertDeltasAsync(_context, history.CounterDeltas);
        }

        await transaction.CommitAsync();
    }
}
