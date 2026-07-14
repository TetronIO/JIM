// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Metaverse Object — Parallel Bulk Create

    /// <summary>
    /// Bulk creates MVOs with their attribute values using parallel multi-connection writes.
    /// Each partition of MVOs is written on its own <see cref="NpgsqlConnection"/>, allowing
    /// PostgreSQL to utilise multiple CPU cores during the INSERT phase.
    /// <para>
    /// Mirrors the proven pattern from <see cref="CreateConnectedSystemObjectsAsync"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Unlike CSO creates (which happen during import), MVO creates happen during sync where
    /// downstream code (change tracking, export evaluation) still relies on EF's change tracker
    /// to persist MVO-related entities (e.g., MetaverseObjectChange via mvo.Changes collection).
    /// After raw SQL persistence, MVOs are attached to the change tracker as Unchanged so that
    /// EF can discover child entities added to their navigation collections. This bridge will
    /// be removed when MVO change tracking is also converted to raw SQL.
    /// </remarks>
    public async Task CreateMetaverseObjectsBulkAsync(List<MetaverseObject> metaverseObjects)
    {
        if (metaverseObjects.Count == 0)
            return;

        // Pre-generate IDs for all MVOs and their attribute values (bypasses EF ValueGeneratedOnAdd).
        // This ensures mvo.Id is set BEFORE persistence, which the caller relies on for CSO FK fixup
        // (SyncTaskProcessorBase sets cso.MetaverseObjectId = cso.MetaverseObject.Id after this call).
        foreach (var mvo in metaverseObjects)
        {
            if (mvo.Id == Guid.Empty)
                mvo.Id = Guid.NewGuid();

            foreach (var av in mvo.AttributeValues)
            {
                if (av.Id == Guid.Empty)
                    av.Id = Guid.NewGuid();
            }
        }

        // Fixup ReferenceValueId FKs from navigation properties.
        // MVO attribute values can reference other MVOs (e.g., StaticMembers → Person MVO).
        // The referenced MVO may be in this batch (same-page) or already persisted from a
        // previous page — either way the navigation has a valid Id we can use as the FK.
        foreach (var mvo in metaverseObjects)
        {
            foreach (var av in mvo.AttributeValues)
            {
                if (av.ReferenceValue != null && av.ReferenceValue.Id != Guid.Empty && !av.ReferenceValueId.HasValue)
                    av.ReferenceValueId = av.ReferenceValue.Id;
            }
        }

        var parallelism = ParallelBatchWriter.GetWriteParallelism();
        var connectionString = _connectionStringForParallelWrites;

        // For small batches (under parallelism threshold), use the main EF connection directly.
        // The parallel overhead (opening N connections, partitioning) isn't worthwhile for small writes.
        if (metaverseObjects.Count < parallelism * 50 || connectionString == null)
        {
            await CreateMvosOnSingleConnectionAsync(metaverseObjects);
        }
        else
        {
            Log.Information("CreateMetaverseObjectsBulkAsync: Writing {Count} MVOs across {Parallelism} parallel connections",
                metaverseObjects.Count, parallelism);

            // Two-phase write: MVO rows first, then attribute value rows.
            //
            // MVO attribute values can contain ReferenceValueId FKs pointing to other MVOs
            // in the same batch (e.g., a group MVO referencing a user MVO via a member attribute).
            // When partitioned across parallel connections, a group in partition A may reference
            // a user in partition B. If both are inserted in a single transaction per partition,
            // partition A's FK check fails because partition B hasn't committed yet.
            //
            // Phase 1 commits all MVO rows across all partitions, guaranteeing every MVO exists
            // in the database. Phase 2 then inserts attribute values — all ReferenceValueId FKs
            // are satisfied regardless of which partition the referenced MVO was in.

            // Phase 1: Insert all MVO rows across parallel connections.
            await ParallelBatchWriter.ExecuteAsync(
                metaverseObjects,
                parallelism,
                connectionString,
                async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    await BulkInsertMvosOnConnectionAsync(connection, transaction, partition);
                    await transaction.CommitAsync();
                });

            // Phase 2: Insert all attribute value rows across parallel connections.
            // Re-partition by attribute values (not by MVO) for balanced distribution,
            // especially when one MVO has disproportionately many values (e.g., large groups).
            var allAttributeValues = metaverseObjects
                .SelectMany(mvo => mvo.AttributeValues.Select(av => (MvoId: mvo.Id, Value: av)))
                .ToList();

            if (allAttributeValues.Count > 0)
            {
                await ParallelBatchWriter.ExecuteAsync(
                    allAttributeValues,
                    parallelism,
                    connectionString,
                    async (connection, partition) =>
                    {
                        await using var transaction = await connection!.BeginTransactionAsync();
                        await BulkInsertMvoAttributeValuesOnConnectionAsync(connection, transaction, partition.ToList());
                        await transaction.CommitAsync();
                    });
            }
        }

        // Bridge: attach persisted MVOs to the EF change tracker as Unchanged.
        //
        // Downstream sync code (CreatePendingMvoChangeObjectsAsync, EvaluatePendingExportsAsync)
        // still relies on EF to persist child entities added to MVO navigation collections
        // (e.g., mvo.Changes.Add(change)). Without tracking, these child entities are orphaned
        // and SaveChangesAsync either misses them or hits concurrency errors when it discovers
        // untracked MVOs through navigation property traversal.
        //
        // We set the shadow FK "TypeId" explicitly since MetaverseObject has no TypeId property
        // (it's inferred by EF from the Type navigation). The xmin concurrency token is not set:
        // PostgreSQL assigned a real xmin during COPY, but we do not know its value, so the tracked
        // OriginalValue stays 0. This used to be a latent hazard: if a just-created MVO were then
        // updated via EF SaveChangesAsync in the same page flush, the update issued "... WHERE xmin = 0",
        // matched no rows, and threw an unhandled DbUpdateConcurrencyException that aborted the run (the
        // pre-release Full Regression Scenario14-AttributePriority failure). That hazard is now closed:
        // the MVO update path is raw SQL too (see UpdateMetaverseObjectsBulkAsync), keyed by Id with no
        // xmin predicate, and it detaches the graph afterwards so no later EF SaveChangesAsync re-runs the
        // xmin-guarded update. The bogus tracked xmin is therefore harmless: nothing on the sync write path
        // reads it. EF-tracked writes elsewhere (UI / API edits) still enforce xmin against their own reads.
        foreach (var mvo in metaverseObjects)
        {
            var entry = _context.Entry(mvo);
            if (entry.State == EntityState.Detached)
            {
                entry.State = EntityState.Unchanged;
                // Set shadow FK for the Type relationship
                entry.Property("TypeId").CurrentValue = mvo.Type.Id;
            }

            foreach (var av in mvo.AttributeValues)
            {
                var avEntry = _context.Entry(av);
                if (avEntry.State == EntityState.Detached)
                {
                    avEntry.State = EntityState.Unchanged;
                    // Set shadow FK for the MetaverseObject relationship
                    avEntry.Property("MetaverseObjectId").CurrentValue = mvo.Id;
                }
            }
        }
    }

    /// <summary>
    /// Falls back to the shared EF-based implementation for small batches.
    /// </summary>
    private async Task CreateMvosOnSingleConnectionAsync(List<MetaverseObject> metaverseObjects)
    {
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        await using var transaction = await _context.Database.BeginTransactionAsync();

        await BulkInsertMvosViaEfAsync(metaverseObjects);

        var allAttributeValues = metaverseObjects
            .SelectMany(mvo => mvo.AttributeValues.Select(av => (MvoId: mvo.Id, Value: av)))
            .ToList();

        if (allAttributeValues.Count > 0)
            await BulkInsertMvoAttributeValuesViaEfAsync(allAttributeValues);

        await transaction.CommitAsync();
        _context.Database.SetCommandTimeout(previousTimeout);
    }

    /// <summary>
    /// Inserts MVO rows on an independent NpgsqlConnection using COPY binary import.
    /// COPY binary streams data directly without SQL parsing or parameter limits,
    /// providing significantly higher throughput than parameterised INSERT.
    /// </summary>
    /// <remarks>
    /// The <c>xmin</c> concurrency token is excluded — PostgreSQL assigns it automatically on INSERT.
    /// <c>TypeId</c> is a shadow FK (no explicit property on MetaverseObject) — read from <c>mvo.Type.Id</c>.
    /// </remarks>
    private static async Task BulkInsertMvosOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<MetaverseObject> objects)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "MetaverseObjects" ({MvoBulkInsertColumns.ToQuotedList(MvoBulkInsertColumns.MetaverseObjects)}) FROM STDIN (FORMAT binary)""");

        foreach (var mvo in objects)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(mvo.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(mvo.Created, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            if (mvo.LastUpdated.HasValue)
                await writer.WriteAsync(mvo.LastUpdated.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(mvo.Type.Id, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)mvo.Status, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)mvo.Origin, NpgsqlTypes.NpgsqlDbType.Integer);
            if (mvo.LastConnectorDisconnectedDate.HasValue)
                await writer.WriteAsync(mvo.LastConnectorDisconnectedDate.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync((int)mvo.DeletionInitiatedByType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (mvo.DeletionInitiatedById.HasValue)
                await writer.WriteAsync(mvo.DeletionInitiatedById.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (mvo.DeletionInitiatedByName is not null)
                await writer.WriteAsync(mvo.DeletionInitiatedByName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (mvo.CachedDisplayName is not null)
                await writer.WriteAsync(mvo.CachedDisplayName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(mvo.ScopeReviewPending, NpgsqlTypes.NpgsqlDbType.Boolean);
            if (mvo.LastScopeEvaluatedAt.HasValue)
                await writer.WriteAsync(mvo.LastScopeEvaluatedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts MVO attribute value rows on an independent NpgsqlConnection using COPY binary import.
    /// </summary>
    /// <remarks>
    /// <c>MetaverseObjectId</c> is a shadow FK — passed explicitly as a tuple element from the parent MVO.
    /// </remarks>
    private static async Task BulkInsertMvoAttributeValuesOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<(Guid MvoId, MetaverseObjectAttributeValue Value)> attributeValues)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "MetaverseObjectAttributeValues" ({MvoBulkInsertColumns.ToQuotedList(MvoBulkInsertColumns.MetaverseObjectAttributeValues)}) FROM STDIN (FORMAT binary)""");

        foreach (var (mvoId, av) in attributeValues)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(av.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(mvoId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(av.AttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
            if (av.StringValue is not null)
                await writer.WriteAsync(av.StringValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (av.DateTimeValue.HasValue)
                await writer.WriteAsync(av.DateTimeValue.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
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
            if (av.ReferenceValueId.HasValue)
                await writer.WriteAsync(av.ReferenceValueId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (av.UnresolvedReferenceValueId.HasValue)
                await writer.WriteAsync(av.UnresolvedReferenceValueId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (av.ContributedBySystemId.HasValue)
                await writer.WriteAsync(av.ContributedBySystemId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (av.ContributedBySyncRuleId.HasValue)
                await writer.WriteAsync(av.ContributedBySyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(av.NullValue, NpgsqlTypes.NpgsqlDbType.Boolean);
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts MVO rows using the main EF connection (single-connection fallback for small batches).
    /// </summary>
    private async Task BulkInsertMvosViaEfAsync(List<MetaverseObject> objects)
    {
        var columnsPerRow = MvoBulkInsertColumns.MetaverseObjects.Length;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(objects, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append($@"INSERT INTO ""MetaverseObjects"" ({MvoBulkInsertColumns.ToQuotedList(MvoBulkInsertColumns.MetaverseObjects)}) VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append('(').Append(string.Join(", ", Enumerable.Range(offset, columnsPerRow).Select(n => $"{{{n}}}"))).Append(')');

                var mvo = chunk[i];
                parameters.Add(mvo.Id);
                parameters.Add(mvo.Created);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.LastUpdated, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(mvo.Type.Id);
                parameters.Add((int)mvo.Status);
                parameters.Add((int)mvo.Origin);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.LastConnectorDisconnectedDate, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add((int)mvo.DeletionInitiatedByType);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionInitiatedById, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionInitiatedByName, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.CachedDisplayName, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(mvo.ScopeReviewPending);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.LastScopeEvaluatedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz));
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Inserts MVO attribute value rows using the main EF connection (single-connection fallback).
    /// </summary>
    private async Task BulkInsertMvoAttributeValuesViaEfAsync(
        List<(Guid MvoId, MetaverseObjectAttributeValue Value)> attributeValues)
    {
        var columnsPerRow = MvoBulkInsertColumns.MetaverseObjectAttributeValues.Length;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(attributeValues, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append($@"INSERT INTO ""MetaverseObjectAttributeValues"" ({MvoBulkInsertColumns.ToQuotedList(MvoBulkInsertColumns.MetaverseObjectAttributeValues)}) VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append('(').Append(string.Join(", ", Enumerable.Range(offset, columnsPerRow).Select(n => $"{{{n}}}"))).Append(')');

                var (mvoId, av) = chunk[i];
                parameters.Add(av.Id);
                parameters.Add(mvoId);
                parameters.Add(av.AttributeId);
                parameters.Add(BulkSqlHelpers.NullableParam(av.StringValue, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(av.DateTimeValue, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(av.IntValue, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(av.LongValue, NpgsqlTypes.NpgsqlDbType.Bigint));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ByteValue, NpgsqlTypes.NpgsqlDbType.Bytea));
                parameters.Add(BulkSqlHelpers.NullableParam(av.GuidValue, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.BoolValue, NpgsqlTypes.NpgsqlDbType.Boolean));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ReferenceValueId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.UnresolvedReferenceValueId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ContributedBySystemId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ContributedBySyncRuleId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(av.NullValue);
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    #endregion

    #region Metaverse Object — Bulk Update

    /// <summary>
    /// Updates already-persisted Metaverse Objects and reconciles their attribute values entirely via raw SQL,
    /// deliberately bypassing EF Core's change tracker and the <c>xmin</c> optimistic-concurrency check.
    /// <para>
    /// This replaces the previous EF <c>SaveChangesAsync</c> update path. During synchronisation the worker is the
    /// sole writer of the metaverse (all operations serialise through it), and Metaverse Objects are created here via
    /// the raw COPY path, which attaches them to the tracker without their real store-generated xmin. The next EF
    /// update of such an object therefore issued <c>... WHERE xmin = 0</c>, matched no rows, and threw an unhandled
    /// <see cref="DbUpdateConcurrencyException"/> that aborted the whole run (the pre-release Full Regression
    /// Scenario14-AttributePriority failure). Keying updates by <c>Id</c> and dropping the xmin predicate removes that
    /// failure class and converges the update path with the create path, per the design note formerly on
    /// <see cref="CreateMetaverseObjectsBulkAsync"/>. Optimistic concurrency via xmin remains in force for the EF write
    /// paths where a concurrent writer genuinely exists (UI / API edits).
    /// </para>
    /// <para>
    /// The sync engine models every attribute change as a removal plus an addition (see
    /// <c>SyncEngine.ApplyPendingAttributeChanges</c>), so existing attribute values are never mutated in place. The
    /// reconciliation therefore only inserts newly added values and deletes removed ones, computed by diffing the
    /// in-memory collection against the database. This is safe because Metaverse Objects reaching this path always
    /// carry their complete attribute-value set (loaded with an unfiltered Include), so a value absent from the
    /// in-memory collection is genuinely a removal, not an unloaded value.
    /// </para>
    /// </summary>
    public async Task UpdateMetaverseObjectsBulkAsync(List<MetaverseObject> metaverseObjects)
    {
        if (metaverseObjects.Count == 0)
            return;

        // Pre-generate ids for newly added attribute values and fix up reference FKs from in-memory
        // navigations, mirroring the create path so the raw insert below has everything it needs.
        foreach (var mvo in metaverseObjects)
        {
            foreach (var av in mvo.AttributeValues)
            {
                if (av.Id == Guid.Empty)
                    av.Id = Guid.NewGuid();

                if (av.ReferenceValue != null && av.ReferenceValue.Id != Guid.Empty && !av.ReferenceValueId.HasValue)
                    av.ReferenceValueId = av.ReferenceValue.Id;
            }
        }

        var mvoIds = metaverseObjects.Select(m => m.Id).ToList();

        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);
        try
        {
            // One transaction per batch: the row update, attribute-value inserts and deletes commit atomically,
            // so a Metaverse Object is never left half-updated (Synchronisation Integrity: no partial state).
            await using var transaction = await _context.Database.BeginTransactionAsync();

            // 1. Update the MetaverseObjects rows (scalar columns), keyed by Id, with no xmin predicate.
            await BulkUpdateMvoRowsViaEfAsync(metaverseObjects);

            // 2. Reconcile attribute values: insert the added values, delete the removed ones.
            var existingAvIdsByMvo = await LoadExistingAttributeValueIdsAsync(mvoIds);
            var attributeValuesToInsert = new List<(Guid MvoId, MetaverseObjectAttributeValue Value)>();
            var attributeValueIdsToDelete = new List<Guid>();

            foreach (var mvo in metaverseObjects)
            {
                var existing = existingAvIdsByMvo.GetValueOrDefault(mvo.Id) ?? new HashSet<Guid>();
                var currentIds = new HashSet<Guid>();

                foreach (var av in mvo.AttributeValues)
                {
                    currentIds.Add(av.Id);
                    if (!existing.Contains(av.Id))
                        attributeValuesToInsert.Add((mvo.Id, av));
                }

                foreach (var existingId in existing.Where(existingId => !currentIds.Contains(existingId)))
                    attributeValueIdsToDelete.Add(existingId);
            }

            if (attributeValuesToInsert.Count > 0)
                await BulkInsertMvoAttributeValuesViaEfAsync(attributeValuesToInsert);

            if (attributeValueIdsToDelete.Count > 0)
                await DeleteMetaverseObjectAttributeValuesByIdsAsync(attributeValueIdsToDelete);

            await transaction.CommitAsync();
        }
        finally
        {
            _context.Database.SetCommandTimeout(previousTimeout);
        }

        // 3. Detach the persisted graph from the change tracker. The Metaverse Object was loaded tracked and may be
        //    marked Modified, and any attribute values added during processing may be tracked as Added; leaving them
        //    would let a later EF SaveChangesAsync in the same page flush (e.g. the CSO join-state update) re-run the
        //    xmin-guarded MVO update that this method exists to avoid, or re-insert attribute values already written
        //    here as raw SQL (a duplicate-key violation). Detaching is the definitive guard against recurrence: unlike
        //    re-attaching as Unchanged (the create path's bridge), a detached entity cannot be re-persisted by EF at all.
        DetachPersistedMvoGraphs(metaverseObjects);
    }

    /// <summary>
    /// Issues the batched, VALUES-list UPDATE of the MetaverseObjects rows on the EF connection (so it joins the
    /// ambient transaction). Only the mutable columns in <see cref="MvoBulkInsertColumns.MetaverseObjectsUpdate"/>
    /// are written; there is no xmin predicate, so the update is keyed solely on the immutable Id.
    /// </summary>
    private async Task BulkUpdateMvoRowsViaEfAsync(List<MetaverseObject> objects)
    {
        // Id plus the eleven mutable columns.
        const int columnsPerRow = 12;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        // Reuse one StringBuilder across chunks (Clear() each iteration) rather than allocating per chunk.
        var sql = new StringBuilder();
        foreach (var chunk in BulkSqlHelpers.ChunkList(objects, chunkSize))
        {
            sql.Clear();
            sql.Append("""
                UPDATE "MetaverseObjects" AS m SET
                    "LastUpdated" = v."LastUpdated",
                    "TypeId" = v."TypeId",
                    "Status" = v."Status",
                    "Origin" = v."Origin",
                    "LastConnectorDisconnectedDate" = v."LastConnectorDisconnectedDate",
                    "DeletionInitiatedByType" = v."DeletionInitiatedByType",
                    "DeletionInitiatedById" = v."DeletionInitiatedById",
                    "DeletionInitiatedByName" = v."DeletionInitiatedByName",
                    "CachedDisplayName" = v."CachedDisplayName",
                    "ScopeReviewPending" = v."ScopeReviewPending",
                    "LastScopeEvaluatedAt" = v."LastScopeEvaluatedAt"
                FROM (VALUES
                """);

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(',');
                var o = i * columnsPerRow;
                // Explicit casts give the VALUES columns a definite type even when a whole chunk is null for a column.
                sql.Append($"({{{o}}}::uuid,{{{o + 1}}}::timestamptz,{{{o + 2}}}::int,{{{o + 3}}}::int,{{{o + 4}}}::int,{{{o + 5}}}::timestamptz,{{{o + 6}}}::int,{{{o + 7}}}::uuid,{{{o + 8}}}::text,{{{o + 9}}}::text,{{{o + 10}}}::boolean,{{{o + 11}}}::timestamptz)");

                var mvo = chunk[i];
                parameters.Add(mvo.Id);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.LastUpdated, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(mvo.Type.Id);
                parameters.Add((int)mvo.Status);
                parameters.Add((int)mvo.Origin);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.LastConnectorDisconnectedDate, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add((int)mvo.DeletionInitiatedByType);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionInitiatedById, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionInitiatedByName, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.CachedDisplayName, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(mvo.ScopeReviewPending);
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.LastScopeEvaluatedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz));
            }

            sql.Append("""
                ) AS v("Id","LastUpdated","TypeId","Status","Origin","LastConnectorDisconnectedDate","DeletionInitiatedByType","DeletionInitiatedById","DeletionInitiatedByName","CachedDisplayName","ScopeReviewPending","LastScopeEvaluatedAt")
                WHERE m."Id" = v."Id"
                """);

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Loads the current database attribute-value ids for the given Metaverse Objects, grouped by Metaverse Object id.
    /// Used to compute the insert/delete delta for the update path. Runs on the EF connection and ambient transaction.
    /// </summary>
    private async Task<Dictionary<Guid, HashSet<Guid>>> LoadExistingAttributeValueIdsAsync(List<Guid> mvoIds)
    {
        var result = new Dictionary<Guid, HashSet<Guid>>();
        if (mvoIds.Count == 0)
            return result;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(
            @"SELECT ""Id"", ""MetaverseObjectId"" FROM ""MetaverseObjectAttributeValues"" WHERE ""MetaverseObjectId"" = ANY(@ids)",
            connection, transaction);
        command.Parameters.AddWithValue("ids", mvoIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var attributeValueId = reader.GetGuid(0);
            var metaverseObjectId = reader.GetGuid(1);
            if (!result.TryGetValue(metaverseObjectId, out var set))
            {
                set = [];
                result[metaverseObjectId] = set;
            }
            set.Add(attributeValueId);
        }

        return result;
    }

    /// <summary>
    /// Detaches the given Metaverse Objects and every tracked attribute value belonging to them, so no later EF
    /// SaveChangesAsync re-persists what this method has already written via raw SQL. See the call site for why this
    /// is essential rather than cosmetic.
    /// </summary>
    private void DetachPersistedMvoGraphs(List<MetaverseObject> metaverseObjects)
    {
        var mvoIdSet = metaverseObjects.Select(m => m.Id).ToHashSet();

        foreach (var avEntry in _context.ChangeTracker.Entries<MetaverseObjectAttributeValue>().ToList())
        {
            var linkedByNavigation = avEntry.Entity.MetaverseObject != null && mvoIdSet.Contains(avEntry.Entity.MetaverseObject.Id);
            var linkedByForeignKey = avEntry.Property("MetaverseObjectId").CurrentValue is Guid ownerId && mvoIdSet.Contains(ownerId);
            if (linkedByNavigation || linkedByForeignKey)
                avEntry.State = EntityState.Detached;
        }

        foreach (var entry in metaverseObjects.Select(mvo => _context.Entry(mvo)).Where(entry => entry.State != EntityState.Detached))
            entry.State = EntityState.Detached;
    }

    #endregion

    #region Metaverse Object — Reference FK Fixup

    /// <summary>
    /// Tactical fixup: populates ReferenceValueId on MetaverseObjectAttributeValues where the FK is
    /// null but the ReferenceValue navigation is set in-memory.
    ///
    /// Background: ProcessReferenceAttribute sets the ReferenceValue navigation property for same-page
    /// references (so EF handles insert ordering for not-yet-persisted MVOs). EF is supposed to infer
    /// the scalar FK from the navigation at SaveChanges time, but this silently fails when entities are
    /// managed via explicit Entry().State (as in UpdateMetaverseObjectsAsync). The result is MVO attribute
    /// values with ReferenceValue navigation set in-memory but ReferenceValueId NULL in the database.
    ///
    /// This method iterates the in-memory MVOs after persistence, collects attribute values where the
    /// navigation is set but the FK is null, and issues a targeted SQL UPDATE for those specific rows.
    ///
    /// TACTICAL: This will be retired when MVO persistence is converted to direct SQL, which will
    /// always set scalar FKs explicitly without relying on EF navigation inference.
    /// </summary>
    public async Task<int> FixupMvoReferenceValueIdsAsync(IReadOnlyList<(Guid MvoId, int AttributeId, Guid TargetMvoId)> fixups)
    {
        if (fixups.Count == 0)
            return 0;

        // Batch UPDATE using a VALUES list. Match by (MvoId, AttributeId, TargetMvoId) since
        // the attribute value's primary key may not be assigned yet when fixups are collected
        // (EF assigns it during SaveChangesAsync).
        const int chunkSize = 500;
        var totalFixed = 0;

        foreach (var chunk in fixups.Chunk(chunkSize))
        {
            var sb = new StringBuilder();
            sb.Append("""
                UPDATE "MetaverseObjectAttributeValues" mav
                SET "ReferenceValueId" = v."TargetMvoId"
                FROM (VALUES
                """);

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"({{{i * 3}}}::uuid, {{{i * 3 + 1}}}::int, {{{i * 3 + 2}}}::uuid)");
                parameters.Add(chunk[i].MvoId);
                parameters.Add(chunk[i].AttributeId);
                parameters.Add(chunk[i].TargetMvoId);
            }

            sb.Append("""
                ) AS v("MvoId", "AttrId", "TargetMvoId")
                WHERE mav."MetaverseObjectId" = v."MvoId"
                  AND mav."AttributeId" = v."AttrId"
                  AND mav."ReferenceValueId" IS NULL
                """);

            totalFixed += await _context.Database.ExecuteSqlRawAsync(sb.ToString(), parameters.ToArray());
        }

        Log.Information("FixupMvoReferenceValueIdsAsync: Fixed {Count} MVO attribute value reference FKs", totalFixed);
        return totalFixed;
    }

    #endregion

    #region Metaverse Object - Bulk Delete

    /// <summary>
    /// Deletes multiple MVOs in one set-based pass (issue #993). Each FK cleanup statement from
    /// the singular <see cref="SyncRepository.DeleteMetaverseObjectAsync"/> runs once for the whole
    /// batch via <c>= ANY</c> instead of once per object, and the MVO rows themselves are removed
    /// in a single SaveChanges. Semantics per object are identical to the singular method.
    /// </summary>
    public async Task DeleteMetaverseObjectsAsync(IReadOnlyCollection<MetaverseObject> metaverseObjects)
    {
        if (metaverseObjects.Count == 0)
            return;

        var mvoIds = metaverseObjects.Select(mvo => mvo.Id).ToArray();

        // Null out FK reference in Activities to preserve audit history
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Activities"" SET ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = ANY({0})",
            mvoIds);

        // Stamp DeletedMetaverseObjectId on all prior change records for these MVOs so that
        // GetDeletedMvoChangeHistoryAsync can correlate them after the FK is nulled.
        // Then null the FK to allow the MVOs to be deleted without constraint violations.
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectChanges"" SET ""DeletedMetaverseObjectId"" = ""MetaverseObjectId"", ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = ANY({0})",
            mvoIds);

        // Null out FK reference in ConnectedSystemObjects to detach any CSOs still joined
        // to these MVOs, and fix up tracked instances to match the database state so a later
        // SaveChangesAsync does not write the stale FK value back.
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = ANY({0})",
            mvoIds);
        var mvoIdSet = mvoIds.ToHashSet();
        foreach (var trackedCso in _context.ChangeTracker.Entries<Models.Staging.ConnectedSystemObject>()
            .Where(e => e.Entity.MetaverseObjectId.HasValue && mvoIdSet.Contains(e.Entity.MetaverseObjectId.Value)))
        {
            trackedCso.Entity.MetaverseObjectId = null;
            trackedCso.Entity.MetaverseObject = null;
        }

        // Null out reference attribute values on other MVOs that point at the deleted MVOs
        // (e.g. Manager references), and reference values in change tracking records.
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = ANY({0})",
            mvoIds);
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectChangeAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = ANY({0})",
            mvoIds);

        _context.MetaverseObjects.RemoveRange(metaverseObjects);
        await _context.SaveChangesAsync();

        Log.Information("DeleteMetaverseObjectsAsync: Deleted {Count} Metaverse Objects in bulk", metaverseObjects.Count);
    }

    #endregion
}
