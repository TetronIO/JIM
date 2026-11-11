// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Connected System Object — Reference Targets

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> GetConnectedSystemObjectTypeIdsInUseAsync(int connectedSystemId)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        // A loose index scan: each step asks the (ConnectedSystemId, TypeId, Id) index for the next type id above the
        // last, so the cost is one probe per type in use rather than a pass over every object.
        await using var command = new NpgsqlCommand(
            """
            WITH RECURSIVE types AS (
                (SELECT "TypeId" FROM "ConnectedSystemObjects"
                 WHERE "ConnectedSystemId" = @connectedSystemId
                 ORDER BY "TypeId" LIMIT 1)
                UNION ALL
                SELECT (SELECT cso."TypeId" FROM "ConnectedSystemObjects" cso
                        WHERE cso."ConnectedSystemId" = @connectedSystemId AND cso."TypeId" > types."TypeId"
                        ORDER BY cso."TypeId" LIMIT 1)
                FROM types
                WHERE types."TypeId" IS NOT NULL
            )
            SELECT "TypeId" FROM types WHERE "TypeId" IS NOT NULL
            """, npgsqlConn, npgsqlTx);
        command.Parameters.Add(new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId });

        var inUse = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            inUse.Add(reader.GetInt32(0));
        return inUse;
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, IReadOnlySet<int>>> GetReferencedObjectTypeIdsAsync(IReadOnlyCollection<int> attributeIds)
    {
        var result = attributeIds.Distinct().ToDictionary(id => id, _ => (IReadOnlySet<int>)new HashSet<int>());
        if (result.Count == 0)
            return result;

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        // One pass over the attributes' values (measured ~2 s for 7.25M reference values across 790,000 objects). The
        // referenced row is reached by its primary key, so the referencing system needs no filter of its own: an
        // attribute id belongs to exactly one Connected System's schema.
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT av."AttributeId", target."TypeId"
            FROM "ConnectedSystemObjectAttributeValues" av
            INNER JOIN "ConnectedSystemObjects" target ON target."Id" = av."ReferenceValueId"
            WHERE av."AttributeId" = ANY(@attributeIds)
            """, npgsqlConn, npgsqlTx);
        command.Parameters.Add(new NpgsqlParameter("attributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = result.Keys.ToArray()
        });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ((HashSet<int>)result[reader.GetInt32(0)]).Add(reader.GetInt32(1));

        return result;
    }

    #endregion

    #region Connected System Object — Parallel Bulk Create

    /// <summary>
    /// Bulk creates CSOs with their attribute values using parallel multi-connection writes.
    /// Each partition of CSOs is written on its own <see cref="NpgsqlConnection"/>, allowing
    /// PostgreSQL to utilise multiple CPU cores during the INSERT phase.
    /// </summary>
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds = null)
    {
        ConnectedSystemObject.EnsureWhollyLoaded(connectedSystemObjects, nameof(CreateConnectedSystemObjectsAsync));
        if (connectedSystemObjects.Count == 0)
            return;

        PrepareConnectedSystemObjectsForCreate(connectedSystemObjects, previouslyCommittedCsoIds);
        var attributeValueRows = ParallelBatchWriter.FlattenValueRows(connectedSystemObjects, cso => cso.Id, cso => cso.AttributeValues);

        using var createSpan = MvoActivitySource.StartActivity("Cso.Create");
        createSpan?.SetTag("count", connectedSystemObjects.Count);
        createSpan?.SetTag("valueCount", attributeValueRows.Count);

        // Gated on the rows written, objects plus values, not on the object count (#2872).
        if (!ShouldWriteInParallel(connectedSystemObjects.Count + attributeValueRows.Count))
        {
            createSpan?.SetTag("connections", 1);
            await CreateCsosOnSingleConnectionAsync(connectedSystemObjects, attributeValueRows);
            return;
        }

        var parallelism = WriteParallelism;
        var connectionString = ConnectionStringForParallelWrites!;
        createSpan?.SetTag("connections", parallelism);
        Log.Information("CreateConnectedSystemObjectsAsync: Writing {Count} CSOs and {ValueCount} attribute values across {Parallelism} parallel connections",
            connectedSystemObjects.Count, attributeValueRows.Count, parallelism);

        // Three-phase parallel write: CSO rows first (committed), then attribute values, then the
        // synchronisation-pending mark (#2092); see WriteCsoRowsAndValuesInParallelAsync for the first two.
        // The phases commit separately, so unlike the single-connection path the write is not one transaction.
        // A failure in any phase therefore removes every CSO row the call committed, and their
        // values, before it propagates (#2872): the import is told the batch failed and finds nothing of it,
        // as when the single-connection transaction rolls back, rather than objects with no external identifier
        // that the next import cannot match and so creates again beside them.
        var allBatchCsoIds = BatchAndCommittedCsoIds(connectedSystemObjects, previouslyCommittedCsoIds);
        var committedCsoIds = new ConcurrentQueue<Guid>();
        var written = false;
        try
        {
            await WriteCsoRowsAndValuesInParallelAsync(connectedSystemObjects, attributeValueRows, allBatchCsoIds, parallelism,
                connectionString, committedCsoIds);

            // Phase 3: mark the rows synchronisation-pending, now that every value has committed.
            using (MvoActivitySource.StartActivity("Cso.Create.Mark"))
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await MarkConnectedSystemObjectsSynchronisationPendingOnConnectionAsync(connection, transaction,
                    connectedSystemObjects.Select(cso => cso.Id).ToList());
                await transaction.CommitAsync();
            }

            written = true;
        }
        finally
        {
            // Only the rows this call committed: an id that was already in the table failed its partition's insert,
            // so it is not in the list, and the object that already held it is left alone.
            if (!written && !committedCsoIds.IsEmpty)
                await RemoveRowsOfFailedParallelWriteAsync(connectionString, nameof(CreateConnectedSystemObjectsAsync),
                    CsoRowRemovals([.. committedCsoIds]));
        }

        foreach (var cso in connectedSystemObjects)
            cso.SynchronisationPending = true;
    }

    /// <inheritdoc />
    public void PrepareConnectedSystemObjectsForCreate(List<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds = null)
    {
        // Pre-generate IDs for all CSOs and their attribute values (bypasses EF ValueGeneratedOnAdd)
        foreach (var cso in connectedSystemObjects)
        {
            if (cso.Id == Guid.Empty)
                cso.Id = Guid.NewGuid();

            foreach (var av in cso.AttributeValues)
            {
                if (av.Id == Guid.Empty)
                    av.Id = Guid.NewGuid();
            }
        }

        // Fixup ReferenceValueId FKs from navigation properties within this batch.
        // Cross-batch references (referenced CSO in a later batch) are left null and
        // resolved after all batches via FixupCrossBatchReferenceIdsAsync.
        // Also null out ReferenceValueId for references to CSOs NOT in this batch — these
        // may have been set by ResolveReferencesAsync before the persist phase, pointing
        // to pre-generated IDs for CSOs that haven't been persisted yet.
        //
        // When previouslyCommittedCsoIds is provided, FKs pointing to already-committed CSOs
        // are preserved. Combined with sorting CSOs so referenced objects come first, this
        // eliminates most cross-batch FK violations and reduces FixupCrossBatchReferenceIdsAsync work.
        //
        // Idempotent: a second pass over the same objects with the same committed ids changes nothing, so the create
        // writers run it again whether or not their caller prepared the objects first.
        var allowedCsoIds = BatchAndCommittedCsoIds(connectedSystemObjects, previouslyCommittedCsoIds);

        foreach (var cso in connectedSystemObjects)
        {
            foreach (var av in cso.AttributeValues)
            {
                if (av.ReferenceValue != null && av.ReferenceValue.Id != Guid.Empty && !av.ReferenceValueId.HasValue
                    && allowedCsoIds.Contains(av.ReferenceValue.Id))
                    av.ReferenceValueId = av.ReferenceValue.Id;

                // Null out ReferenceValueId for references to CSOs not yet committed.
                // ResolveReferencesAsync may have set this to a pre-generated ID for a CSO
                // in a future batch. Writing this FK would cause an FK constraint violation.
                // FixupCrossBatchReferenceIdsAsync resolves these after all batches are persisted.
                if (av.ReferenceValueId.HasValue && av.ReferenceValueId.Value != Guid.Empty
                    && !allowedCsoIds.Contains(av.ReferenceValueId.Value))
                    av.ReferenceValueId = null;
            }
        }
    }

    /// <summary>
    /// The ids of the objects being created and of those committed by earlier batches: the objects a value written now
    /// may reference.
    /// </summary>
    private static HashSet<Guid> BatchAndCommittedCsoIds(IEnumerable<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds)
    {
        var ids = new HashSet<Guid>(connectedSystemObjects.Select(c => c.Id));
        if (previouslyCommittedCsoIds != null)
            ids.UnionWith(previouslyCommittedCsoIds);
        return ids;
    }

    /// <summary>
    /// What removing the Connected System Objects a failed parallel write committed takes: their attribute values first,
    /// then the objects (see <see cref="RemoveRowsOfFailedParallelWriteAsync"/> for why the values go first).
    /// </summary>
    private static ParallelWriteRemoval[] CsoRowRemovals(Guid[] committedCsoIds) =>
    [
        new ParallelWriteRemoval("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", committedCsoIds),
        new ParallelWriteRemoval("ConnectedSystemObjects", "Id", committedCsoIds)
    ];

    /// <summary>
    /// The first two phases of a parallel Connected System Object create: the object rows, then their attribute values,
    /// each across <paramref name="parallelism"/> connections, each connection committing its own part. Phase 1 commits
    /// every row before phase 2 starts, making them visible to every later transaction, so a value's ReferenceValueId can
    /// point to any object of this batch or an earlier one without cross-partition foreign key violations. The values are
    /// partitioned by row, not by object, so a large group's members spread across every connection (#2872); one object's
    /// values may therefore commit on several connections, which is why the rows are written without the
    /// synchronisation-pending mark, and marked only once every value has committed: no synchronisation can load a marked
    /// object without all its values, process it and clear the mark.
    /// </summary>
    /// <param name="committedCsoIds">Receives the id of every object row this call commits, for the caller's removal
    /// should a later step fail.</param>
    private static async Task WriteCsoRowsAndValuesInParallelAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        IReadOnlyList<(Guid OwnerId, ConnectedSystemObjectAttributeValue Value)> attributeValueRows,
        HashSet<Guid> allBatchCsoIds,
        int parallelism,
        string connectionString,
        ConcurrentQueue<Guid> committedCsoIds)
    {
        // Phase 1: Write CSO rows across parallel connections and commit.
        using (var rowsSpan = MvoActivitySource.StartActivity("Cso.Create.Rows"))
        {
            rowsSpan?.SetTag("rowCount", connectedSystemObjects.Count);
            rowsSpan?.SetTag("connections", Math.Min(parallelism, connectedSystemObjects.Count));
            await ParallelBatchWriter.ExecuteAsync(
                connectedSystemObjects,
                parallelism,
                connectionString,
                async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    await BulkInsertCsosOnConnectionAsync(connection, transaction, partition, synchronisationPending: false);
                    await transaction.CommitAsync();
                    foreach (var cso in partition)
                        committedCsoIds.Enqueue(cso.Id);
                });
        }

        // Phase 2: Write attribute value rows across parallel connections. All CSO rows are now committed and
        // visible, so references to any CSO of this batch or an earlier one succeed.
        using (var valuesSpan = MvoActivitySource.StartActivity("Cso.Create.Values"))
        {
            valuesSpan?.SetTag("rowCount", attributeValueRows.Count);
            valuesSpan?.SetTag("connections", Math.Min(parallelism, attributeValueRows.Count));
            await ParallelBatchWriter.ExecuteAsync(
                attributeValueRows,
                parallelism,
                connectionString,
                async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    await BulkInsertCsoAttributeValuesOnConnectionAsync(connection, partition, allBatchCsoIds);
                    await transaction.CommitAsync();
                });
        }
    }

    /// <summary>
    /// Below-threshold fallback for <see cref="CreateConnectedSystemObjectsAsync"/>: writes CSO rows
    /// then their attribute values via COPY binary import on the EF connection's own transaction,
    /// rather than opening the N independent connections the parallel branch above uses. COPY binary
    /// is markedly faster than parameterised multi-row INSERT even on a single connection (no
    /// per-statement SQL parse/bind of tens of thousands of parameters), so this reuses the same
    /// on-connection writers the parallel branch calls (<see cref="BulkInsertCsosOnConnectionAsync"/>,
    /// <see cref="BulkInsertCsoAttributeValuesOnConnectionAsync"/>); see the established pattern for
    /// COPY on the EF connection in <see cref="ApplyExportedAttributeValuesAsync"/>.
    /// </summary>
    /// <remarks>
    /// No partition-based reference filtering is needed here (<c>partitionCsoIds: null</c>, meaning
    /// "write every set ReferenceValueId unconditionally"), unlike the parallel branch's two-phase
    /// write across independent connections. This whole batch is one transaction on one connection:
    /// CSO rows are COPY'd and therefore visible to this session before attribute values are COPY'd,
    /// so a same-batch forward reference resolves without cross-partition isolation. The caller
    /// (<see cref="CreateConnectedSystemObjectsAsync"/>) has already nulled any ReferenceValueId
    /// pointing outside <c>allowedCsoIds</c> (this batch plus previously committed batches), so every
    /// ReferenceValueId reaching this method is guaranteed to exist by the time it is written.
    /// </remarks>
    private async Task CreateCsosOnSingleConnectionAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        IReadOnlyList<(Guid OwnerId, ConnectedSystemObjectAttributeValue Value)> attributeValueRows)
    {
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        await using var transaction = await _context.Database.BeginTransactionAsync();

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction)_context.Database.CurrentTransaction!.GetDbTransaction();

        // One transaction for the rows and their values, so the rows carry the synchronisation-pending mark (#2092).
        await BulkInsertCsosOnConnectionAsync(npgsqlConn, npgsqlTx, connectedSystemObjects, synchronisationPending: true);

        if (attributeValueRows.Count > 0)
            await BulkInsertCsoAttributeValuesOnConnectionAsync(npgsqlConn, attributeValueRows, partitionCsoIds: null);

        await transaction.CommitAsync();
        _context.Database.SetCommandTimeout(previousTimeout);

        foreach (var cso in connectedSystemObjects)
            cso.SynchronisationPending = true;
    }

    /// <summary>
    /// Inserts CSO rows on an independent NpgsqlConnection using COPY binary import.
    /// COPY binary streams data directly without SQL parsing or parameter limits,
    /// providing significantly higher throughput than parameterised INSERT.
    /// </summary>
    /// <param name="synchronisationPending">The synchronisation-pending mark (#2092) to write. True where the rows commit
    /// in the same transaction as their attribute values; false on the parallel path, whose rows commit before their
    /// values, so that no synchronisation can load a marked object without its values, process it and clear the mark
    /// (attribute value writes do not move the object's row version). That path marks the rows once every value has
    /// committed, in <see cref="MarkConnectedSystemObjectsSynchronisationPendingOnConnectionAsync"/>.</param>
    private static async Task BulkInsertCsosOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ConnectedSystemObject> objects,
        bool synchronisationPending)
    {
        // Writer order below MUST match CsoBulkColumns.ConnectedSystemObjects exactly.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ConnectedSystemObjects" (
                {BulkSqlHelpers.ToQuotedList(CsoBulkColumns.ConnectedSystemObjects)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var cso in objects)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(cso.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(cso.ConnectedSystemId, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(cso.Created, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            if (cso.LastUpdated.HasValue)
                await writer.WriteAsync(cso.LastUpdated.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(cso.TypeId, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(cso.ExternalIdAttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
            if (cso.SecondaryExternalIdAttributeId.HasValue)
                await writer.WriteAsync(cso.SecondaryExternalIdAttributeId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync((int)cso.Status, NpgsqlTypes.NpgsqlDbType.Integer);
            if (cso.MetaverseObjectId.HasValue)
                await writer.WriteAsync(cso.MetaverseObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync((int)cso.JoinType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (cso.DateJoined.HasValue)
                await writer.WriteAsync(cso.DateJoined.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (cso.PartitionId.HasValue)
                await writer.WriteAsync(cso.PartitionId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(cso.ScopeReviewPending, NpgsqlTypes.NpgsqlDbType.Boolean);
            if (cso.LastScopeEvaluatedAt.HasValue)
                await writer.WriteAsync(cso.LastScopeEvaluatedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            // SPEC-1082 D6: create writers ALWAYS write NULL for ImportStateHash/ImportStateFingerprint.
            // This is a two-phase create: parent CSO rows commit here, in phase 1, before attribute
            // values commit in phase 2. A non-null hash written at this point could survive a phase-2
            // failure as a permanently believable lie about content that was never actually persisted.
            // The only legitimate stamp is StampImportStateAsync, run after values commit (D7/D8).
            await writer.WriteNullAsync();
            await writer.WriteNullAsync();
            await writer.WriteAsync(cso.DerivedInputChangePending, NpgsqlTypes.NpgsqlDbType.Boolean);
            if (cso.JoinMethod.HasValue)
                await writer.WriteAsync((int)cso.JoinMethod.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (cso.JoinSyncRuleId.HasValue)
                await writer.WriteAsync(cso.JoinSyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (cso.JoinSyncRuleName != null)
                await writer.WriteAsync(cso.JoinSyncRuleName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (cso.JoinObjectMatchingRuleId.HasValue)
                await writer.WriteAsync(cso.JoinObjectMatchingRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (cso.JoinMatchValue != null)
                await writer.WriteAsync(cso.JoinMatchValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            // #2092: whether this row is written already announcing itself to synchronisation; see the parameter.
            await writer.WriteAsync(synchronisationPending, NpgsqlTypes.NpgsqlDbType.Boolean);
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Sets the synchronisation-pending mark (#2092) on Connected System Objects the parallel create path wrote with it
    /// unset, in the transaction that commits their attribute values, so the mark becomes visible with the values it
    /// announces and never before them. A narrow single-column statement, exempt from the bulk column list rule.
    /// </summary>
    private static async Task MarkConnectedSystemObjectsSynchronisationPendingOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        await using var command = new NpgsqlCommand(
            @"UPDATE ""ConnectedSystemObjects"" SET ""SynchronisationPending"" = true WHERE ""Id"" = ANY(@ids)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = connectedSystemObjectIds.ToArray() });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts CSO attribute value rows via COPY binary import, on either an independent
    /// NpgsqlConnection (the parallel branch) or the EF connection's own transaction (the
    /// single-connection branch, <see cref="CreateCsosOnSingleConnectionAsync"/>).
    /// </summary>
    /// <param name="partitionCsoIds">CSO IDs being written on THIS connection. ReferenceValueId FKs
    /// pointing to CSOs outside this partition are written as null to avoid FK violations: the
    /// referenced CSO may be on a different parallel connection and not yet committed.
    /// FixupCrossBatchReferenceIdsAsync resolves these after all batches complete. Null (the
    /// single-connection branch's default) means "write every set ReferenceValueId unconditionally",
    /// which is safe there because the whole batch is one transaction on one connection with CSO rows
    /// COPY'd before attribute values, and the caller has already nulled any reference outside the
    /// allowed set before persistence begins.</param>
    private static async Task BulkInsertCsoAttributeValuesOnConnectionAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)> attributeValues,
        HashSet<Guid>? partitionCsoIds = null)
    {
        // Writer order below MUST match CsoBulkColumns.ConnectedSystemObjectAttributeValues exactly.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "ConnectedSystemObjectAttributeValues" (
                {BulkSqlHelpers.ToQuotedList(CsoBulkColumns.ConnectedSystemObjectAttributeValues)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var (csoId, av) in attributeValues)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(av.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(csoId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(av.AttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
            if (av.StringValue is not null)
                await writer.WriteAsync(av.StringValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (av.DateTimeValue.HasValue)
                await writer.WriteAsync(DateTimeAttributeValue.ToCanonical(av.DateTimeValue.Value), NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (av.IntValue.HasValue)
                await writer.WriteAsync(av.IntValue.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (av.LongValue.HasValue)
                await writer.WriteAsync(av.LongValue.Value, NpgsqlTypes.NpgsqlDbType.Bigint);
            else
                await writer.WriteNullAsync();
            if (av.DecimalValue.HasValue)
                await writer.WriteAsync(av.DecimalValue.Value, NpgsqlTypes.NpgsqlDbType.Numeric);
            else
                await writer.WriteNullAsync();
            if (av.ByteValue is not null)
                await writer.WriteAsync(av.ByteValue, NpgsqlTypes.NpgsqlDbType.Bytea);
            else
                await writer.WriteNullAsync();
            if (av.GuidValue.HasValue)
                await writer.WriteAsync(av.GuidValue.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (av.BoolValue.HasValue)
                await writer.WriteAsync(av.BoolValue.Value, NpgsqlTypes.NpgsqlDbType.Boolean);
            else
                await writer.WriteNullAsync();
            // Only write ReferenceValueId if the referenced CSO is in this partition (or no
            // partition filtering is needed). Cross-partition references would violate the FK
            // because the referenced CSO is being written on a different parallel connection.
            if (av.ReferenceValueId.HasValue
                && (partitionCsoIds == null || partitionCsoIds.Contains(av.ReferenceValueId.Value)))
                await writer.WriteAsync(av.ReferenceValueId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (av.UnresolvedReferenceValue is not null)
                await writer.WriteAsync(av.UnresolvedReferenceValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    #endregion

    #region Optimistic Export Apply (issue #1079)

    /// <summary>
    /// Persists an optimistic export apply delta: bulk-inserts new attribute value rows via COPY
    /// binary import and bulk-deletes superseded ones. Touches NO parent CSO row - see the remark
    /// on <see cref="JIM.Data.Repositories.ISyncRepository.ApplyExportedAttributeValuesAsync"/> for
    /// why <c>LastUpdated</c> must stay untouched here (it is what keeps a no-op confirming import
    /// out of the next Delta Synchronisation).
    /// </summary>
    /// <remarks>
    /// The delete, the COPY and the import-state update are one transaction (#2894): the caller's, when it holds one,
    /// otherwise this method's own. Each used to commit on its own, so a COPY or update that failed after the delete left
    /// the object with its superseded values removed and its new ones unwritten, and everything reading it before the
    /// confirming import (a Delta Synchronisation, drift detection, the portal) saw a partial set of values. Tracked
    /// instances of the removed values are detached only once the statements have run, so a failed apply leaves the
    /// tracker matching the rows it left in place.
    /// </remarks>
    public async Task ApplyExportedAttributeValuesAsync(List<ConnectedSystemObjectAttributeValue> additions, List<Guid> removalValueIds, IReadOnlyCollection<Guid> affectedCsoIds)
    {
        if (additions.Count == 0 && removalValueIds.Count == 0)
            return;

        var relational = _context.Database.IsRelational();
        await using (var ownTransaction = relational && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync()
            : null)
        {
            if (removalValueIds.Count > 0)
                await DeleteCsoAttributeValuesByIdAsync(removalValueIds);

            if (additions.Count > 0)
            {
                var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
                await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
                // COPY joins the connection's current transaction (this method's, or the caller's), as the import writer does.
                await BulkInsertCsoAttributeValuesOnConnectionAsync(connection,
                    additions.Select(value => (value.ConnectedSystemObject.Id, value)).ToList(), partitionCsoIds: null);
            }

            // SPEC-1082 D9: null the import state hash/fingerprint for every mutated CSO, set-based,
            // in the same transaction. Deliberately does NOT touch LastUpdated (see the class remarks above).
            if (affectedCsoIds.Count > 0)
                await NullImportStateForCsosAsync(affectedCsoIds);

            if (ownTransaction != null)
                await ownTransaction.CommitAsync();
        }

        // Only now that the removals are written (committed, or in the caller's transaction): a failure above leaves
        // the rows, and so their tracked instances, as they were.
        if (removalValueIds.Count > 0)
            DetachTrackedEntities<ConnectedSystemObjectAttributeValue>(removalValueIds.ToHashSet(), av => av.Id);
    }

    /// <summary>
    /// SPEC-1082 D9: sets ImportStateHash and ImportStateFingerprint to NULL for the given CSO IDs,
    /// via a single set-based UPDATE. Never touches LastUpdated.
    /// </summary>
    /// <remarks>
    /// Only rows with a stamp to clear are updated (#2895). Import stamping (<c>StampImportStateAsync</c>) is the only
    /// writer that sets either column, so a provisioned object's are always NULL, and an unconditional update rewrote every
    /// Create's row for nothing: a dead row version per object, a million over a 500,000-object initial load.
    /// </remarks>
    private async Task NullImportStateForCsosAsync(IReadOnlyCollection<Guid> csoIds)
    {
        if (csoIds.Count == 0)
            return;

        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""ImportStateHash"" = NULL, ""ImportStateFingerprint"" = NULL
              WHERE ""Id"" = ANY({0}) AND (""ImportStateHash"" IS NOT NULL OR ""ImportStateFingerprint"" IS NOT NULL)",
            csoIds.ToArray());
    }

    /// <summary>
    /// Deletes <see cref="ConnectedSystemObjectAttributeValue"/> rows by Id. The caller detaches any tracked instance of
    /// them once its transaction's statements have run (the raw-SQL rule in <c>src/CLAUDE.md</c>; see
    /// <see cref="DetachTrackedEntities{T}"/>'s remarks for the <c>DbUpdateConcurrencyException</c> a tracked instance
    /// left behind causes). Whether anything is tracked depends on the caller's context: the worker's long-lived context
    /// tracks by default, and the parallel export path re-loads each batch tracked, so a batch holding a large group
    /// tracks every one of its member values. The detach is set-based, so it costs one pass over the tracker however
    /// many values are removed (#2813: matching against a list of the removed ids took seconds per batch).
    /// </summary>
    private async Task DeleteCsoAttributeValuesByIdAsync(List<Guid> removalValueIds)
    {
        var removalValueIdSet = removalValueIds.ToHashSet();

        if (_context.Database.IsRelational())
        {
            await _context.ConnectedSystemObjectAttributeValues
                .Where(av => removalValueIds.Contains(av.Id))
                .ExecuteDeleteAsync();
        }
        else
        {
            var entities = await _context.ConnectedSystemObjectAttributeValues
                .Where(av => removalValueIdSet.Contains(av.Id))
                .ToListAsync();
            _context.ConnectedSystemObjectAttributeValues.RemoveRange(entities);
            await _context.SaveChangesAsync();
        }
    }

    #endregion

    #region Partition Placement (#2723)

    /// <inheritdoc />
    public async Task<int> UpdateConnectedSystemObjectPartitionsAsync(IReadOnlyCollection<(Guid ConnectedSystemObjectId, int PartitionId)> placements)
    {
        if (placements.Count == 0)
            return 0;

        // One row per object; were an object given twice, the last placement stands.
        var partitionIdByCsoId = new Dictionary<Guid, int>(placements.Count);
        foreach (var (connectedSystemObjectId, partitionId) in placements)
            partitionIdByCsoId[connectedSystemObjectId] = partitionId;

        // bulk-sql-column-exemption: a narrow, single-column write of a call-site-computed value (where the Connector
        // placed the object), not the Connected System Object write path, so it deliberately writes no other column.
        var changed = await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" AS cso
              SET ""PartitionId"" = placed.partition_id
              FROM unnest({0}, {1}) AS placed(id, partition_id)
              WHERE cso.""Id"" = placed.id
                AND cso.""PartitionId"" IS DISTINCT FROM placed.partition_id",
            new NpgsqlParameter { Value = partitionIdByCsoId.Keys.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid },
            new NpgsqlParameter { Value = partitionIdByCsoId.Values.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer });

        // Every placed object's tracked instance, not only those the statement changed: one that already held the
        // Partition matches it either way, and one that was stale now reads as the row does.
        ConnectedSystemObjectMarkFixUp.Apply(_context, cso => partitionIdByCsoId.ContainsKey(cso.Id),
            cso => cso.PartitionId, cso => (int?)partitionIdByCsoId[cso.Id]);

        return changed;
    }

    #endregion

    #region Cross-Batch Reference Fixup

    /// <summary>
    /// Rows updated per statement by the reference fix-ups (<see cref="FixupCrossBatchReferenceIdsAsync"/> and
    /// <see cref="FixupCrossBatchChangeRecordReferenceIdsAsync"/>) when the caller does not specify a batch size. Sized
    /// so each UPDATE completes comfortably inside <see cref="PostgresDataRepository.BulkOperationCommandTimeoutSeconds"/>:
    /// the Scale500k25kGroups run (2026-07-18) showed a single-statement update of 6.5M rows exceeds 300s, so the write
    /// side must be bounded however many resolutions the candidate step finds.
    /// </summary>
    private const int DefaultReferenceFixupBatchSize = 250_000;

    /// <summary>
    /// Spans for the sync write path's Connected System Object stages, from the same source as the Metaverse Object
    /// stages' (<see cref="MvoActivitySource"/>), picked up by the Worker's diagnostic listener, which listens to every
    /// source named "JIM.*".
    /// </summary>
    private static readonly ActivitySource CsoActivitySource = new("JIM.PostgresData.Sync");

    public async Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId, int? batchSize = null)
    {
        // A Connected System Object's Reference attribute value holds the reference as the connector gave it (a DN, or
        // another Secondary External ID) in UnresolvedReferenceValue, and the referenced CSO's id in ReferenceValueId
        // once the import knows it. The import resolves most references in memory before writing; a value it could not
        // resolve (its object arrives in a later run, or a later batch of this one) is written without the id, and this
        // method resolves those outstanding values of the given Connected System by matching UnresolvedReferenceValue
        // against the Secondary External ID values of CSOs in the same Connected System: ignoring case per RFC 4514,
        // hence the LOWER() comparison, unless the target's attribute is case-exact (#2051). A value that matches no CSO
        // stays outstanding for a later run; a value that already holds an id is never re-pointed.
        //
        // Every reference value keeps its UnresolvedReferenceValue once resolved, so "outstanding" is the pair
        // UnresolvedReferenceValue IS NOT NULL AND ReferenceValueId IS NULL, and two partial indexes hold exactly those
        // rows. The pair is what makes the planner's estimates unusable here, whatever state the statistics are in. With
        // fresh statistics it multiplies the two columns' null fractions as though they were independent, when they are
        // all but exclusive (a reference value almost always has its id, and no other value has either): at most a
        // quarter of the table, hundreds of thousands of rows when a handful are outstanding. With statistics gathered
        // before the outstanding values and their objects arrived, both are estimated at one row. A single statement
        // joining the outstanding values to the Connected System's objects and their Secondary External IDs therefore
        // went wrong both ways (#2706). A Delta Import creating one group on a 500,000-object system spent 8.8 s in it
        // to fix three references. On fresh statistics over 3.3 million values, the UPDATE matched three values by
        // walking the whole LOWER("StringValue") index, and spent 1.4 s of its 2 s compiling JIT code for the cost it
        // had inflated. On stale ones, both the early-exit count and the UPDATE paired every outstanding value with
        // every object in the system, the join condition a filter over the product: 12,000 values and 6,000 objects ran
        // past a 5 s statement timeout in the count alone (CsoReferenceFixupStaleStatisticsDatabaseTests). So, as
        // FixupCrossBatchChangeRecordReferenceIdsAsync does for change records:
        // 1. Read the Connected System's outstanding values into a session temp table, and count them in the same round
        //    trip. The partial index drives the read, and each value's owner is checked by primary key in a scalar
        //    subquery, which the planner evaluates per row rather than turning into a join it could plan as a hash of
        //    every object in the system. Nothing outstanding, the usual case after an import, ends the method here
        //    without reading the target side at all; this replaces the separate early-exit count, which was the
        //    product above.
        // 2. Index the temp table on the folded value and ANALYZE it, so the planner knows exactly how many values are
        //    outstanding. The index makes every join order a lookup: driven from the outstanding values it probes the
        //    target's LOWER() index, and driven from the target's objects it probes this one.
        // 3. Join the temp table to the target side for the resolutions, with the same case-exact rule.
        // Steps 1 and 3 run with JIT off: their estimates are the misestimates above, and on the inflated ones the
        // planner compiles every expression to save time it will not spend (125 ms of compilation to read three rows on
        // a 3.3 million value test table). Each of the two batches turns it off first (TransactionJit), which holds for
        // the batch's own implicit transaction and no longer, so the UPDATEs below run with it on again.
        //
        // The resolutions are applied by primary key in bounded batches, so each statement stays well inside the bulk
        // command timeout however large the backlog (294,000 outstanding values were recorded when the LOWER() indexes
        // were added). Each batch is a separate auto-committed statement; partial progress is durable and the operation
        // is idempotent (a resolved value holds its id, so it drops out of the next invocation's first step), so a
        // failure part way through simply leaves less for the next invocation.
        //
        // The spans carry the change record fix-up's tag names, so the two read alike in a breakdown: markedCount is
        // the number of outstanding values read.
        //
        // The temp tables are scoped to the underlying connection, so every command below must run on the same physical
        // connection: the RawSqlConnectionLease holds it open for the duration. Both are dropped first and on every
        // return; a failure part way leaves them to the next call's leading drop, or to Npgsql's pool reset (DISCARD
        // ALL) when the connection goes back to the pool.
        var effectiveBatchSize = batchSize ?? DefaultReferenceFixupBatchSize;
        if (effectiveBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");

        using var resolveSpan = CsoActivitySource.StartActivity("Cso.ResolveReferences");
        resolveSpan?.SetTag("connectedSystemId", connectedSystemId);

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        const string outstandingTableName = "_jim_cso_reference_outstanding";
        const string resolutionsTableName = "_jim_cso_reference_fixup";
        var dropTempTablesSql = $"""DROP TABLE IF EXISTS "{outstandingTableName}"; DROP TABLE IF EXISTS "{resolutionsTableName}";""";

        long outstandingCount;
        long candidateCount = 0;
        using (var findSpan = CsoActivitySource.StartActivity("Cso.ResolveReferences.FindCandidates"))
        {
            // The id test is what makes a value outstanding, so this only ever fills in a missing id and never replaces
            // one. Every value belongs to a CSO, so the scalar subquery always returns exactly one row.
            outstandingCount = await ExecuteReferenceFixupCountWithJitOffAsync(npgsqlConn, connectedSystemId, $"""
                {dropTempTablesSql}
                CREATE TEMP TABLE "{outstandingTableName}" AS
                SELECT av."Id" AS av_id, av."UnresolvedReferenceValue" AS reference_value, LOWER(av."UnresolvedReferenceValue") AS lower_value
                FROM "ConnectedSystemObjectAttributeValues" av
                WHERE av."UnresolvedReferenceValue" IS NOT NULL
                  AND av."ReferenceValueId" IS NULL
                  AND (SELECT owner_cso."ConnectedSystemId" FROM "ConnectedSystemObjects" owner_cso
                       WHERE owner_cso."Id" = av."ConnectedSystemObjectId") = @connectedSystemId;
                SELECT COUNT(*) FROM "{outstandingTableName}";
                """);
            findSpan?.SetTag("markedCount", outstandingCount);

            if (outstandingCount > 0)
            {
                candidateCount = await ExecuteReferenceFixupCountWithJitOffAsync(npgsqlConn, connectedSystemId, $"""
                    CREATE INDEX ON "{outstandingTableName}" (lower_value);
                    ANALYZE "{outstandingTableName}";
                    CREATE TEMP TABLE "{resolutionsTableName}" AS
                    SELECT row_number() OVER () AS rn, s.av_id, s.target_id
                    FROM (
                        SELECT o.av_id, target_cso."Id" AS target_id
                        FROM "{outstandingTableName}" o
                        JOIN "ConnectedSystemObjectAttributeValues" target_av ON LOWER(target_av."StringValue") = o.lower_value
                            AND target_av."StringValue" IS NOT NULL
                        JOIN "ConnectedSystemAttributes" target_attr ON target_attr."Id" = target_av."AttributeId"
                            AND target_attr."IsSecondaryExternalId" = true
                        JOIN "ConnectedSystemObjects" target_cso ON target_cso."Id" = target_av."ConnectedSystemObjectId"
                            AND target_cso."ConnectedSystemId" = @connectedSystemId
                        WHERE NOT target_attr."CaseExact" OR o.reference_value = target_av."StringValue"
                    ) s;
                    CREATE INDEX ON "{resolutionsTableName}" (rn);
                    SELECT COUNT(*) FROM "{resolutionsTableName}";
                    """);
            }

            findSpan?.SetTag("candidateCount", candidateCount);
        }

        resolveSpan?.SetTag("markedCount", outstandingCount);
        resolveSpan?.SetTag("candidateCount", candidateCount);

        if (outstandingCount == 0)
        {
            await ExecuteReferenceFixupSqlAsync(npgsqlConn, connectedSystemId, dropTempTablesSql);
            resolveSpan?.SetTag("resolvedCount", 0);
            return 0;
        }

        var resolvedCount = 0;
        using (var applySpan = CsoActivitySource.StartActivity("Cso.ResolveReferences.Apply"))
        {
            if (candidateCount > 0)
            {
                // Joined to the attribute values by primary key, so each batch is one index probe per resolution whatever
                // the statistics say. The id test only rechecks the probed row: on its own it implies neither partial
                // index's predicate, so it cannot hand the planner an outstanding-values index to drive the join from
                // (the misestimate FixupCrossBatchChangeRecordReferenceIdsAsync records for its mark). It is also what
                // keeps a value naming two objects, which only a duplicated Secondary External ID produces, to one of
                // them, updated and counted once as the single UPDATE did, when its two resolutions fall in different
                // batches.
                await using var updateCmd = new NpgsqlCommand($"""
                    UPDATE "ConnectedSystemObjectAttributeValues" av
                    SET "ReferenceValueId" = f.target_id
                    FROM "{resolutionsTableName}" f
                    WHERE f.rn > @rangeStart AND f.rn <= @rangeEnd
                      AND av."Id" = f.av_id
                      AND av."ReferenceValueId" IS NULL
                    """, npgsqlConn);
                updateCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
                var rangeStartParam = updateCmd.Parameters.Add("rangeStart", NpgsqlTypes.NpgsqlDbType.Bigint);
                var rangeEndParam = updateCmd.Parameters.Add("rangeEnd", NpgsqlTypes.NpgsqlDbType.Bigint);

                for (long rangeStart = 0; rangeStart < candidateCount; rangeStart += effectiveBatchSize)
                {
                    rangeStartParam.Value = rangeStart;
                    rangeEndParam.Value = rangeStart + effectiveBatchSize;
                    resolvedCount += await updateCmd.ExecuteNonQueryAsync();
                    Log.Information("FixupCrossBatchReferenceIdsAsync: Resolved {Resolved:N0} of {Total:N0} references for Connected System {ConnectedSystemId}",
                        resolvedCount, candidateCount, connectedSystemId);
                }
            }

            applySpan?.SetTag("resolvedCount", resolvedCount);
        }

        await ExecuteReferenceFixupSqlAsync(npgsqlConn, connectedSystemId, dropTempTablesSql);

        resolveSpan?.SetTag("resolvedCount", resolvedCount);
        return resolvedCount;
    }

    /// <summary>
    /// Runs one of the reference fix-ups' statements (or a batch of them, one implicit transaction) on the given
    /// connection under the bulk command timeout, binding <c>@connectedSystemId</c> where the SQL uses it.
    /// </summary>
    private static async Task<int> ExecuteReferenceFixupSqlAsync(NpgsqlConnection connection, int connectedSystemId, string sql)
    {
        await using var command = NewReferenceFixupCommand(connection, connectedSystemId, sql);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// As <see cref="ExecuteReferenceFixupSqlAsync"/>, returning the count the batch's final <c>SELECT COUNT(*)</c>
    /// reads; the statements before it return no rows.
    /// </summary>
    private static async Task<long> ExecuteReferenceFixupScalarAsync(NpgsqlConnection connection, int connectedSystemId, string sql)
    {
        await using var command = NewReferenceFixupCommand(connection, connectedSystemId, sql);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>
    /// As <see cref="ExecuteReferenceFixupScalarAsync"/>, with JIT off for the batch's own implicit transaction
    /// (<see cref="TransactionJit"/>): the statement turning it off is sent first, and its result is stepped past, so
    /// the count is the one result after it with columns, the batch's final <c>SELECT COUNT(*)</c>.
    /// </summary>
    private static async Task<long> ExecuteReferenceFixupCountWithJitOffAsync(NpgsqlConnection connection, int connectedSystemId, string sql)
    {
        await using var command = NewReferenceFixupCommand(connection, connectedSystemId, $"{TransactionJit.DisableSql};\n{sql}");
        await using var reader = await command.ExecuteReaderAsync();
        if (await TransactionJit.SkipDisableResultAsync(reader))
        {
            do
            {
                if (reader.FieldCount > 0 && await reader.ReadAsync())
                    return reader.GetInt64(0);
            }
            while (await reader.NextResultAsync());
        }

        throw new InvalidOperationException("The reference fix-up's batch returned no count.");
    }

    private static NpgsqlCommand NewReferenceFixupCommand(NpgsqlConnection connection, int connectedSystemId, string sql)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds };
        if (sql.Contains("@connectedSystemId", StringComparison.Ordinal))
            command.Parameters.AddWithValue("connectedSystemId", connectedSystemId);
        return command;
    }

    public async Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId, int? batchSize = null)
    {
        // Change record attribute values (ConnectedSystemObjectChangeAttributeValues) hold a reference's DN in
        // StringValue, and its referenced CSO's id in ReferenceValueId when the writer could write it. The writer
        // cannot when the referenced CSO's row does not exist yet (an import writes new objects in batches with their
        // ids decided up front, so a reference can name an object a later batch writes) or when the reference named no
        // object at all (an import reference that matched nothing yet, or an export snapshot naming its objects by
        // identifier). Every writer marks exactly those values IsUnresolvedReference
        // (ConnectedSystemObjectChangeAttributeValue.SetUnresolvedReferenceMark), and this method resolves the marked
        // values of the given Connected System's change records by matching StringValue against the secondary external
        // ID attribute values of CSOs in the same Connected System: ignoring case per RFC 4514, hence the LOWER()
        // comparison, unless the target's attribute is case-exact (#2051). Writing the id clears the mark.
        //
        // Only marked values are read (#2690), through the partial index on the mark, so the candidate step costs what
        // is outstanding rather than a scan of every string value of change history (19.8 million rows, 20 to 40 s a
        // run, on a 500,000-object database). A value whose id was written is never marked, so when its referenced
        // CSO is later deleted and the foreign key's ON DELETE SET NULL clears the id, it keeps the DN it was recorded
        // with and is not re-pointed at a later object that reuses the DN. A marked value that matches no CSO stays
        // marked and is evaluated again by the next invocation, since its object may arrive in a later run.
        //
        // The candidate step must not depend on the tables' statistics, because they are routinely stale exactly when
        // it matters: an initial export writes thousands of marked references to objects it provisioned in the same
        // run, and the statistics were last gathered (by autovacuum, during the import and synchronisation) while the
        // target Connected System had no objects and nothing was marked. Planned as one join, the marked values and the
        // target's objects were then each estimated at one row, and the planner paired every marked value with every
        // target object, the LOWER() match a filter over the product rather than a lookup: Scenario 032 at Medium ran
        // into the 300 s statement timeout during the export, and 12,000 marked values against 6,000 objects take 64 s
        // (ChangeRecordReferenceFixupStaleStatisticsDatabaseTests). So the candidate step runs in three statements:
        // 1. Read the Connected System's marked values into a session temp table. The partial index is the statement's
        //    only selective condition, and every plan measured drives from it, probing each marked value's change
        //    record by primary key. Nothing marked, which is the usual case, ends the method here without reading the
        //    target side at all.
        // 2. Index the temp table on the folded value and ANALYZE it, so the planner knows exactly how many marked
        //    values there are. The index makes every join order a lookup: driven from the marked values it probes the
        //    target's LOWER() index, and driven from the target's objects (still misestimated) it probes this one.
        // 3. Join the temp table to the target side for the resolutions, with the same case-exact rule.
        //
        // The resolutions are applied in bounded batches because a single UPDATE does not survive
        // customer scale: the Scale500k25kGroups run accumulated 6.5M unresolved rows across the sync
        // and export stages and a single-statement UPDATE blew the bulk command timeout, hard-failing
        // the confirming import. Each batch is a separate auto-committed statement well inside the
        // timeout. Partial progress is durable and the operation is idempotent (a resolved row loses
        // its mark, so it drops out of the next candidate step), so a failure part way through simply
        // leaves less for the next invocation.
        //
        // The temp tables are scoped to the underlying connection, so every command below must run on
        // the same physical connection: the RawSqlConnectionLease holds it open for the duration. Both
        // are dropped first and on every return; a failure part way leaves them to the next call's
        // leading drop, or to Npgsql's pool reset (DISCARD ALL) when the connection goes back to the pool.
        var effectiveBatchSize = batchSize ?? DefaultReferenceFixupBatchSize;
        if (effectiveBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");

        using var resolveSpan = CsoActivitySource.StartActivity("Cso.ResolveChangeReferences");
        resolveSpan?.SetTag("connectedSystemId", connectedSystemId);

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        const string markedTableName = "_jim_change_record_reference_marked";
        const string resolutionsTableName = "_jim_change_record_reference_fixup";

        Task<int> ExecuteAsync(string sql) => ExecuteReferenceFixupSqlAsync(npgsqlConn, connectedSystemId, sql);

        Task<long> CountAsync(string tableName) =>
            ExecuteReferenceFixupScalarAsync(npgsqlConn, connectedSystemId, $"""SELECT COUNT(*) FROM "{tableName}";""");

        Task DropTempTablesAsync() =>
            ExecuteAsync($"""DROP TABLE IF EXISTS "{markedTableName}"; DROP TABLE IF EXISTS "{resolutionsTableName}";""");

        long markedCount;
        long candidateCount = 0;
        using (var findSpan = CsoActivitySource.StartActivity("Cso.ResolveChangeReferences.FindCandidates"))
        {
            await DropTempTablesAsync();

            // The mark alone says the value is a reference still to resolve: the writers set it only on values of a
            // Reference attribute holding a DN and written without the id. The id test is belt and braces, so this
            // only ever fills in a missing id and never replaces one.
            await ExecuteAsync($"""
                CREATE TEMP TABLE "{markedTableName}" AS
                SELECT cav."Id" AS cav_id, cav."StringValue" AS string_value, LOWER(cav."StringValue") AS lower_value
                FROM "ConnectedSystemObjectChangeAttributeValues" cav
                JOIN "ConnectedSystemObjectChangeAttributes" ca ON ca."Id" = cav."ConnectedSystemObjectChangeAttributeId"
                JOIN "ConnectedSystemObjectChanges" cc ON cc."Id" = ca."ConnectedSystemChangeId"
                WHERE cav."IsUnresolvedReference"
                  AND cav."ReferenceValueId" IS NULL
                  AND cc."ConnectedSystemId" = @connectedSystemId;
                """);
            markedCount = await CountAsync(markedTableName);
            findSpan?.SetTag("markedCount", markedCount);

            if (markedCount > 0)
            {
                await ExecuteAsync($"""
                    CREATE INDEX ON "{markedTableName}" (lower_value);
                    ANALYZE "{markedTableName}";
                    """);

                await ExecuteAsync($"""
                    CREATE TEMP TABLE "{resolutionsTableName}" AS
                    SELECT row_number() OVER () AS rn, s.cav_id, s.target_id
                    FROM (
                        SELECT m.cav_id, target_cso."Id" AS target_id
                        FROM "{markedTableName}" m
                        JOIN "ConnectedSystemObjectAttributeValues" target_av ON LOWER(target_av."StringValue") = m.lower_value
                        JOIN "ConnectedSystemAttributes" target_attr ON target_attr."Id" = target_av."AttributeId"
                            AND target_attr."IsSecondaryExternalId" = true
                        JOIN "ConnectedSystemObjects" target_cso ON target_cso."Id" = target_av."ConnectedSystemObjectId"
                            AND target_cso."ConnectedSystemId" = @connectedSystemId
                        WHERE NOT target_attr."CaseExact" OR m.string_value = target_av."StringValue"
                    ) s;
                    CREATE INDEX ON "{resolutionsTableName}" (rn);
                    """);
                candidateCount = await CountAsync(resolutionsTableName);
            }

            findSpan?.SetTag("candidateCount", candidateCount);
        }

        resolveSpan?.SetTag("markedCount", markedCount);
        resolveSpan?.SetTag("candidateCount", candidateCount);

        if (markedCount == 0)
        {
            await DropTempTablesAsync();
            resolveSpan?.SetTag("resolvedCount", 0);
            return 0;
        }

        var resolvedCount = 0;
        using (var applySpan = CsoActivitySource.StartActivity("Cso.ResolveChangeReferences.Apply"))
        {
            if (candidateCount > 0)
            {
                // Joined to the change values by primary key and nothing else, so each batch is one index probe per
                // resolution whatever the statistics say. A predicate on the mark here would hand the planner the
                // partial index, estimated (staleness again) at one row, to drive the join from, and it then rescans
                // the batch once per marked value: 12,000 resolutions took 25 s that way. A value matching two objects
                // with the same DN, which only a duplicated Secondary External ID produces, has two resolutions and
                // takes one of them; when they fall in different batches it is updated, and counted, twice.
                await using var updateCmd = new NpgsqlCommand($"""
                    UPDATE "ConnectedSystemObjectChangeAttributeValues" cav
                    SET "ReferenceValueId" = f.target_id, "IsUnresolvedReference" = false
                    FROM "{resolutionsTableName}" f
                    WHERE f.rn > @rangeStart AND f.rn <= @rangeEnd
                      AND cav."Id" = f.cav_id
                    """, npgsqlConn);
                updateCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
                var rangeStartParam = updateCmd.Parameters.Add("rangeStart", NpgsqlTypes.NpgsqlDbType.Bigint);
                var rangeEndParam = updateCmd.Parameters.Add("rangeEnd", NpgsqlTypes.NpgsqlDbType.Bigint);

                for (long rangeStart = 0; rangeStart < candidateCount; rangeStart += effectiveBatchSize)
                {
                    rangeStartParam.Value = rangeStart;
                    rangeEndParam.Value = rangeStart + effectiveBatchSize;
                    resolvedCount += await updateCmd.ExecuteNonQueryAsync();
                    Log.Information("FixupCrossBatchChangeRecordReferenceIdsAsync: Resolved {Resolved:N0} of {Total:N0} change record references for Connected System {ConnectedSystemId}",
                        resolvedCount, candidateCount, connectedSystemId);
                }
            }

            applySpan?.SetTag("resolvedCount", resolvedCount);
        }

        await DropTempTablesAsync();

        resolveSpan?.SetTag("resolvedCount", resolvedCount);
        return resolvedCount;
    }

    #endregion

    #region Pending Export — Worker-Only Bulk Operations

    public async Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
    {
        var csoIds = connectedSystemObjectIds.ToArray();
        if (csoIds.Length == 0)
            return 0;

        ForgetQueuedForCsoIds(csoIds);

        // Instances of the rows this method is about to delete may be tracked on the worker's
        // long-lived context (reconciliation and navigation fix-up both track Pending Exports).
        // Detach them before the raw SQL deletes: PendingExport.SourceMetaverseObject is
        // configured SetNull-on-delete, so a tracked instance left behind makes EF Core's
        // cascade fix-up issue an UPDATE against the already-deleted row when its source MVO is
        // deleted in the same page flush; that matches zero rows and throws
        // DbUpdateConcurrencyException, poisoning every later SaveChangesAsync on the context
        // (Scenario-004-DeletionRules Test 3 failure, issue #993).
        var pendingExportIds = (await _context.PendingExports
            .Where(pe => pe.ConnectedSystemObjectId != null && csoIds.Contains(pe.ConnectedSystemObjectId.Value))
            .Select(pe => pe.Id)
            .ToListAsync())
            .ToHashSet();
        DetachTrackedChildEntities(pendingExportIds);
        DetachTrackedEntities<PendingExport>(pendingExportIds, pe => pe.Id);

        // Use raw SQL for performance and to avoid change tracker identity conflicts.
        // After ClearChangeTracker(), loading PEs with Include chains would create
        // MetaverseAttribute instances that conflict with instances already tracked by the
        // cross-page CSO query, causing identity resolution failures.
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""PendingExportAttributeValueChanges""
              WHERE ""PendingExportId"" IN (
                  SELECT pe.""Id"" FROM ""PendingExports"" pe
                  WHERE pe.""ConnectedSystemObjectId"" = ANY({0})
              )",
            csoIds);

        // Delete parent records and return count
        var deleted = await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""PendingExports"" WHERE ""ConnectedSystemObjectId"" = ANY({0})",
            csoIds);

        return deleted;
    }

    /// <summary>
    /// Deletes Connected System Objects by id (see <see cref="ISyncRepository.DeleteConnectedSystemObjectsByIdsAsync"/>),
    /// through the one raw delete the Connected System repository's deletion also uses
    /// (<see cref="ConnectedSystemObjectDeletionSql"/>, #2669). Used for a successful Delete export of a CSO whose
    /// provisioning was never confirmed (<c>ExportExecutionServer</c>'s unconfirmed-provisioning-delete rule): the export
    /// batch loads the CSO graph <c>AsNoTracking()</c>, so this deletes by id and attaches nothing.
    /// </summary>
    public Task<int> DeleteConnectedSystemObjectsByIdsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
        => ConnectedSystemObjectDeletionSql.DeleteAsync(_context, connectedSystemObjectIds);

    public async Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
    {
        var exportList = untrackedPendingExports.ToList();
        if (exportList.Count == 0)
            return;

        ForgetQueued(exportList);
        var pendingExportIds = exportList.Select(pe => pe.Id).ToList();

        // Delete children first, then parents. Must delete ALL children from the database,
        // not just the ones loaded in the untracked entity's collection. The change tracker
        // may contain additional tracked children from the import phase (via navigation fixup)
        // that weren't loaded by the AsNoTracking query.

        // Detach any tracked children and parents to prevent EF Core's ClientSetNull
        // behaviour from interfering with our explicit deletion order.
        var pendingExportIdSet = pendingExportIds.ToHashSet();
        DetachTrackedChildEntities(pendingExportIdSet);
        DetachTrackedEntities<PendingExport>(pendingExportIdSet, e => e.Id);

        if (_context.Database.IsRelational())
        {
            // Relational databases: use ExecuteDeleteAsync for direct SQL DELETE.
            // Bypasses change tracker entirely, guaranteed correct ordering.
            await _context.PendingExportAttributeValueChanges
                .Where(avc => EF.Property<Guid?>(avc, "PendingExportId") != null && pendingExportIds.Contains(EF.Property<Guid?>(avc, "PendingExportId")!.Value))
                .ExecuteDeleteAsync();

            await _context.PendingExports
                .Where(pe => pendingExportIds.Contains(pe.Id))
                .ExecuteDeleteAsync();
        }
        else
        {
            // InMemory provider (tests): ExecuteDeleteAsync not supported.
            // Load and remove children, flush, then remove parents, flush.
            var children = await _context.PendingExportAttributeValueChanges
                .Where(avc => EF.Property<Guid?>(avc, "PendingExportId") != null && pendingExportIds.Contains(EF.Property<Guid?>(avc, "PendingExportId")!.Value))
                .ToListAsync();
            _context.PendingExportAttributeValueChanges.RemoveRange(children);
            await _context.SaveChangesAsync();

            var parents = await _context.PendingExports
                .Where(pe => pendingExportIds.Contains(pe.Id))
                .ToListAsync();
            _context.PendingExports.RemoveRange(parents);
            await _context.SaveChangesAsync();
        }
    }

    public async Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
    {
        var exportList = untrackedPendingExports.ToList();
        if (exportList.Count == 0)
            return;

        ThrowIfAnyQueued(exportList.Select(pe => pe.Id), nameof(UpdateUntrackedPendingExportsAsync));

        if (!_context.Database.IsRelational())
        {
            // InMemory provider (tests): use EF Core change tracker approach
            foreach (var pe in exportList)
            {
                var tracked = await _context.PendingExports.FindAsync(pe.Id);
                if (tracked != null)
                {
                    tracked.Status = pe.Status;
                    tracked.ChangeType = pe.ChangeType;
                    tracked.ErrorCount = pe.ErrorCount;
                    tracked.FailingSince = pe.FailingSince;
                    tracked.LastAttemptedAt = pe.LastAttemptedAt;
                    tracked.LastErrorMessage = pe.LastErrorMessage;
                    tracked.LastErrorStackTrace = pe.LastErrorStackTrace;
                    tracked.HasUnresolvedReferences = pe.HasUnresolvedReferences;
                }

                foreach (var attrChange in pe.AttributeValueChanges)
                {
                    var trackedAttr = await _context.PendingExportAttributeValueChanges.FindAsync(attrChange.Id);
                    if (trackedAttr != null)
                    {
                        trackedAttr.Status = attrChange.Status;
                        trackedAttr.LastImportedValue = attrChange.LastImportedValue;
                        trackedAttr.ExportAttemptCount = attrChange.ExportAttemptCount;
                        trackedAttr.LastExportedAt = attrChange.LastExportedAt;
                        trackedAttr.AwaitsInitialPassword = attrChange.AwaitsInitialPassword;
                        trackedAttr.AwaitsExchange = attrChange.AwaitsExchange;
                    }
                }
            }
            await _context.SaveChangesAsync();
            return;
        }

        // Relational databases: use COPY binary + UPDATE FROM for bulk updates.
        // This bypasses the change tracker entirely, avoiding O(n) tracker scans per entity
        // that caused multi-minute stalls at 100K scale.
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        // The temp tables below are session-scoped and only dropped on the success path, so a call that
        // fails part-way leaves its rows on the physical connection. Truncate on every use rather than
        // relying on Npgsql's pool reset (DISCARD ALL) to have run in between: a connection held open
        // across calls, or No Reset On Close, would otherwise feed those stale rows into the UPDATE ... FROM
        // below and overwrite unrelated Pending Exports with an earlier call's values.

        // 1. Bulk update PendingExports
        await using (var createCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
        {
            createCmd.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS _pe_bulk_update (
                    "Id" uuid NOT NULL,
                    "Status" int,
                    "ChangeType" int,
                    "ErrorCount" int,
                    "FailingSince" timestamptz,
                    "LastAttemptedAt" timestamptz,
                    "LastErrorMessage" text,
                    "LastErrorStackTrace" text,
                    "HasUnresolvedReferences" boolean
                ) ON COMMIT PRESERVE ROWS;
                TRUNCATE _pe_bulk_update
                """;
            await createCmd.ExecuteNonQueryAsync();
        }

        // Writer order below MUST match PendingExportBulkColumns.PendingExportsRetryUpdate exactly.
        await using (var writer = await npgsqlConn.BeginBinaryImportAsync(
            $"""COPY _pe_bulk_update ("Id", {BulkSqlHelpers.ToQuotedList(PendingExportBulkColumns.PendingExportsRetryUpdate)}) FROM STDIN (FORMAT binary)"""))
        {
            foreach (var pe in exportList)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(pe.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
                await writer.WriteAsync((int)pe.Status, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync((int)pe.ChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync(pe.ErrorCount, NpgsqlTypes.NpgsqlDbType.Integer);
                if (pe.FailingSince.HasValue)
                    await writer.WriteAsync(pe.FailingSince.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                else
                    await writer.WriteNullAsync();
                if (pe.LastAttemptedAt.HasValue)
                    await writer.WriteAsync(pe.LastAttemptedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                else
                    await writer.WriteNullAsync();
                if (pe.LastErrorMessage is not null)
                    await writer.WriteAsync(pe.LastErrorMessage, NpgsqlTypes.NpgsqlDbType.Text);
                else
                    await writer.WriteNullAsync();
                if (pe.LastErrorStackTrace is not null)
                    await writer.WriteAsync(pe.LastErrorStackTrace, NpgsqlTypes.NpgsqlDbType.Text);
                else
                    await writer.WriteNullAsync();
                await writer.WriteAsync(pe.HasUnresolvedReferences, NpgsqlTypes.NpgsqlDbType.Boolean);
            }
            await writer.CompleteAsync();
        }

        await using (var updateCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
        {
            updateCmd.CommandText = $"""
                UPDATE "PendingExports" t
                SET {string.Join(", ", PendingExportBulkColumns.PendingExportsRetryUpdate.Select(c => $"\"{c}\" = v.\"{c}\""))}
                FROM _pe_bulk_update v
                WHERE t."Id" = v."Id"
                """;
            await updateCmd.ExecuteNonQueryAsync();
        }

        // 2. Bulk update PendingExportAttributeValueChanges
        var allAttrChanges = exportList
            .SelectMany(pe => pe.AttributeValueChanges)
            .ToList();

        if (allAttrChanges.Count > 0)
        {
            await using (var createCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                createCmd.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS _peavc_bulk_update (
                        "Id" uuid NOT NULL,
                        "Status" int,
                        "LastImportedValue" text,
                        "ExportAttemptCount" int,
                        "LastExportedAt" timestamptz,
                        "AwaitsInitialPassword" boolean,
                        "AwaitsExchange" boolean
                    ) ON COMMIT PRESERVE ROWS;
                    TRUNCATE _peavc_bulk_update
                    """;
                await createCmd.ExecuteNonQueryAsync();
            }

            // Writer order below MUST match PendingExportBulkColumns.PendingExportAttributeValueChangesConfirmationUpdate exactly.
            await using (var writer = await npgsqlConn.BeginBinaryImportAsync(
                $"""COPY _peavc_bulk_update ("Id", {BulkSqlHelpers.ToQuotedList(PendingExportBulkColumns.PendingExportAttributeValueChangesConfirmationUpdate)}) FROM STDIN (FORMAT binary)"""))
            {
                foreach (var avc in allAttrChanges)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(avc.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
                    await writer.WriteAsync((int)avc.Status, NpgsqlTypes.NpgsqlDbType.Integer);
                    if (avc.LastImportedValue is not null)
                        await writer.WriteAsync(avc.LastImportedValue, NpgsqlTypes.NpgsqlDbType.Text);
                    else
                        await writer.WriteNullAsync();
                    await writer.WriteAsync(avc.ExportAttemptCount, NpgsqlTypes.NpgsqlDbType.Integer);
                    if (avc.LastExportedAt.HasValue)
                        await writer.WriteAsync(avc.LastExportedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                    else
                        await writer.WriteNullAsync();
                    await writer.WriteAsync(avc.AwaitsInitialPassword, NpgsqlTypes.NpgsqlDbType.Boolean);
                    await writer.WriteAsync(avc.AwaitsExchange, NpgsqlTypes.NpgsqlDbType.Boolean);
                }
                await writer.CompleteAsync();
            }

            await using (var updateCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                updateCmd.CommandText = $"""
                    UPDATE "PendingExportAttributeValueChanges" t
                    SET {string.Join(", ", PendingExportBulkColumns.PendingExportAttributeValueChangesConfirmationUpdate.Select(c => $"\"{c}\" = v.\"{c}\""))}
                    FROM _peavc_bulk_update v
                    WHERE t."Id" = v."Id"
                    """;
                await updateCmd.ExecuteNonQueryAsync();
            }
        }

        // Clean up temp tables
        await using (var dropCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
        {
            dropCmd.CommandText = """
                DROP TABLE IF EXISTS _pe_bulk_update;
                DROP TABLE IF EXISTS _peavc_bulk_update
                """;
            await dropCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DeleteUntrackedPendingExportAttributeValueChangesAsync(IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges)
    {
        var changeList = untrackedAttributeValueChanges.ToList();
        if (changeList.Count == 0)
            return;

        ThrowIfAnyQueued(changeList.Where(c => c.PendingExportId.HasValue).Select(c => c.PendingExportId!.Value),
            nameof(DeleteUntrackedPendingExportAttributeValueChangesAsync));

        var changeIds = changeList.Select(c => c.Id).ToList();

        // Detach any tracked instances to prevent change tracker conflicts
        DetachTrackedEntities<PendingExportAttributeValueChange>(changeIds.ToHashSet(), e => e.Id);

        if (_context.Database.IsRelational())
        {
            await _context.PendingExportAttributeValueChanges
                .Where(avc => changeIds.Contains(avc.Id))
                .ExecuteDeleteAsync();
        }
        else
        {
            var entities = await _context.PendingExportAttributeValueChanges
                .Where(avc => changeIds.Contains(avc.Id))
                .ToListAsync();
            _context.PendingExportAttributeValueChanges.RemoveRange(entities);
            await _context.SaveChangesAsync();
        }
    }

    #endregion

    /// <summary>
    /// Names the Run Profile Execution Item that queued each Pending Export, after both exist (#1223): the
    /// set-once fix-up for the deletion-cascade and reference-recall paths, whose Pending Exports are persisted
    /// before the item reporting each one is built. The item becomes the latest stager and joins the export's
    /// stagers (#2100) as <see cref="PendingExport.RecordStager"/> does in memory: once, last, and keeping only
    /// the most recent <see cref="PendingExport.MaxRecordedStagers"/>. A deliberately narrow statement rather
    /// than a PendingExportBulkColumns writer, per the raw-SQL column-list rules' targeted-mutation exemption.
    /// </summary>
    public async Task SetPendingExportQueueingItemsAsync(
        IReadOnlyCollection<(Guid PendingExportId, Guid QueuedByRunProfileExecutionItemId)> stamps)
    {
        if (stamps.Count == 0)
            return;

        ThrowIfAnyQueued(stamps.Select(stamp => stamp.PendingExportId), nameof(SetPendingExportQueueingItemsAsync));
        var pendingExportIds = stamps.Select(stamp => stamp.PendingExportId).ToArray();
        var queueingItemIds = stamps.Select(stamp => stamp.QueuedByRunProfileExecutionItemId).ToArray();

        // The list with the item appended unless already present, then sliced to its most recent entries:
        // PostgreSQL arrays are 1-based, so the slice starts at max(1, length - cap + 1).
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""PendingExports"" AS pe
              SET ""QueuedByRunProfileExecutionItemId"" = stamp.queueing_item_id,
                  ""StagedByRunProfileExecutionItemIds"" = CASE
                      WHEN stamp.queueing_item_id = ANY(pe.""StagedByRunProfileExecutionItemIds"")
                          THEN pe.""StagedByRunProfileExecutionItemIds""
                      ELSE (array_append(pe.""StagedByRunProfileExecutionItemIds"", stamp.queueing_item_id))
                          [GREATEST(1, cardinality(pe.""StagedByRunProfileExecutionItemIds"") + 2 - {2}):]
                  END
              FROM unnest({0}, {1}) AS stamp(pending_export_id, queueing_item_id)
              WHERE pe.""Id"" = stamp.pending_export_id",
            new Npgsql.NpgsqlParameter { Value = pendingExportIds, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid },
            new Npgsql.NpgsqlParameter { Value = queueingItemIds, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid },
            new Npgsql.NpgsqlParameter { Value = PendingExport.MaxRecordedStagers, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
    }

    public async Task<HashSet<Guid>> GetConnectedSystemObjectIdsAwaitingInitialPasswordAsync(int connectedSystemId, IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return [];

        // Raw SQL, asked once per synchronisation page with drift to evaluate (#2039). Led by the partial index on the
        // flag (IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsInitialPassword), which holds only the
        // changes awaiting an Initial Password, so the probe is near-free where none exists; each hit joins to its
        // Pending Export by primary key.
        const string sql = """
            SELECT DISTINCT pe."ConnectedSystemObjectId"
            FROM "PendingExportAttributeValueChanges" avc
            JOIN "PendingExports" pe ON pe."Id" = avc."PendingExportId"
            WHERE avc."AwaitsInitialPassword"
              AND avc."Status" = @awaitingConfirmation
              AND pe."ConnectedSystemId" = @connectedSystemId
              AND pe."ConnectedSystemObjectId" = ANY(@connectedSystemObjectIds)
            """;

        return await ReadGuidSetAsync(sql,
            new NpgsqlParameter("awaitingConfirmation", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation },
            new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId },
            new NpgsqlParameter("connectedSystemObjectIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = connectedSystemObjectIds.ToArray() });
    }

    public async Task<AccountEnablementFacts> GetAccountEnablementFactsAsync(int connectedSystemId, Guid pendingPasswordChangeId, Guid connectedSystemObjectId,
        DateTime exportsInFlightSince)
    {
        // Raw SQL, asked once per delivery attempt (#2064, #2173). The hold is the same probe as
        // GetConnectedSystemObjectIdsAwaitingInitialPasswordAsync, led by the partial index on the flag, so it is
        // near-free where nothing awaits a password; the mark is a primary-key read of the queued change; the export in
        // flight, the stranded one and whether the one in flight is the account's Create (#2160) read the account's one
        // Pending Export through the unique index on its object.
        // LastAttemptedAt is when MarkPendingExportsAsExecutingAsync took the export into Executing; a missing one is
        // read as in flight, never stranded, because waiting is the safe side of not knowing. A read-side projection,
        // so exempt from the bulk column list rule.
        const string sql = """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM "PendingExportAttributeValueChanges" avc
                    JOIN "PendingExports" pe ON pe."Id" = avc."PendingExportId"
                    WHERE avc."AwaitsInitialPassword"
                      AND avc."Status" = @awaitingConfirmation
                      AND pe."ConnectedSystemId" = @connectedSystemId
                      AND pe."ConnectedSystemObjectId" = @connectedSystemObjectId),
                COALESCE((SELECT ppc."EnabledStateSetByAttributeFlow" FROM "PendingPasswordChanges" ppc WHERE ppc."Id" = @pendingPasswordChangeId), FALSE),
                EXISTS (
                    SELECT 1
                    FROM "PendingExports" pe
                    WHERE pe."ConnectedSystemObjectId" = @connectedSystemObjectId
                      AND pe."ConnectedSystemId" = @connectedSystemId
                      AND pe."Status" = @executing
                      AND (pe."LastAttemptedAt" IS NULL OR pe."LastAttemptedAt" >= @exportsInFlightSince)),
                (SELECT pe."Id"
                    FROM "PendingExports" pe
                    WHERE pe."ConnectedSystemObjectId" = @connectedSystemObjectId
                      AND pe."ConnectedSystemId" = @connectedSystemId
                      AND pe."Status" = @executing
                      AND pe."LastAttemptedAt" < @exportsInFlightSince),
                EXISTS (
                    SELECT 1
                    FROM "PendingExports" pe
                    WHERE pe."ConnectedSystemObjectId" = @connectedSystemObjectId
                      AND pe."ConnectedSystemId" = @connectedSystemId
                      AND pe."Status" = @executing
                      AND pe."ChangeType" = @create
                      AND (pe."LastAttemptedAt" IS NULL OR pe."LastAttemptedAt" >= @exportsInFlightSince))
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("awaitingConfirmation", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation });
        command.Parameters.Add(new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId });
        command.Parameters.Add(new NpgsqlParameter("connectedSystemObjectId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = connectedSystemObjectId });
        command.Parameters.Add(new NpgsqlParameter("pendingPasswordChangeId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = pendingPasswordChangeId });
        command.Parameters.Add(new NpgsqlParameter("executing", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportStatus.Executing });
        command.Parameters.Add(new NpgsqlParameter("exportsInFlightSince", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = exportsInFlightSince });
        command.Parameters.Add(new NpgsqlParameter("create", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportChangeType.Create });

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new AccountEnablementFacts(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetBoolean(4));
    }

    public async Task<List<InitialPasswordHoldSummary>> GetInitialPasswordHoldSummariesAsync(int connectedSystemId)
    {
        // Raw SQL, asked once per import after reconciliation (#2039), so a held change is judged even when the import
        // did not return its account. Led by the same partial index on the flag as the lookup above, which holds only
        // the changes awaiting an Initial Password: where none exists the probe reads an empty index and returns
        // nothing. Each hit joins to its Pending Export and Connected System Object by primary key. A read-side
        // projection, so exempt from the bulk column list rule.
        const string sql = """
            SELECT avc."Id", pe."Id", pe."ConnectedSystemObjectId", cso."MetaverseObjectId"
            FROM "PendingExportAttributeValueChanges" avc
            JOIN "PendingExports" pe ON pe."Id" = avc."PendingExportId"
            JOIN "ConnectedSystemObjects" cso ON cso."Id" = pe."ConnectedSystemObjectId"
            WHERE avc."AwaitsInitialPassword"
              AND avc."Status" = @awaitingConfirmation
              AND pe."ConnectedSystemId" = @connectedSystemId
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("awaitingConfirmation", NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        });
        command.Parameters.Add(new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId });

        var held = new List<InitialPasswordHoldSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            held.Add(new InitialPasswordHoldSummary
            {
                AttributeValueChangeId = reader.GetGuid(0),
                PendingExportId = reader.GetGuid(1),
                ConnectedSystemObjectId = reader.GetGuid(2),
                MetaverseObjectId = await reader.IsDBNullAsync(3) ? null : reader.GetGuid(3)
            });
        }

        return held;
    }

    /// <summary>
    /// Runs a single-column uuid SELECT on the context's own connection and transaction and returns its rows as a set.
    /// </summary>
    private async Task<HashSet<Guid>> ReadGuidSetAsync(string sql, params NpgsqlParameter[] parameters)
    {
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    #region Private Pending Export Bulk Helpers

    /// <summary>
    /// Inserts Pending Export rows via COPY binary import on the given connection, in the transaction open on it: the
    /// context's own on the single-connection path of <see cref="CreatePendingExportsAsync"/>, or a parallel connection's
    /// own on its parallel path (#2919). The only insert path for this table, at any batch size.
    /// </summary>
    private static async Task BulkInsertPendingExportsOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<PendingExport> exports)
    {
        // Writer order below MUST match PendingExportBulkColumns.PendingExports exactly.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "PendingExports" (
                {BulkSqlHelpers.ToQuotedList(PendingExportBulkColumns.PendingExports)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var pe in exports)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(pe.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(pe.ConnectedSystemId, NpgsqlTypes.NpgsqlDbType.Integer);
            if (pe.ConnectedSystemObjectId.HasValue)
                await writer.WriteAsync(pe.ConnectedSystemObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync((int)pe.ChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)pe.Status, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(pe.ErrorCount, NpgsqlTypes.NpgsqlDbType.Integer);
            if (pe.FailingSince.HasValue)
                await writer.WriteAsync(pe.FailingSince.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (pe.LastAttemptedAt.HasValue)
                await writer.WriteAsync(pe.LastAttemptedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (pe.LastErrorMessage is not null)
                await writer.WriteAsync(pe.LastErrorMessage, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (pe.LastErrorStackTrace is not null)
                await writer.WriteAsync(pe.LastErrorStackTrace, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (pe.SourceMetaverseObjectId.HasValue)
                await writer.WriteAsync(pe.SourceMetaverseObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(pe.HasUnresolvedReferences, NpgsqlTypes.NpgsqlDbType.Boolean);
            await writer.WriteAsync(pe.CreatedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            if (pe.ProvisioningSyncRuleId.HasValue)
                await writer.WriteAsync(pe.ProvisioningSyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (pe.QueuedByRunProfileExecutionItemId.HasValue)
                await writer.WriteAsync(pe.QueuedByRunProfileExecutionItemId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(pe.StagedByRunProfileExecutionItemIds.ToArray(),
                NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid);
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts Pending Export attribute value change rows via COPY binary import on the given connection, in the
    /// transaction open on it (see <see cref="CreatePendingExportsAsync"/>). Each row's parent Pending Export id is passed
    /// beside it, from the export that holds the change, so the change need not carry it.
    /// </summary>
    private static async Task BulkInsertPendingExportAttributeValueChangesOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<(Guid PendingExportId, PendingExportAttributeValueChange Change)> changes)
    {
        // Writer order below MUST match PendingExportBulkColumns.PendingExportAttributeValueChanges exactly.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""
            COPY "PendingExportAttributeValueChanges" (
                {BulkSqlHelpers.ToQuotedList(PendingExportBulkColumns.PendingExportAttributeValueChanges)}
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var (pendingExportId, avc) in changes)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(avc.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(pendingExportId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(avc.AttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
            if (avc.StringValue is not null)
                await writer.WriteAsync(avc.StringValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (avc.DateTimeValue.HasValue)
                await writer.WriteAsync(DateTimeAttributeValue.ToCanonical(avc.DateTimeValue.Value), NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (avc.IntValue.HasValue)
                await writer.WriteAsync(avc.IntValue.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (avc.LongValue.HasValue)
                await writer.WriteAsync(avc.LongValue.Value, NpgsqlTypes.NpgsqlDbType.Bigint);
            else
                await writer.WriteNullAsync();
            if (avc.DecimalValue.HasValue)
                await writer.WriteAsync(avc.DecimalValue.Value, NpgsqlTypes.NpgsqlDbType.Numeric);
            else
                await writer.WriteNullAsync();
            if (avc.ByteValue is not null)
                await writer.WriteAsync(avc.ByteValue, NpgsqlTypes.NpgsqlDbType.Bytea);
            else
                await writer.WriteNullAsync();
            if (avc.GuidValue.HasValue)
                await writer.WriteAsync(avc.GuidValue.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (avc.BoolValue.HasValue)
                await writer.WriteAsync(avc.BoolValue.Value, NpgsqlTypes.NpgsqlDbType.Boolean);
            else
                await writer.WriteNullAsync();
            if (avc.UnresolvedReferenceValue is not null)
                await writer.WriteAsync(avc.UnresolvedReferenceValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync((int)avc.ChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)avc.Status, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(avc.ExportAttemptCount, NpgsqlTypes.NpgsqlDbType.Integer);
            if (avc.LastExportedAt.HasValue)
                await writer.WriteAsync(avc.LastExportedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (avc.LastImportedValue is not null)
                await writer.WriteAsync(avc.LastImportedValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (avc.ResolvedReferenceCsoId.HasValue)
                await writer.WriteAsync(avc.ResolvedReferenceCsoId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (avc.SyncRuleId.HasValue)
                await writer.WriteAsync(avc.SyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (avc.SyncRuleName is not null)
                await writer.WriteAsync(avc.SyncRuleName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(avc.AwaitsInitialPassword, NpgsqlTypes.NpgsqlDbType.Boolean);
            await writer.WriteAsync(avc.AwaitsExchange, NpgsqlTypes.NpgsqlDbType.Boolean);
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Detaches every tracked entity of type T whose key, as <paramref name="keyOf"/> reads it, is one of
    /// <paramref name="keys"/>. Used before a raw SQL write to keep the change tracker from interfering with it (e.g.
    /// ClientSetNull cascading on orphaned children). <paramref name="keyOf"/> returns null for an entity outside the
    /// write's scope, which is how a caller narrows the match beyond the key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes a set rather than a free predicate (#2813), so the match costs one lookup per tracked entry. A predicate
    /// testing <c>list.Contains(entity.Id)</c> was O(tracked x keys): optimistic export apply removing 9,000 member
    /// values from a batch tracking 900,000 took over five seconds in this scan alone.
    /// </para>
    /// <para>
    /// Change detection is suppressed while enumerating: mid-sync, tracked entities routinely
    /// hold navigations to untracked instances that duplicate already-tracked keys (cross-page
    /// reference resolution builds such graphs), and ChangeTracker.Entries&lt;T&gt;() otherwise
    /// runs DetectChanges, attaches those graphs, and throws an identity conflict. Detaching
    /// needs only the entries already tracked, so skipping detection is safe.
    /// </para>
    /// </remarks>
    private void DetachTrackedEntities<T>(IReadOnlySet<Guid> keys, Func<T, Guid?> keyOf) where T : class
    {
        if (keys.Count == 0)
            return;

        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<T>()
                .Where(e => keyOf(e.Entity) is { } key && keys.Contains(key))
                .ToList();

            foreach (var entry in entries)
                entry.State = EntityState.Detached;
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    /// <summary>
    /// Detaches tracked PendingExportAttributeValueChange entities whose PendingExportId shadow FK
    /// is one of the given parent IDs. Accesses the shadow property via the change tracker entry.
    /// Set-based and with change detection suppressed, for the reasons given on
    /// <see cref="DetachTrackedEntities{T}"/>.
    /// </summary>
    private void DetachTrackedChildEntities(IReadOnlySet<Guid> pendingExportIds)
    {
        if (pendingExportIds.Count == 0)
            return;

        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<PendingExportAttributeValueChange>()
                .Where(e => e.Property<Guid?>("PendingExportId").CurrentValue is { } fkValue && pendingExportIds.Contains(fkValue))
                .ToList();

            foreach (var entry in entries)
                entry.State = EntityState.Detached;
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    #endregion

    #region Connected System Object - MVO Deletion Support (issue #993)

    /// <summary>
    /// Gets all CSOs joined to any of the given MVOs across all Connected Systems, in one query,
    /// grouped by MVO ID. LEAN SHAPE: only the external ID and secondary external ID attribute
    /// values (with their Attribute) are loaded; MVO deletion needs the secondary external ID
    /// (e.g. the DN for LDAP) to stamp on delete Pending Exports, and reference recall needs the
    /// external IDs to pre-resolve reference values. Loading the full attribute graph here would
    /// materialise every membership row of any deleted group.
    /// </summary>
    public async Task<Dictionary<Guid, List<ConnectedSystemObject>>> GetConnectedSystemObjectsForMvoDeletionAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds)
    {
        if (metaverseObjectIds.Count == 0)
            return new Dictionary<Guid, List<ConnectedSystemObject>>();

        // Step 1: the CSO rows themselves, no children. The MVO ID is projected from the database
        // row rather than read from the materialised entity afterwards: this is a tracking query
        // on the worker's long-lived context, so identity resolution returns already-tracked
        // instances, and earlier passes of the same page may have disconnected one in memory
        // (MetaverseObjectId = null) ahead of persistence; grouping on the in-memory value would
        // then throw. The ?? Guid.Empty is unreachable (the Where excludes NULL rows) and exists
        // only to keep the projection null-safe.
        var mvoIds = metaverseObjectIds.ToArray();
        var csoRows = await _context.ConnectedSystemObjects
            .Where(cso => cso.MetaverseObjectId.HasValue && mvoIds.Contains(cso.MetaverseObjectId.Value))
            .Select(cso => new { Cso = cso, MvoId = cso.MetaverseObjectId ?? Guid.Empty })
            .ToListAsync();
        if (csoRows.Count == 0)
            return new Dictionary<Guid, List<ConnectedSystemObject>>();

        // Step 2: only the external ID attribute values, matched by the CSO's external ID columns
        // (which the delete PE stamping and reference recall actually read) OR the schema attribute
        // flags (belt and braces should the columns and flags ever diverge). A filtered Include
        // cannot express the column match: EF Core cannot translate a filtered Include that
        // references the parent entity (InvalidOperationException at query translation), so this
        // runs as a correlated subquery and the values are stitched onto the CSOs below.
        var csoIds = csoRows.Select(r => r.Cso.Id).ToList();
        var externalIdValueRows = await _context.ConnectedSystemObjects
            .Where(cso => csoIds.Contains(cso.Id))
            .SelectMany(cso => cso.AttributeValues
                .Where(av => av.AttributeId == cso.ExternalIdAttributeId
                          || av.AttributeId == cso.SecondaryExternalIdAttributeId
                          || av.Attribute.IsExternalId
                          || av.Attribute.IsSecondaryExternalId)
                .Select(av => new { CsoId = cso.Id, Value = av, av.Attribute }))
            .ToListAsync();

        // Stitch in memory. Tracked-query navigation fix-up may already have added a value to its
        // CSO's collection (or the CSO may already be tracked with its values from page processing),
        // so guard against double-adding the same instance.
        var csosById = csoRows.ToDictionary(r => r.Cso.Id, r => r.Cso);
        foreach (var row in externalIdValueRows)
        {
            row.Value.Attribute = row.Attribute;
            var attributeValues = csosById[row.CsoId].AttributeValues;
            if (!attributeValues.Contains(row.Value))
                attributeValues.Add(row.Value);
        }

        return csoRows
            .GroupBy(r => r.MvoId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Cso).ToList());
    }

    /// <summary>
    /// Summary-tier load of the CSOs joined to the given MVOs in the given target systems, for
    /// reference recall staging (#1003). Raw SQL into scalars: nothing is materialised into the
    /// change tracker and no attribute values are loaded (the whole point of the recall fast path
    /// is to never touch a referencing group's membership rows).
    /// </summary>
    public async Task<List<ConnectedSystemObjectRecallTarget>> GetConnectedSystemObjectRecallTargetsAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds,
        IReadOnlyCollection<int> targetConnectedSystemIds)
    {
        if (metaverseObjectIds.Count == 0 || targetConnectedSystemIds.Count == 0)
            return new List<ConnectedSystemObjectRecallTarget>();

        var targets = new List<ConnectedSystemObjectRecallTarget>();
        var connection = _context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            @"SELECT ""Id"", ""MetaverseObjectId"", ""ConnectedSystemId"", ""Status""
              FROM ""ConnectedSystemObjects""
              WHERE ""MetaverseObjectId"" = ANY(@mvoIds) AND ""ConnectedSystemId"" = ANY(@systemIds)";
        var mvoIdsParameter = command.CreateParameter();
        mvoIdsParameter.ParameterName = "mvoIds";
        mvoIdsParameter.Value = metaverseObjectIds.ToArray();
        command.Parameters.Add(mvoIdsParameter);
        var systemIdsParameter = command.CreateParameter();
        systemIdsParameter.ParameterName = "systemIds";
        systemIdsParameter.Value = targetConnectedSystemIds.ToArray();
        command.Parameters.Add(systemIdsParameter);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            targets.Add(new ConnectedSystemObjectRecallTarget
            {
                ConnectedSystemObjectId = reader.GetGuid(0),
                MetaverseObjectId = reader.GetGuid(1),
                ConnectedSystemId = reader.GetInt32(2),
                Status = (ConnectedSystemObjectStatus)reader.GetInt32(3)
            });
        }

        return targets;
    }

    /// <summary>
    /// Gets the Connected System ID of each CSO joined to the given MVO: one row per CSO, duplicates
    /// preserved when a system holds multiple joined CSOs (#119). Raw SQL per the worker hot-path rule;
    /// replaces the previous count-only query at the disconnect call sites, so it costs the same single
    /// round trip. Read-side SELECT: a deliberate projection subset, exempt from the bulk column list rule.
    /// </summary>
    public async Task<List<int>> GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(Guid metaverseObjectId)
    {
        var systemIds = new List<int>();
        var connection = _context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            @"SELECT ""ConnectedSystemId""
              FROM ""ConnectedSystemObjects""
              WHERE ""MetaverseObjectId"" = @mvoId";
        var mvoIdParameter = command.CreateParameter();
        mvoIdParameter.ParameterName = "mvoId";
        mvoIdParameter.Value = metaverseObjectId;
        command.Parameters.Add(mvoIdParameter);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            systemIds.Add(reader.GetInt32(0));

        return systemIds;
    }

    public async Task<Dictionary<Guid, ConnectedSystemObjectDisplaySnapshot>> GetConnectedSystemObjectDisplaySnapshotsAsync(IReadOnlyCollection<Guid> csoIds)
    {
        if (csoIds.Count == 0)
            return new Dictionary<Guid, ConnectedSystemObjectDisplaySnapshot>();

        var idList = csoIds as IList<Guid> ?? csoIds.ToList();

        // A correlated single-column scalar subquery keyed on the CSO's external-ID attribute, rendering
        // the anchor via the shared ExternalIdValueText definition; a single-column projection emits a
        // clean correlated subquery rather than the whole-table ROW_NUMBER() a multi-column projection
        // would produce, and filtering by ExternalIdAttributeId rides the
        // (ConnectedSystemObjectId, AttributeId) index instead of scanning member values.
        var rows = await _context.ConnectedSystemObjects
            .AsNoTracking()
            .Where(cso => idList.Contains(cso.Id))
            .Select(cso => new
            {
                cso.Id,
                ExtIdText = cso.AttributeValues
                    .Where(av => av.AttributeId == cso.ExternalIdAttributeId)
                    .AsQueryable()
                    .Select(ExternalIdValueText.FromAttributeValue)
                    .FirstOrDefault(),
                TypeName = cso.Type!.Name
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.Id, r => new ConnectedSystemObjectDisplaySnapshot
        {
            ConnectedSystemObjectId = r.Id,
            ExternalId = r.ExtIdText,
            TypeName = r.TypeName
        });
    }

    /// <summary>
    /// The reference recall existence query (#1003). A resolved reference is matched by its id alone; an unresolved
    /// one by its raw string ignoring case, the database lowering both sides so it never depends on .NET folding alike
    /// (#2051); a case-exact value's exact match is decided by the caller (ReferenceValueIndex). Each way of matching
    /// is an index lookup on the values sought (see <see cref="CsoReferenceValueMatchSql"/>), so the cost follows the
    /// number of deleted objects and the rows that name them, not the membership of the groups searched (#2669).
    /// Call per target Connected System so identical values cannot cross-match between systems.
    /// </summary>
    public async Task<List<CsoReferenceValueMatch>> GetCsoReferenceValueMatchesAsync(
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        IReadOnlyCollection<int> connectedSystemAttributeIds,
        IReadOnlyCollection<Guid> deletedReferenceCsoIds,
        IReadOnlyCollection<string> referenceValues)
    {
        if (connectedSystemObjectIds.Count == 0 || connectedSystemAttributeIds.Count == 0 ||
            (deletedReferenceCsoIds.Count == 0 && referenceValues.Count == 0))
            return new List<CsoReferenceValueMatch>();

        var matches = new List<CsoReferenceValueMatch>();
        var connection = _context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = CsoReferenceValueMatchSql;
        AddCsoReferenceValueMatchParameters(command, connectedSystemObjectIds, connectedSystemAttributeIds,
            deletedReferenceCsoIds, referenceValues);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            matches.Add(new CsoReferenceValueMatch
            {
                AttributeValueId = reader.GetGuid(0),
                ConnectedSystemObjectId = reader.GetGuid(1),
                AttributeId = reader.GetInt32(2),
                ReferenceValueId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                UnresolvedReferenceValue = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }

        return matches;
    }

    /// <summary>
    /// The statement behind <see cref="GetCsoReferenceValueMatchesAsync"/>, exposed so its plan can be checked
    /// (<c>ReferenceRecallStagingDatabaseTests</c>).
    /// </summary>
    /// <remarks>
    /// Two arms, each led by the predicate an index answers, rather than one OR (#2669). Held in one OR, the string
    /// comparison could be served by no index, so PostgreSQL read every member row of every referencing group and
    /// tested each: about 8 s of a 5,000-leaver deprovisioning at Scale500k25kGroups. The first arm looks the deleted
    /// objects' ids up in the <c>ReferenceValueId</c> index; the second looks the lowered values up in
    /// <c>IX_CsoAttrVal_Lower_UnresolvedRef</c>, whose predicate (<c>UnresolvedReferenceValue IS NOT NULL AND
    /// ReferenceValueId IS NULL</c>) the arm repeats so the partial index applies. The arms are disjoint on
    /// <c>ReferenceValueId</c>, so <c>UNION ALL</c> returns each row at most once. The values are lowered in the
    /// database, as the stored side is, so the narrowing never depends on .NET folding case the same way (#2051).
    /// </remarks>
    internal const string CsoReferenceValueMatchSql =
        @"SELECT ""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""ReferenceValueId"", ""UnresolvedReferenceValue""
          FROM ""ConnectedSystemObjectAttributeValues""
          WHERE ""ReferenceValueId"" = ANY(@deletedCsoIds)
            AND ""ConnectedSystemObjectId"" = ANY(@csoIds)
            AND ""AttributeId"" = ANY(@attributeIds)
          UNION ALL
          SELECT ""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""ReferenceValueId"", ""UnresolvedReferenceValue""
          FROM ""ConnectedSystemObjectAttributeValues""
          WHERE LOWER(""UnresolvedReferenceValue"") = ANY(ARRAY(SELECT lower(v) FROM unnest(@referenceValues) AS v))
            AND ""UnresolvedReferenceValue"" IS NOT NULL
            AND ""ReferenceValueId"" IS NULL
            AND ""ConnectedSystemObjectId"" = ANY(@csoIds)
            AND ""AttributeId"" = ANY(@attributeIds)";

    /// <summary>
    /// How many ids and strings one probe statement sends at most; larger plans are asked in several statements.
    /// </summary>
    private const int ReconciliationProbeChunkTuples = 10_000;

    /// <inheritdoc />
    public async Task<List<ProbedAttributeValues>> GetReconciliationProbeValuesAsync(IReadOnlyCollection<ReconciliationProbeQuestion> questions)
    {
        var answers = new List<ProbedAttributeValues>(questions.Count);
        if (questions.Count == 0)
            return answers;

        // One question per object and attribute: two would split one attribute's candidates between two answers.
        var duplicate = questions.GroupBy(q => (q.ConnectedSystemObjectId, q.AttributeId)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException(
                $"More than one reconciliation probe question for attribute {duplicate.Key.AttributeId} of Connected System Object {duplicate.Key.ConnectedSystemObjectId}.",
                nameof(questions));

        var connection = _context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        foreach (var chunk in ChunkReconciliationProbeQuestions(questions))
        {
            var candidatesByPair = chunk.ToDictionary(q => (q.ConnectedSystemObjectId, q.AttributeId),
                _ => new Dictionary<Guid, ConnectedSystemObjectAttributeValue>());

            var valueQuestions = chunk.Where(q => q.ReferenceIds.Count + q.ExactValues.Count + q.CaseInsensitiveValues.Count > 0).ToList();
            if (valueQuestions.Count > 0)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = ReconciliationProbeValuesSql;
                AddReconciliationProbeValuesParameters(command,
                    valueQuestions.Select(q => q.ConnectedSystemObjectId).Distinct().ToList(),
                    valueQuestions.Select(q => q.AttributeId).Distinct().ToList(),
                    valueQuestions.SelectMany(q => q.ReferenceIds).Distinct().ToList(),
                    valueQuestions.SelectMany(q => q.ExactValues).Distinct(StringComparer.Ordinal).ToList(),
                    valueQuestions.SelectMany(q => q.CaseInsensitiveValues).Distinct(StringComparer.Ordinal).ToList());

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    // The arms match ids and strings across every object and attribute of the chunk, so a row can belong
                    // to a pair nobody asked about (dropped) or be returned by two arms (kept once).
                    var row = new ConnectedSystemObjectAttributeValue
                    {
                        Id = reader.GetGuid(0),
                        AttributeId = reader.GetInt32(2),
                        ReferenceValueId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                        UnresolvedReferenceValue = reader.IsDBNull(4) ? null : reader.GetString(4)
                    };
                    if (candidatesByPair.TryGetValue((reader.GetGuid(1), row.AttributeId), out var candidates))
                        candidates.TryAdd(row.Id, row);
                }
            }

            var countQuestions = chunk.Where(q => q.NeedsCount).ToList();
            var countsByPair = new Dictionary<(Guid, int), int>();
            if (countQuestions.Count > 0)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = ReconciliationProbeCountSql;
                AddReconciliationProbeCountParameters(command,
                    countQuestions.Select(q => q.ConnectedSystemObjectId).ToList(),
                    countQuestions.Select(q => q.AttributeId).ToList());

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    countsByPair[(reader.GetGuid(0), reader.GetInt32(1))] = reader.GetInt32(2);
            }

            foreach (var question in chunk)
            {
                var pair = (question.ConnectedSystemObjectId, question.AttributeId);
                int? cappedCount = null;
                if (question.NeedsCount)
                {
                    if (!countsByPair.TryGetValue(pair, out var count))
                        throw new InvalidOperationException(
                            $"The reconciliation probe returned no count for attribute {question.AttributeId} of Connected System Object {question.ConnectedSystemObjectId}.");
                    cappedCount = count;
                }

                answers.Add(new ProbedAttributeValues
                {
                    ConnectedSystemObjectId = question.ConnectedSystemObjectId,
                    AttributeId = question.AttributeId,
                    ProbedChangeIds = question.ChangeIds,
                    CandidateValues = candidatesByPair[pair].Values.ToList(),
                    CappedValueCount = cappedCount
                });
            }
        }

        return answers;
    }

    private static IEnumerable<List<ReconciliationProbeQuestion>> ChunkReconciliationProbeQuestions(IEnumerable<ReconciliationProbeQuestion> questions)
    {
        var chunk = new List<ReconciliationProbeQuestion>();
        var tuples = 0;
        foreach (var question in questions)
        {
            if (chunk.Count > 0 && tuples + question.TupleCount > ReconciliationProbeChunkTuples)
            {
                yield return chunk;
                chunk = [];
                tuples = 0;
            }

            chunk.Add(question);
            tuples += question.TupleCount;
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Raw Npgsql, once per page of a Delta Import (#2817). The database reads every member of each group asked about,
    /// which is the comparison's own cost and cannot be avoided while a Connector reports a group's whole member list; what
    /// it returns is only what is left undecided, so a group of 450,000 members that lost a few costs those few rows to
    /// read and nothing to hold. See <see cref="HeldReferenceValueAnswersSql"/> for the statement.
    /// </remarks>
    public async Task<List<HeldReferenceValueAnswer>> GetHeldReferenceValueAnswersAsync(IReadOnlyList<HeldReferenceValueQuestion> questions)
    {
        var answers = questions.Select(question => new HeldReferenceValueAnswer(question)).ToList();
        if (questions.Count == 0)
            return answers;

        // One question per object and attribute: two would answer one attribute twice, each about only part of it.
        var duplicate = questions.GroupBy(q => (q.ConnectedSystemObjectId, q.AttributeId)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException(
                $"More than one held reference value question for attribute {duplicate.Key.AttributeId} of Connected System Object {duplicate.Key.ConnectedSystemObjectId}.",
                nameof(questions));

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction()
        };
        PrepareHeldReferenceValueAnswersCommand(command, questions);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // Column order is HeldReferenceValueAnswersSql's: kind, question (1-based), value id, unresolved reference,
            // referenced object, its External ID, imported value.
            var answer = answers[reader.GetInt32(1) - 1];
            var kind = reader.GetInt32(0);
            if (kind == HeldReferenceValueAnswerUnmatchedIncoming)
            {
                answer.UnmatchedIncomingValues.Add(reader.GetString(6));
                continue;
            }

            var valueId = reader.GetGuid(2);
            var unresolvedReferenceValue = reader.IsDBNull(3) ? null : reader.GetString(3);
            var referenceValueId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            if (referenceValueId.HasValue && !reader.IsDBNull(5))
                answer.ReferencedExternalIds.TryAdd(referenceValueId.Value, reader.GetString(5));

            if (kind == HeldReferenceValueAnswerUnmatchedHeld)
                answer.UnmatchedValueIds.Add(valueId);
            else if (answer.RelatedValues.TrueForAll(related => related.Id != valueId))
                answer.RelatedValues.Add(new HeldReferenceValue(valueId, unresolvedReferenceValue, referenceValueId));
        }

        return answers;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Raw Npgsql, once per page of a Delta Import (#2836), before <see cref="GetHeldReferenceValueAnswersAsync"/>. The
    /// database counts each attribute's values through the (object, attribute) index and returns the strings of only those
    /// holding as many values as the question carries; the Worker compares them with the question's values, removing each
    /// held string from a set of them and stopping at the first that is not there (not imported, or held twice). An
    /// attribute is held as imported when nothing failed and the set is empty: the held strings and the imported values are
    /// then the same set, each string held once.
    /// </para>
    /// <para>
    /// That is exactly when the answer would be empty. Every held value is matched exactly by the string it keeps, so none
    /// is undecided, and every imported value is one of those strings, so none is undecided either and none has related
    /// values. The comparison is ordinal on the strings as read, which is byte equality on the stored text: the database is
    /// UTF-8, and its text equality, which the answer uses, is byte equality under its deterministic collation.
    /// </para>
    /// <para>
    /// Nothing imported is sent, which is what makes it cheaper than asking: a group of 200,000 members held as imported
    /// costs a count and a read of its member strings (measured at about 170 ms), where asking cost about 450 ms in the
    /// database alone. A group whose count differs costs only the count. A question carrying a value twice, or none, is never
    /// named: its count cannot match, and it is left to be asked.
    /// </para>
    /// </remarks>
    public async Task<HashSet<(Guid ConnectedSystemObjectId, int AttributeId)>> GetReferenceAttributesHeldAsImportedAsync(IReadOnlyList<HeldReferenceValueQuestion> questions)
    {
        var heldAsImported = new HashSet<(Guid ConnectedSystemObjectId, int AttributeId)>();
        var asked = questions.Where(q => q.Values.Count > 0).ToList();
        if (asked.Count == 0)
            return heldAsImported;

        var duplicate = asked.GroupBy(q => (q.ConnectedSystemObjectId, q.AttributeId)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException(
                $"More than one held reference value question for attribute {duplicate.Key.AttributeId} of Connected System Object {duplicate.Key.ConnectedSystemObjectId}.",
                nameof(questions));

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            Transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction()
        };
        PrepareReferenceAttributesHeldAsImportedCommand(command, asked);

        // Per question (1-based, as the statement numbers them): the imported values not yet matched by a held string,
        // built when its first row arrives; and whether a held string failed to match.
        var remaining = new HashSet<string>?[asked.Count];
        var failed = new bool[asked.Count];
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var index = reader.GetInt32(0) - 1;
                if (failed[index])
                    continue;

                var unmatched = remaining[index] ??= asked[index].Values.ToHashSet(StringComparer.Ordinal);
                failed[index] = reader.IsDBNull(1) || !unmatched.Remove(reader.GetString(1));
            }
        }

        for (var index = 0; index < asked.Count; index++)
        {
            if (remaining[index] is { Count: 0 } && !failed[index])
                heldAsImported.Add((asked[index].ConnectedSystemObjectId, asked[index].AttributeId));
        }

        return heldAsImported;
    }

    /// <summary>
    /// The statement behind <see cref="GetReferenceAttributesHeldAsImportedAsync"/>: the question number and string of
    /// every value of each attribute holding exactly as many values as its question carries, and nothing for any other.
    /// </summary>
    internal const string ReferenceAttributesHeldAsImportedSql = """
        WITH question AS MATERIALIZED (
            SELECT q.cso_id, q.attribute_id, q.value_count, q.ordinal::integer AS question
            FROM unnest(@questionCsoIds, @questionAttributeIds, @questionValueCounts) WITH ORDINALITY AS q(cso_id, attribute_id, value_count, ordinal)
        ),
        candidate AS MATERIALIZED (
            SELECT q.question, q.cso_id, q.attribute_id
            FROM question q
            WHERE (SELECT count(*) FROM "ConnectedSystemObjectAttributeValues" counted
                   WHERE counted."ConnectedSystemObjectId" = q.cso_id AND counted."AttributeId" = q.attribute_id) = q.value_count
        )
        SELECT c.question, av."UnresolvedReferenceValue"
        FROM candidate c
        JOIN "ConnectedSystemObjectAttributeValues" av
          ON av."ConnectedSystemObjectId" = c.cso_id AND av."AttributeId" = c.attribute_id
        """;

    /// <summary>
    /// Sets <see cref="ReferenceAttributesHeldAsImportedSql"/> and its parameters on <paramref name="command"/>: the
    /// questions as parallel arrays (object, attribute, how many values each carries). Exposed so the plan can be checked.
    /// </summary>
    internal static void PrepareReferenceAttributesHeldAsImportedCommand(NpgsqlCommand command, IReadOnlyList<HeldReferenceValueQuestion> questions)
    {
        command.CommandText = ReferenceAttributesHeldAsImportedSql;
        command.Parameters.Add(new NpgsqlParameter("questionCsoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = questions.Select(q => q.ConnectedSystemObjectId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("questionAttributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = questions.Select(q => q.AttributeId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("questionValueCounts", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint)
            { Value = questions.Select(q => (long)q.Values.Count).ToArray() });
    }

    /// <summary>A held value no imported value matches exactly.</summary>
    private const int HeldReferenceValueAnswerUnmatchedHeld = 0;

    /// <summary>A held value whose object's External ID equals an unmatched imported value through <c>lower()</c>.</summary>
    private const int HeldReferenceValueAnswerRelatedHeld = 1;

    /// <summary>An imported value no held value matches exactly.</summary>
    private const int HeldReferenceValueAnswerUnmatchedIncoming = 2;

    /// <summary>
    /// The External ID of the object a held value was resolved to, read as <c>GetReferenceExternalIdsForCsosAsync</c> reads
    /// it: its Secondary External ID's text, else its External ID's. <c>{0}</c> is the referenced object's id.
    /// </summary>
    private const string HeldReferenceExternalIdSql = """
        (SELECT COALESCE(
             (SELECT sec."StringValue" FROM "ConnectedSystemObjectAttributeValues" sec
              WHERE sec."ConnectedSystemObjectId" = ref."Id" AND sec."AttributeId" = ref."SecondaryExternalIdAttributeId" LIMIT 1),
             (SELECT pri."StringValue" FROM "ConnectedSystemObjectAttributeValues" pri
              WHERE pri."ConnectedSystemObjectId" = ref."Id" AND pri."AttributeId" = ref."ExternalIdAttributeId" LIMIT 1))
         FROM "ConnectedSystemObjects" ref WHERE ref."Id" = {0})
        """;

    /// <summary>
    /// The statement behind <see cref="GetHeldReferenceValueAnswersAsync"/>: one row per thing left undecided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A held value is matched exactly when the string it was imported as (which resolution keeps), or the External ID of
    /// the object it was resolved to, is byte for byte one of the imported values. The string is compared first, over the
    /// whole group; only the values it leaves (the members that left, and any renamed since they were imported) have their
    /// object's External ID read, so a group whose members are unchanged costs no join to its members' objects. An
    /// imported value is matched exactly the same two ways.
    /// </para>
    /// <para>
    /// Every comparison is set-based whatever the planner estimates, because an estimate is easily wrong here (a
    /// materialised step is costed at a handful of rows, and a directory that moved every user to another OU leaves every
    /// member's kept DN unmatched): a nested loop over 450,000 members against 450,000 imported values would never finish.
    /// A held value is tested against <c>@keys</c>, each imported value prefixed with its question's number, with
    /// <c>= ANY</c>, which PostgreSQL answers from a hash of the array when the array is a constant: it is, because JIM
    /// sends this statement unprepared, so it is planned with its parameters' values
    /// (<c>HeldReferenceValueAnswerDatabaseTests</c> checks the plan shows the array, not a parameter). Each test sits in a
    /// <c>CASE</c>, which the planner does not estimate the selectivity of, so it does not cost the array element by
    /// element (measured at 200,000 values: about 95 ms of planning for one test left bare; 13 to 19 ms for the whole
    /// statement with both wrapped).
    /// The imported values left unmatched are an <c>EXCEPT</c>, which is hashed or sorted, never looped.
    /// </para>
    /// <para>
    /// Every imported value left unmatched is looked up through the <c>lower("StringValue")</c> index for the objects whose
    /// External ID it equals ignoring case, and the group's values naming those objects are returned as related: the
    /// comparison matches a member to an imported value ignoring case through its object, so it needs them to tell a DN
    /// whose case changed from a new member. The database's folding finds them; whether that folding can be trusted is the
    /// caller's to decide (<see cref="HeldReferenceValueAnswer.IsCompleteIgnoringCase"/>).
    /// </para>
    /// </remarks>
    internal static readonly string HeldReferenceValueAnswersSql = $"""
        WITH question AS MATERIALIZED (
            SELECT q.cso_id, q.attribute_id, q.ordinal::integer AS question
            FROM unnest(@questionCsoIds, @questionAttributeIds) WITH ORDINALITY AS q(cso_id, attribute_id, ordinal)
        ),
        held_unmatched AS MATERIALIZED (
            SELECT q.question, av."Id", av."UnresolvedReferenceValue", av."ReferenceValueId"
            FROM question q
            JOIN "ConnectedSystemObjectAttributeValues" av
              ON av."ConnectedSystemObjectId" = q.cso_id AND av."AttributeId" = q.attribute_id
            WHERE CASE WHEN av."UnresolvedReferenceValue" IS NULL THEN true
                       ELSE NOT ((lpad(q.question::text, {HeldReferenceValueKeyQuestionWidth}, '0') || av."UnresolvedReferenceValue") = ANY(@keys)) END
        ),
        held_unmatched_named AS MATERIALIZED (
            SELECT h.question, h."Id", h."UnresolvedReferenceValue", h."ReferenceValueId",
                   {string.Format(System.Globalization.CultureInfo.InvariantCulture, HeldReferenceExternalIdSql, "h.\"ReferenceValueId\"")} AS external_id
            FROM held_unmatched h
        ),
        incoming_unmatched AS MATERIALIZED (
            SELECT left(k, {HeldReferenceValueKeyQuestionWidth})::integer AS question, substr(k, {HeldReferenceValueKeyQuestionWidth + 1}) AS value
            FROM unnest(@keys) AS k
            EXCEPT
            SELECT q.question, av."UnresolvedReferenceValue"
            FROM question q
            JOIN "ConnectedSystemObjectAttributeValues" av
              ON av."ConnectedSystemObjectId" = q.cso_id AND av."AttributeId" = q.attribute_id
            WHERE av."UnresolvedReferenceValue" IS NOT NULL
            EXCEPT
            SELECT n.question, n.external_id FROM held_unmatched_named n WHERE n.external_id IS NOT NULL
        )
        SELECT {HeldReferenceValueAnswerUnmatchedHeld} AS kind, n.question, n."Id", n."UnresolvedReferenceValue", n."ReferenceValueId", n.external_id, NULL::text AS value
        FROM held_unmatched_named n
        WHERE CASE WHEN n.external_id IS NULL THEN true
                   ELSE NOT ((lpad(n.question::text, {HeldReferenceValueKeyQuestionWidth}, '0') || n.external_id) = ANY(@keys)) END
        UNION ALL
        SELECT {HeldReferenceValueAnswerRelatedHeld}, u.question, av."Id", av."UnresolvedReferenceValue", av."ReferenceValueId",
               {string.Format(System.Globalization.CultureInfo.InvariantCulture, HeldReferenceExternalIdSql, "av.\"ReferenceValueId\"")}, NULL::text
        FROM incoming_unmatched u
        JOIN question q ON q.question = u.question
        CROSS JOIN LATERAL (
            SELECT DISTINCT named."ConnectedSystemObjectId"
            FROM "ConnectedSystemObjectAttributeValues" named
            WHERE lower(named."StringValue") = lower(u.value) AND named."StringValue" IS NOT NULL
            OFFSET 0) AS named
        JOIN "ConnectedSystemObjectAttributeValues" av
          ON av."ReferenceValueId" = named."ConnectedSystemObjectId"
         AND av."ConnectedSystemObjectId" = q.cso_id AND av."AttributeId" = q.attribute_id
        UNION ALL
        SELECT {HeldReferenceValueAnswerUnmatchedIncoming}, u.question, NULL::uuid, NULL::text, NULL::uuid, NULL::text, u.value
        FROM incoming_unmatched u
        """;

    /// <summary>
    /// The width of the question number each imported value is prefixed with in <c>@keys</c>, zero-padded so the value
    /// starts at a fixed place and nothing in it can be mistaken for part of the number.
    /// </summary>
    private const int HeldReferenceValueKeyQuestionWidth = 10;

    /// <summary>
    /// Sets <see cref="HeldReferenceValueAnswersSql"/> and its parameters on <paramref name="command"/>: the questions as
    /// parallel arrays (object, attribute), and every imported value prefixed with the 1-based number of its question.
    /// Exposed so the plan can be checked (<c>HeldReferenceValueAnswerDatabaseTests</c>).
    /// </summary>
    internal static void PrepareHeldReferenceValueAnswersCommand(NpgsqlCommand command, IReadOnlyList<HeldReferenceValueQuestion> questions)
    {
        var keys = new string[questions.Sum(q => q.Values.Count)];
        var next = 0;
        for (var q = 0; q < questions.Count; q++)
        {
            var prefix = (q + 1).ToString(new string('0', HeldReferenceValueKeyQuestionWidth), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var value in questions[q].Values)
                keys[next++] = prefix + value;
        }

        command.CommandText = HeldReferenceValueAnswersSql;
        command.Parameters.Add(new NpgsqlParameter("questionCsoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = questions.Select(q => q.ConnectedSystemObjectId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("questionAttributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = questions.Select(q => q.AttributeId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = keys });
    }

    /// <inheritdoc />
    public async Task<bool> DoesDatabaseFoldCaseLikeIdentifierKeyAsync()
    {
        var sample = IdentifierKey.FoldingCheckSample;

        var connection = _context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT lower(@sample) = @folded";
        command.Parameters.Add(new NpgsqlParameter("sample", NpgsqlTypes.NpgsqlDbType.Text) { Value = sample });
        command.Parameters.Add(new NpgsqlParameter("folded", NpgsqlTypes.NpgsqlDbType.Text) { Value = IdentifierKey.For(sample, caseExact: false) });
        return await command.ExecuteScalarAsync() is true;
    }

    /// <summary>
    /// The candidate rows statement behind <see cref="GetReconciliationProbeValuesAsync"/>, exposed so its plan can be
    /// checked (<c>ReconciliationCsoLoadDatabaseTests</c>).
    /// </summary>
    /// <remarks>
    /// Three arms, each led by the predicate an index answers, the shape <see cref="CsoReferenceValueMatchSql"/> takes
    /// for the same reason (#2669): held in one OR, no index could serve the string comparisons, and PostgreSQL would read
    /// every member of every group asked about. The referenced ids are looked up in the <c>ReferenceValueId</c> index.
    /// Both string arms look strings up in <c>IX_CsoAttrVal_Lower_UnresolvedRef</c>, by their lowered form, repeating its
    /// predicate so the partial index applies, and so find unresolved rows only. That is all a change naming an object can
    /// match by string, since a resolved row names its object by id alone (<c>SyncEngine.ReferenceRowMatches</c>); the
    /// planner sends a change naming no object, which matches a resolved row by the string it keeps, to the full load
    /// instead (#2871), so no index holds a resolved row's string. The exact arm then compares the string exactly; a
    /// string equal to another is equal lowered too, whatever the database's folding, so the lowered lookup misses
    /// nothing it should find. The case-insensitive arm asks only for ASCII strings, and only once
    /// <see cref="DoesDatabaseFoldCaseLikeIdentifierKeyAsync"/> has confirmed the database folds the non-ASCII characters
    /// JIM folds into ASCII as JIM does. The arms may overlap and match across the objects and attributes of one
    /// statement, so the caller keeps each row once, under the question for its own object and attribute.
    /// </remarks>
    internal const string ReconciliationProbeValuesSql =
        @"SELECT ""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""ReferenceValueId"", ""UnresolvedReferenceValue""
          FROM ""ConnectedSystemObjectAttributeValues""
          WHERE ""ReferenceValueId"" = ANY(@referenceIds)
            AND ""ConnectedSystemObjectId"" = ANY(@csoIds)
            AND ""AttributeId"" = ANY(@attributeIds)
          UNION ALL
          SELECT ""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""ReferenceValueId"", ""UnresolvedReferenceValue""
          FROM ""ConnectedSystemObjectAttributeValues""
          WHERE LOWER(""UnresolvedReferenceValue"") = ANY(ARRAY(SELECT lower(v) FROM unnest(@exactValues) AS v))
            AND ""UnresolvedReferenceValue"" = ANY(@exactValues)
            AND ""UnresolvedReferenceValue"" IS NOT NULL
            AND ""ReferenceValueId"" IS NULL
            AND ""ConnectedSystemObjectId"" = ANY(@csoIds)
            AND ""AttributeId"" = ANY(@attributeIds)
          UNION ALL
          SELECT ""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""ReferenceValueId"", ""UnresolvedReferenceValue""
          FROM ""ConnectedSystemObjectAttributeValues""
          WHERE LOWER(""UnresolvedReferenceValue"") = ANY(ARRAY(SELECT lower(v) FROM unnest(@caseInsensitiveValues) AS v))
            AND ""UnresolvedReferenceValue"" IS NOT NULL
            AND ""ReferenceValueId"" IS NULL
            AND ""ConnectedSystemObjectId"" = ANY(@csoIds)
            AND ""AttributeId"" = ANY(@attributeIds)";

    /// <summary>
    /// The capped count statement behind <see cref="GetReconciliationProbeValuesAsync"/>, exposed so its plan can be
    /// checked: per object and attribute, how many values it holds, stopping at two. Each pair reads at most two rows
    /// through the (object, attribute) index, however many members a group has.
    /// </summary>
    internal const string ReconciliationProbeCountSql =
        @"SELECT p.cso_id, p.attribute_id, c.value_count
          FROM unnest(@countCsoIds, @countAttributeIds) AS p(cso_id, attribute_id)
          CROSS JOIN LATERAL (
              SELECT count(*)::integer AS value_count
              FROM (
                  SELECT 1
                  FROM ""ConnectedSystemObjectAttributeValues"" AS av
                  WHERE av.""ConnectedSystemObjectId"" = p.cso_id
                    AND av.""AttributeId"" = p.attribute_id
                  LIMIT 2
              ) AS capped
          ) AS c";

    /// <summary>
    /// Binds the parameters of <see cref="ReconciliationProbeValuesSql"/>.
    /// </summary>
    internal static void AddReconciliationProbeValuesParameters(System.Data.Common.DbCommand command, IReadOnlyCollection<Guid> csoIds,
        IReadOnlyCollection<int> attributeIds, IReadOnlyCollection<Guid> referenceIds, IReadOnlyCollection<string> exactValues,
        IReadOnlyCollection<string> caseInsensitiveValues)
    {
        command.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = csoIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("attributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = attributeIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("referenceIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = referenceIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("exactValues", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            { Value = exactValues.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("caseInsensitiveValues", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            { Value = caseInsensitiveValues.ToArray() });
    }

    /// <summary>
    /// Binds the parameters of <see cref="ReconciliationProbeCountSql"/>: one object and attribute per position.
    /// </summary>
    internal static void AddReconciliationProbeCountParameters(System.Data.Common.DbCommand command, IReadOnlyList<Guid> csoIds,
        IReadOnlyList<int> attributeIds)
    {
        command.Parameters.Add(new NpgsqlParameter("countCsoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = csoIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("countAttributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = attributeIds.ToArray() });
    }

    /// <summary>
    /// Binds the parameters of <see cref="CsoReferenceValueMatchSql"/>.
    /// </summary>
    internal static void AddCsoReferenceValueMatchParameters(
        System.Data.Common.DbCommand command,
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        IReadOnlyCollection<int> connectedSystemAttributeIds,
        IReadOnlyCollection<Guid> deletedReferenceCsoIds,
        IReadOnlyCollection<string> referenceValues)
    {
        command.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = connectedSystemObjectIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("attributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
            { Value = connectedSystemAttributeIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("deletedCsoIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = deletedReferenceCsoIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("referenceValues", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            { Value = referenceValues.ToArray() });
    }

    /// <summary>
    /// Disconnects the given CSOs from their MVOs in one set-based statement: nulls
    /// <c>MetaverseObjectId</c>, <c>DateJoined</c> and the join record (#348) and resets <c>JoinType</c> to
    /// <c>NotJoined</c>. Tracked instances are fixed up to match the database state so a later
    /// SaveChangesAsync does not write stale join state back (same pattern as the CSO detach in
    /// the MVO delete path).
    /// </summary>
    public async Task DisconnectConnectedSystemObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return;

        var csoIds = connectedSystemObjectIds.ToArray();
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects""
              SET ""MetaverseObjectId"" = NULL, ""JoinType"" = {1}, ""DateJoined"" = NULL,
                  ""JoinMethod"" = NULL, ""JoinSyncRuleId"" = NULL, ""JoinSyncRuleName"" = NULL,
                  ""JoinObjectMatchingRuleId"" = NULL, ""JoinMatchValue"" = NULL
              WHERE ""Id"" = ANY({0})",
            csoIds, (int)ConnectedSystemObjectJoinType.NotJoined);

        var csoIdSet = csoIds.ToHashSet();
        foreach (var trackedCso in _context.ChangeTracker.Entries<ConnectedSystemObject>()
            .Where(e => csoIdSet.Contains(e.Entity.Id)))
        {
            trackedCso.Entity.MetaverseObjectId = null;
            trackedCso.Entity.MetaverseObject = null;
            trackedCso.Entity.ClearJoinRecord();
        }
    }

    #endregion
}
