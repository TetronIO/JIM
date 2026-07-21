// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Connected System Object — Parallel Bulk Create

    /// <summary>
    /// Bulk creates CSOs with their attribute values using parallel multi-connection writes.
    /// Each partition of CSOs is written on its own <see cref="NpgsqlConnection"/>, allowing
    /// PostgreSQL to utilise multiple CPU cores during the INSERT phase.
    /// </summary>
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds = null)
    {
        if (connectedSystemObjects.Count == 0)
            return;

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
        var allowedCsoIds = new HashSet<Guid>(connectedSystemObjects.Select(c => c.Id));
        if (previouslyCommittedCsoIds != null)
            allowedCsoIds.UnionWith(previouslyCommittedCsoIds);

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

        var parallelism = ParallelBatchWriter.GetWriteParallelism();
        var connectionString = _connectionStringForParallelWrites;

        // For small batches (under parallelism threshold), use the main EF connection directly.
        // The parallel overhead (opening N connections, partitioning) isn't worthwhile for small writes.
        if (connectedSystemObjects.Count < parallelism * 50 || connectionString == null)
        {
            await CreateCsosOnSingleConnectionAsync(connectedSystemObjects);
            return;
        }

        Log.Information("CreateConnectedSystemObjectsAsync: Writing {Count} CSOs across {Parallelism} parallel connections",
            connectedSystemObjects.Count, parallelism);

        // Two-phase parallel write: CSO rows first (committed), then attribute values.
        // Phase 1 commits all CSO rows across all parallel connections, making them visible
        // to all subsequent transactions. Phase 2 then writes attribute values with full FK
        // resolution — ReferenceValueId can point to any CSO in this batch or prior batches
        // without cross-partition FK violations.
        //
        // This eliminates the need for FixupCrossBatchReferenceIdsAsync to resolve references
        // that were nulled due to cross-partition isolation.

        // Phase 1: Write CSO rows across parallel connections and commit
        var partitions = ParallelBatchWriter.Partition(connectedSystemObjects, parallelism);
        await ParallelBatchWriter.ExecuteAsync(
            connectedSystemObjects,
            parallelism,
            connectionString,
            async (connection, partition) =>
            {
                await using var transaction = await connection!.BeginTransactionAsync();
                await BulkInsertCsosOnConnectionAsync(connection, transaction, partition);
                await transaction.CommitAsync();
            });

        // Phase 2: Write attribute values across parallel connections
        // All CSO rows are now committed and visible, so cross-partition FK references succeed.
        // Build the full set of allowed CSO IDs: this batch + previously committed batches.
        var allBatchCsoIds = new HashSet<Guid>(connectedSystemObjects.Select(c => c.Id));
        if (previouslyCommittedCsoIds != null)
            allBatchCsoIds.UnionWith(previouslyCommittedCsoIds);

        await ParallelBatchWriter.ExecuteAsync(
            connectedSystemObjects,
            parallelism,
            connectionString,
            async (connection, partition) =>
            {
                var attributeValues = partition
                    .SelectMany(cso => cso.AttributeValues.Select(av => (CsoId: cso.Id, Value: av)))
                    .ToList();

                if (attributeValues.Count > 0)
                {
                    await using var transaction = await connection!.BeginTransactionAsync();
                    await BulkInsertCsoAttributeValuesOnConnectionAsync(connection, transaction, attributeValues, allBatchCsoIds);
                    await transaction.CommitAsync();
                }
            });
    }

    /// <summary>
    /// Falls back to the shared EF-based implementation for small batches.
    /// </summary>
    private async Task CreateCsosOnSingleConnectionAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        await using var transaction = await _context.Database.BeginTransactionAsync();

        await BulkInsertCsosViaEfAsync(connectedSystemObjects);

        var allAttributeValues = connectedSystemObjects
            .SelectMany(cso => cso.AttributeValues.Select(av => (CsoId: cso.Id, Value: av)))
            .ToList();

        if (allAttributeValues.Count > 0)
            await BulkInsertCsoAttributeValuesViaEfAsync(allAttributeValues);

        await transaction.CommitAsync();
        _context.Database.SetCommandTimeout(previousTimeout);
    }

    /// <summary>
    /// Inserts CSO rows on an independent NpgsqlConnection using COPY binary import.
    /// COPY binary streams data directly without SQL parsing or parameter limits,
    /// providing significantly higher throughput than parameterised INSERT.
    /// </summary>
    private static async Task BulkInsertCsosOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ConnectedSystemObject> objects)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY "ConnectedSystemObjects" (
                "Id", "ConnectedSystemId", "Created", "LastUpdated", "TypeId",
                "ExternalIdAttributeId", "SecondaryExternalIdAttributeId",
                "Status", "MetaverseObjectId", "JoinType", "DateJoined"
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
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts CSO attribute value rows on an independent NpgsqlConnection using COPY binary import.
    /// </summary>
    /// <param name="partitionCsoIds">CSO IDs being written on THIS connection. ReferenceValueId FKs
    /// pointing to CSOs outside this partition are written as null to avoid FK violations — the
    /// referenced CSO may be on a different parallel connection and not yet committed.
    /// FixupCrossBatchReferenceIdsAsync resolves these after all batches complete.</param>
    private static async Task BulkInsertCsoAttributeValuesOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)> attributeValues,
        HashSet<Guid>? partitionCsoIds = null)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY "ConnectedSystemObjectAttributeValues" (
                "Id", "ConnectedSystemObjectId", "AttributeId", "StringValue",
                "DateTimeValue", "IntValue", "LongValue", "ByteValue",
                "GuidValue", "BoolValue", "ReferenceValueId", "UnresolvedReferenceValue"
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

    /// <summary>
    /// Inserts CSO rows using the main EF connection (single-connection fallback for small batches).
    /// </summary>
    private async Task BulkInsertCsosViaEfAsync(List<ConnectedSystemObject> objects)
    {
        const int columnsPerRow = 11;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(objects, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append(@"INSERT INTO ""ConnectedSystemObjects"" (""Id"", ""ConnectedSystemId"", ""Created"", ""LastUpdated"", ""TypeId"", ""ExternalIdAttributeId"", ""SecondaryExternalIdAttributeId"", ""Status"", ""MetaverseObjectId"", ""JoinType"", ""DateJoined"") VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"({{{offset}}}, {{{offset + 1}}}, {{{offset + 2}}}, {{{offset + 3}}}, {{{offset + 4}}}, {{{offset + 5}}}, {{{offset + 6}}}, {{{offset + 7}}}, {{{offset + 8}}}, {{{offset + 9}}}, {{{offset + 10}}})");

                var cso = chunk[i];
                parameters.Add(cso.Id);
                parameters.Add(cso.ConnectedSystemId);
                parameters.Add(cso.Created);
                parameters.Add(BulkSqlHelpers.NullableParam(cso.LastUpdated, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(cso.TypeId);
                parameters.Add(cso.ExternalIdAttributeId);
                parameters.Add(BulkSqlHelpers.NullableParam(cso.SecondaryExternalIdAttributeId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add((int)cso.Status);
                parameters.Add(BulkSqlHelpers.NullableParam(cso.MetaverseObjectId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add((int)cso.JoinType);
                parameters.Add(BulkSqlHelpers.NullableParam(cso.DateJoined, NpgsqlTypes.NpgsqlDbType.TimestampTz));
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Inserts CSO attribute value rows using the main EF connection (single-connection fallback).
    /// </summary>
    private async Task BulkInsertCsoAttributeValuesViaEfAsync(
        List<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)> attributeValues)
    {
        const int columnsPerRow = 12;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(attributeValues, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append(@"INSERT INTO ""ConnectedSystemObjectAttributeValues"" (""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""StringValue"", ""DateTimeValue"", ""IntValue"", ""LongValue"", ""ByteValue"", ""GuidValue"", ""BoolValue"", ""ReferenceValueId"", ""UnresolvedReferenceValue"") VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"({{{offset}}}, {{{offset + 1}}}, {{{offset + 2}}}, {{{offset + 3}}}, {{{offset + 4}}}, {{{offset + 5}}}, {{{offset + 6}}}, {{{offset + 7}}}, {{{offset + 8}}}, {{{offset + 9}}}, {{{offset + 10}}}, {{{offset + 11}}})");

                var (csoId, av) = chunk[i];
                parameters.Add(av.Id);
                parameters.Add(csoId);
                parameters.Add(av.AttributeId);
                parameters.Add(BulkSqlHelpers.NullableParam(av.StringValue, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(av.DateTimeValue, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(av.IntValue, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(av.LongValue, NpgsqlTypes.NpgsqlDbType.Bigint));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ByteValue, NpgsqlTypes.NpgsqlDbType.Bytea));
                parameters.Add(BulkSqlHelpers.NullableParam(av.GuidValue, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.BoolValue, NpgsqlTypes.NpgsqlDbType.Boolean));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ReferenceValueId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.UnresolvedReferenceValue, NpgsqlTypes.NpgsqlDbType.Text));
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    #endregion

    #region Optimistic Export Apply (issue #1079)

    /// <summary>
    /// Persists an optimistic export apply delta: bulk-inserts new attribute value rows via COPY
    /// binary import and bulk-deletes superseded ones. Touches NO parent CSO row - see the remark
    /// on <see cref="JIM.Data.Repositories.ISyncRepository.ApplyExportedAttributeValuesAsync"/> for
    /// why <c>LastUpdated</c> must stay untouched here (it is what re-arms the Full Synchronisation
    /// unchanged-object watermark for a no-op confirming import).
    /// </summary>
    public async Task ApplyExportedAttributeValuesAsync(List<ConnectedSystemObjectAttributeValue> additions, List<Guid> removalValueIds)
    {
        if (additions.Count == 0 && removalValueIds.Count == 0)
            return;

        if (removalValueIds.Count > 0)
            await DeleteCsoAttributeValuesByIdAsync(removalValueIds);

        if (additions.Count > 0)
            await BulkInsertOptimisticApplyAttributeValuesRawAsync(additions);
    }

    /// <summary>
    /// Deletes <see cref="ConnectedSystemObjectAttributeValue"/> rows by Id. Detaches any tracked
    /// instances first (belt-and-braces per the raw-SQL rule in <c>src/CLAUDE.md</c>): the export
    /// batch loader is <c>AsNoTracking</c> + <c>ClearChangeTracker()</c> per batch today, but a
    /// future caller on a tracking context must not be silently corrupted - see
    /// <see cref="DetachTrackedEntities{T}"/>'s remarks for the <c>DbUpdateConcurrencyException</c>
    /// failure mode this guards against.
    /// </summary>
    private async Task DeleteCsoAttributeValuesByIdAsync(List<Guid> removalValueIds)
    {
        DetachTrackedEntities<ConnectedSystemObjectAttributeValue>(av => removalValueIds.Contains(av.Id));

        if (_context.Database.IsRelational())
        {
            await _context.ConnectedSystemObjectAttributeValues
                .Where(av => removalValueIds.Contains(av.Id))
                .ExecuteDeleteAsync();
        }
        else
        {
            var entities = await _context.ConnectedSystemObjectAttributeValues
                .Where(av => removalValueIds.Contains(av.Id))
                .ToListAsync();
            _context.ConnectedSystemObjectAttributeValues.RemoveRange(entities);
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Bulk inserts <see cref="ConnectedSystemObjectAttributeValue"/> rows using Npgsql binary
    /// COPY. Mirrors <c>ConnectedSystemRepository.BulkInsertCsoAttributeValuesRawAsync</c> exactly
    /// (same columns, same per-column <see cref="NpgsqlTypes.NpgsqlDbType"/> mapping, same null
    /// handling); duplicated rather than shared because that method is private to
    /// <c>ConnectedSystemRepository</c> and this repository owns its own connection lifecycle.
    /// </summary>
    private async Task BulkInsertOptimisticApplyAttributeValuesRawAsync(List<ConnectedSystemObjectAttributeValue> additions)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "ConnectedSystemObjectAttributeValues" (
                "Id", "ConnectedSystemObjectId", "AttributeId", "StringValue", "DateTimeValue",
                "IntValue", "LongValue", "ByteValue", "GuidValue", "BoolValue",
                "ReferenceValueId", "UnresolvedReferenceValue"
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var av in additions)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(av.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(av.ConnectedSystemObject.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
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
            if (av.UnresolvedReferenceValue is not null)
                await writer.WriteAsync(av.UnresolvedReferenceValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    #endregion

    #region Cross-Batch Reference Fixup

    public async Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId)
    {
        // Resolve ReferenceValueId FKs for attribute values where:
        // - UnresolvedReferenceValue is set (contains the raw DN/secondary external ID string)
        // - ReferenceValueId is null (reference not yet resolved to a CSO ID)
        //
        // Defence-in-depth for cross-run reference resolution (e.g., groups imported in a later
        // run referencing users from a prior run). Within a single import run, upfront CSO ID
        // pre-generation in SyncImportTaskProcessor should resolve all FKs before persistence.
        //
        // Uses case-insensitive LOWER() comparison because LDAP Distinguished Names are
        // case-insensitive per RFC 4514, and different connector runs or LDAP servers may
        // return DNs with different capitalisation.
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);
        try
        {
            // Fast early exit: skip the expensive multi-table JOIN UPDATE when there are no
            // unresolved references. This is the common case for CSV imports, training imports,
            // and confirming imports — only LDAP imports with out-of-order batched groups need fixup.
            var unresolvedCount = await _repo.ConnectedSystems.GetUnresolvedReferenceCountAsync(connectedSystemId);
            if (unresolvedCount == 0)
                return 0;

            return await _context.Database.ExecuteSqlRawAsync(
                """
                UPDATE "ConnectedSystemObjectAttributeValues" av
                SET "ReferenceValueId" = target_cso."Id"
                FROM "ConnectedSystemObjects" owner_cso
                JOIN "ConnectedSystemObjects" target_cso ON target_cso."ConnectedSystemId" = {0}
                JOIN "ConnectedSystemObjectAttributeValues" target_av ON target_av."ConnectedSystemObjectId" = target_cso."Id"
                JOIN "ConnectedSystemAttributes" target_attr ON target_attr."Id" = target_av."AttributeId"
                    AND target_attr."IsSecondaryExternalId" = true
                WHERE owner_cso."ConnectedSystemId" = {0}
                  AND av."ConnectedSystemObjectId" = owner_cso."Id"
                  AND av."UnresolvedReferenceValue" IS NOT NULL
                  AND av."ReferenceValueId" IS NULL
                  AND target_av."StringValue" IS NOT NULL
                  AND LOWER(av."UnresolvedReferenceValue") = LOWER(target_av."StringValue")
                """,
                connectedSystemId);
        }
        finally
        {
            _context.Database.SetCommandTimeout(previousTimeout);
        }
    }

    /// <summary>
    /// Rows updated per statement by <see cref="FixupCrossBatchChangeRecordReferenceIdsAsync"/> when
    /// the caller does not specify a batch size. Sized so each UPDATE completes comfortably inside
    /// <see cref="PostgresDataRepository.BulkOperationCommandTimeoutSeconds"/>: the Scale500k25kGroups
    /// run (2026-07-18) showed a single-statement update of 6.5M rows exceeds 300s, while the
    /// matching join scan alone takes ~17s, so the write side dominates and must be bounded.
    /// </summary>
    private const int DefaultChangeRecordReferenceFixupBatchSize = 250_000;

    public async Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId, int? batchSize = null)
    {
        // Change record attribute values (ConnectedSystemObjectChangeAttributeValues) store reference
        // DN strings in StringValue but have ReferenceValueId nulled during COPY binary persistence
        // to avoid FK violations when the referenced CSO is in a later batch. After all batches
        // complete, this method resolves those references by matching StringValue against the
        // secondary external ID attribute values of CSOs in the same Connected System.
        //
        // Unlike the CSO attribute value fixup, there is no dedicated "UnresolvedReferenceValue"
        // column on change records; the DN is stored in StringValue alongside regular string values.
        // The resolution is safe because it only matches when StringValue equals a secondary external
        // ID value (case-insensitive per RFC 4514, hence the LOWER() comparison), so non-reference
        // string values are naturally excluded by the JOIN.
        //
        // Executed in two phases because a single UPDATE does not survive customer scale: the
        // Scale500k25kGroups run accumulated 6.5M unresolved rows across the sync and export stages
        // and the previous single-statement UPDATE blew the bulk command timeout, hard-failing the
        // confirming import. Phase 1 runs the expensive join once, materialising every resolution
        // into a session-local temp table. Phase 2 applies them in bounded batches, each a separate
        // auto-committed statement well inside the timeout. Partial progress is durable and the
        // operation is idempotent (resolved rows drop out of the phase 1 join), so a failure part
        // way through simply leaves less for the next invocation. The temp table only ever selects
        // resolvable rows, so unresolvable DNs (deleted targets, placeholder members) cannot cause
        // repeat work here; they are re-evaluated on the next invocation.
        //
        // The temp table is scoped to the underlying connection, so every command below must run on
        // the same physical connection: the RawSqlConnectionLease holds it open for the duration and
        // Npgsql's pool reset (DISCARD ALL) clears any leftovers if we fail before the drop.
        var effectiveBatchSize = batchSize ?? DefaultChangeRecordReferenceFixupBatchSize;
        if (effectiveBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        const string tempTableName = "_jim_change_record_reference_fixup";

        await using (var dropCmd = new NpgsqlCommand($"""DROP TABLE IF EXISTS "{tempTableName}";""", npgsqlConn))
        {
            dropCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
            await dropCmd.ExecuteNonQueryAsync();
        }

        await using (var createCmd = new NpgsqlCommand($"""
            CREATE TEMP TABLE "{tempTableName}" AS
            SELECT row_number() OVER () AS rn, s.cav_id, s.target_id
            FROM (
                SELECT cav."Id" AS cav_id, target_cso."Id" AS target_id
                FROM "ConnectedSystemObjectChangeAttributeValues" cav
                JOIN "ConnectedSystemObjectChangeAttributes" ca ON cav."ConnectedSystemObjectChangeAttributeId" = ca."Id"
                JOIN "ConnectedSystemObjectChanges" cc ON cc."Id" = ca."ConnectedSystemChangeId"
                JOIN "ConnectedSystemObjects" target_cso ON target_cso."ConnectedSystemId" = @connectedSystemId
                JOIN "ConnectedSystemObjectAttributeValues" target_av ON target_av."ConnectedSystemObjectId" = target_cso."Id"
                JOIN "ConnectedSystemAttributes" target_attr ON target_attr."Id" = target_av."AttributeId"
                    AND target_attr."IsSecondaryExternalId" = true
                WHERE cc."ConnectedSystemId" = @connectedSystemId
                  AND ca."AttributeType" = @referenceAttributeType
                  AND cav."StringValue" IS NOT NULL
                  AND cav."ReferenceValueId" IS NULL
                  AND target_av."StringValue" IS NOT NULL
                  AND LOWER(cav."StringValue") = LOWER(target_av."StringValue")
            ) s;
            """, npgsqlConn))
        {
            createCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
            createCmd.Parameters.AddWithValue("connectedSystemId", connectedSystemId);
            createCmd.Parameters.AddWithValue("referenceAttributeType", (int)AttributeDataType.Reference);
            await createCmd.ExecuteNonQueryAsync();
        }

        await using (var indexCmd = new NpgsqlCommand($"""CREATE INDEX ON "{tempTableName}" (rn);""", npgsqlConn))
        {
            indexCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
            await indexCmd.ExecuteNonQueryAsync();
        }

        long totalToResolve;
        await using (var countCmd = new NpgsqlCommand($"""SELECT COUNT(*) FROM "{tempTableName}";""", npgsqlConn))
        {
            countCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
            totalToResolve = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
        }

        var totalResolved = 0;
        if (totalToResolve > 0)
        {
            await using var updateCmd = new NpgsqlCommand($"""
                UPDATE "ConnectedSystemObjectChangeAttributeValues" cav
                SET "ReferenceValueId" = f.target_id
                FROM "{tempTableName}" f
                WHERE f.rn > @rangeStart AND f.rn <= @rangeEnd
                  AND cav."Id" = f.cav_id
                """, npgsqlConn);
            updateCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
            var rangeStartParam = updateCmd.Parameters.Add("rangeStart", NpgsqlTypes.NpgsqlDbType.Bigint);
            var rangeEndParam = updateCmd.Parameters.Add("rangeEnd", NpgsqlTypes.NpgsqlDbType.Bigint);

            for (long rangeStart = 0; rangeStart < totalToResolve; rangeStart += effectiveBatchSize)
            {
                rangeStartParam.Value = rangeStart;
                rangeEndParam.Value = rangeStart + effectiveBatchSize;
                totalResolved += await updateCmd.ExecuteNonQueryAsync();
                Log.Information("FixupCrossBatchChangeRecordReferenceIdsAsync: Resolved {Resolved:N0} of {Total:N0} change record references for Connected System {ConnectedSystemId}",
                    totalResolved, totalToResolve, connectedSystemId);
            }
        }

        await using (var finalDropCmd = new NpgsqlCommand($"""DROP TABLE IF EXISTS "{tempTableName}";""", npgsqlConn))
        {
            finalDropCmd.CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds;
            await finalDropCmd.ExecuteNonQueryAsync();
        }

        return totalResolved;
    }

    #endregion

    #region Pending Export — Worker-Only Bulk Operations

    public async Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        var pendingExportsList = pendingExports.ToList();
        if (pendingExportsList.Count == 0)
            return;

        // Pre-generate IDs for all Pending Exports and their attribute value changes
        foreach (var pe in pendingExportsList)
        {
            if (pe.Id == Guid.Empty)
                pe.Id = Guid.NewGuid();

            foreach (var avc in pe.AttributeValueChanges)
            {
                if (avc.Id == Guid.Empty)
                    avc.Id = Guid.NewGuid();
            }
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();

        // Step 1: INSERT parent PendingExport rows
        await BulkInsertPendingExportsRawAsync(pendingExportsList);

        // Step 2: INSERT child attribute value change rows
        var allChanges = pendingExportsList
            .SelectMany(pe => pe.AttributeValueChanges.Select(avc => (PendingExportId: pe.Id, Change: avc)))
            .ToList();

        if (allChanges.Count > 0)
            await BulkInsertPendingExportAttributeValueChangesRawAsync(allChanges);

        await transaction.CommitAsync();
    }

    public async Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
    {
        var csoIds = connectedSystemObjectIds.ToArray();
        if (csoIds.Length == 0)
            return 0;

        // Instances of the rows this method is about to delete may be tracked on the worker's
        // long-lived context (reconciliation and navigation fix-up both track Pending Exports).
        // Detach them before the raw SQL deletes: PendingExport.SourceMetaverseObject is
        // configured SetNull-on-delete, so a tracked instance left behind makes EF Core's
        // cascade fix-up issue an UPDATE against the already-deleted row when its source MVO is
        // deleted in the same page flush; that matches zero rows and throws
        // DbUpdateConcurrencyException, poisoning every later SaveChangesAsync on the context
        // (Scenario4-DeletionRules Test 3 failure, issue #993).
        var pendingExportIds = await _context.PendingExports
            .Where(pe => pe.ConnectedSystemObjectId != null && csoIds.Contains(pe.ConnectedSystemObjectId.Value))
            .Select(pe => pe.Id)
            .ToListAsync();
        DetachTrackedChildEntities(pendingExportIds);
        DetachTrackedEntities<PendingExport>(pe => pendingExportIds.Contains(pe.Id));

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

    public async Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
    {
        var exportList = untrackedPendingExports.ToList();
        if (exportList.Count == 0)
            return;

        var pendingExportIds = exportList.Select(pe => pe.Id).ToList();

        // Delete children first, then parents. Must delete ALL children from the database,
        // not just the ones loaded in the untracked entity's collection. The change tracker
        // may contain additional tracked children from the import phase (via navigation fixup)
        // that weren't loaded by the AsNoTracking query.

        // Detach any tracked children and parents to prevent EF Core's ClientSetNull
        // behaviour from interfering with our explicit deletion order.
        DetachTrackedChildEntities(pendingExportIds);
        DetachTrackedEntities<PendingExport>(
            e => pendingExportIds.Contains(e.Id));

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
                    tracked.MaxRetries = pe.MaxRetries;
                    tracked.LastAttemptedAt = pe.LastAttemptedAt;
                    tracked.NextRetryAt = pe.NextRetryAt;
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

        // 1. Bulk update PendingExports
        await using (var createCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
        {
            createCmd.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS _pe_bulk_update (
                    "Id" uuid NOT NULL,
                    "Status" int,
                    "ChangeType" int,
                    "ErrorCount" int,
                    "MaxRetries" int,
                    "LastAttemptedAt" timestamptz,
                    "NextRetryAt" timestamptz,
                    "LastErrorMessage" text,
                    "LastErrorStackTrace" text,
                    "HasUnresolvedReferences" boolean
                ) ON COMMIT PRESERVE ROWS
                """;
            await createCmd.ExecuteNonQueryAsync();
        }

        await using (var writer = await npgsqlConn.BeginBinaryImportAsync(
            """COPY _pe_bulk_update ("Id", "Status", "ChangeType", "ErrorCount", "MaxRetries", "LastAttemptedAt", "NextRetryAt", "LastErrorMessage", "LastErrorStackTrace", "HasUnresolvedReferences") FROM STDIN (FORMAT binary)"""))
        {
            foreach (var pe in exportList)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(pe.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
                await writer.WriteAsync((int)pe.Status, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync((int)pe.ChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync(pe.ErrorCount, NpgsqlTypes.NpgsqlDbType.Integer);
                await writer.WriteAsync(pe.MaxRetries, NpgsqlTypes.NpgsqlDbType.Integer);
                if (pe.LastAttemptedAt.HasValue)
                    await writer.WriteAsync(pe.LastAttemptedAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
                else
                    await writer.WriteNullAsync();
                if (pe.NextRetryAt.HasValue)
                    await writer.WriteAsync(pe.NextRetryAt.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
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
            updateCmd.CommandText = """
                UPDATE "PendingExports" t
                SET "Status" = v."Status",
                    "ChangeType" = v."ChangeType",
                    "ErrorCount" = v."ErrorCount",
                    "MaxRetries" = v."MaxRetries",
                    "LastAttemptedAt" = v."LastAttemptedAt",
                    "NextRetryAt" = v."NextRetryAt",
                    "LastErrorMessage" = v."LastErrorMessage",
                    "LastErrorStackTrace" = v."LastErrorStackTrace",
                    "HasUnresolvedReferences" = v."HasUnresolvedReferences"
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
                        "LastExportedAt" timestamptz
                    ) ON COMMIT PRESERVE ROWS
                    """;
                await createCmd.ExecuteNonQueryAsync();
            }

            await using (var writer = await npgsqlConn.BeginBinaryImportAsync(
                """COPY _peavc_bulk_update ("Id", "Status", "LastImportedValue", "ExportAttemptCount", "LastExportedAt") FROM STDIN (FORMAT binary)"""))
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
                }
                await writer.CompleteAsync();
            }

            await using (var updateCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                updateCmd.CommandText = """
                    UPDATE "PendingExportAttributeValueChanges" t
                    SET "Status" = v."Status",
                        "LastImportedValue" = v."LastImportedValue",
                        "ExportAttemptCount" = v."ExportAttemptCount",
                        "LastExportedAt" = v."LastExportedAt"
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

        var changeIds = changeList.Select(c => c.Id).ToList();

        // Detach any tracked instances to prevent change tracker conflicts
        DetachTrackedEntities<PendingExportAttributeValueChange>(
            e => changeIds.Contains(e.Id));

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

    #region Private Pending Export Bulk Helpers

    private async Task BulkInsertPendingExportsRawAsync(List<PendingExport> exports)
    {
        const int columnsPerRow = 14;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(exports, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            sql.Append(@"INSERT INTO ""PendingExports"" (""Id"", ""ConnectedSystemId"", ""ConnectedSystemObjectId"", ""ChangeType"", ""Status"", ""ErrorCount"", ""MaxRetries"", ""LastAttemptedAt"", ""NextRetryAt"", ""LastErrorMessage"", ""LastErrorStackTrace"", ""SourceMetaverseObjectId"", ""HasUnresolvedReferences"", ""CreatedAt"") VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"({{{offset}}}, {{{offset + 1}}}, {{{offset + 2}}}, {{{offset + 3}}}, {{{offset + 4}}}, {{{offset + 5}}}, {{{offset + 6}}}, {{{offset + 7}}}, {{{offset + 8}}}, {{{offset + 9}}}, {{{offset + 10}}}, {{{offset + 11}}}, {{{offset + 12}}}, {{{offset + 13}}})");

                var pe = chunk[i];
                parameters.Add(pe.Id);
                parameters.Add(pe.ConnectedSystemId);
                parameters.Add(BulkSqlHelpers.NullableParam(pe.ConnectedSystemObjectId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add((int)pe.ChangeType);
                parameters.Add((int)pe.Status);
                parameters.Add(pe.ErrorCount);
                parameters.Add(pe.MaxRetries);
                parameters.Add(BulkSqlHelpers.NullableParam(pe.LastAttemptedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(pe.NextRetryAt, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(pe.LastErrorMessage, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(pe.LastErrorStackTrace, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(pe.SourceMetaverseObjectId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(pe.HasUnresolvedReferences);
                parameters.Add(pe.CreatedAt);
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Bulk inserts PendingExportAttributeValueChange rows using parameterised multi-row INSERT.
    /// Uses the parent PendingExport ID (shadow FK) passed explicitly since it's not a C# property.
    /// </summary>
    private async Task BulkInsertPendingExportAttributeValueChangesRawAsync(List<(Guid PendingExportId, PendingExportAttributeValueChange Change)> changes)
    {
        const int columnsPerRow = 16;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(changes, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            sql.Append(@"INSERT INTO ""PendingExportAttributeValueChanges"" (""Id"", ""PendingExportId"", ""AttributeId"", ""StringValue"", ""DateTimeValue"", ""IntValue"", ""LongValue"", ""ByteValue"", ""GuidValue"", ""BoolValue"", ""UnresolvedReferenceValue"", ""ChangeType"", ""Status"", ""ExportAttemptCount"", ""LastExportedAt"", ""LastImportedValue"") VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"({{{offset}}}, {{{offset + 1}}}, {{{offset + 2}}}, {{{offset + 3}}}, {{{offset + 4}}}, {{{offset + 5}}}, {{{offset + 6}}}, {{{offset + 7}}}, {{{offset + 8}}}, {{{offset + 9}}}, {{{offset + 10}}}, {{{offset + 11}}}, {{{offset + 12}}}, {{{offset + 13}}}, {{{offset + 14}}}, {{{offset + 15}}})");

                var (pendingExportId, avc) = chunk[i];
                parameters.Add(avc.Id);
                parameters.Add(pendingExportId);
                parameters.Add(avc.AttributeId);
                parameters.Add(BulkSqlHelpers.NullableParam(avc.StringValue, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.DateTimeValue, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.IntValue, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.LongValue, NpgsqlTypes.NpgsqlDbType.Bigint));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.ByteValue, NpgsqlTypes.NpgsqlDbType.Bytea));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.GuidValue, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.BoolValue, NpgsqlTypes.NpgsqlDbType.Boolean));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.UnresolvedReferenceValue, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add((int)avc.ChangeType);
                parameters.Add((int)avc.Status);
                parameters.Add(avc.ExportAttemptCount);
                parameters.Add(BulkSqlHelpers.NullableParam(avc.LastExportedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(avc.LastImportedValue, NpgsqlTypes.NpgsqlDbType.Text));
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Detaches all tracked entities of type T that match the predicate.
    /// Used before ExecuteDeleteAsync to prevent the change tracker from interfering
    /// with direct SQL operations (e.g., ClientSetNull cascading on orphaned children).
    /// </summary>
    /// <remarks>
    /// Change detection is suppressed while enumerating: mid-sync, tracked entities routinely
    /// hold navigations to untracked instances that duplicate already-tracked keys (cross-page
    /// reference resolution builds such graphs), and ChangeTracker.Entries&lt;T&gt;() otherwise
    /// runs DetectChanges, attaches those graphs, and throws an identity conflict. Detaching
    /// needs only the entries already tracked, so skipping detection is safe.
    /// </remarks>
    private void DetachTrackedEntities<T>(Func<T, bool> predicate) where T : class
    {
        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<T>()
                .Where(e => predicate(e.Entity))
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
    /// matches any of the given parent IDs. Accesses the shadow property via the change tracker entry.
    /// Change detection is suppressed for the same reason as <see cref="DetachTrackedEntities{T}"/>.
    /// </summary>
    private void DetachTrackedChildEntities(List<Guid> pendingExportIds)
    {
        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<PendingExportAttributeValueChange>()
                .Where(e =>
                {
                    var fkValue = e.Property<Guid?>("PendingExportId").CurrentValue;
                    return fkValue.HasValue && pendingExportIds.Contains(fkValue.Value);
                })
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

    public async Task<Dictionary<Guid, ConnectedSystemObjectDisplaySnapshot>> GetConnectedSystemObjectDisplaySnapshotsAsync(IReadOnlyCollection<Guid> csoIds)
    {
        if (csoIds.Count == 0)
            return new Dictionary<Guid, ConnectedSystemObjectDisplaySnapshot>();

        var idList = csoIds as IList<Guid> ?? csoIds.ToList();

        // Correlated single-column scalar subqueries keyed on the CSO's external-ID attribute; a
        // single-column projection emits a clean correlated subquery rather than the whole-table
        // ROW_NUMBER() a multi-column projection would produce, and filtering by ExternalIdAttributeId
        // rides the (ConnectedSystemObjectId, AttributeId) index instead of scanning member values.
        var rows = await _context.ConnectedSystemObjects
            .AsNoTracking()
            .Where(cso => idList.Contains(cso.Id))
            .Select(cso => new
            {
                cso.Id,
                ExtIdString = cso.AttributeValues.Where(av => av.AttributeId == cso.ExternalIdAttributeId).Select(av => av.StringValue).FirstOrDefault(),
                ExtIdDateTime = cso.AttributeValues.Where(av => av.AttributeId == cso.ExternalIdAttributeId).Select(av => av.DateTimeValue).FirstOrDefault(),
                ExtIdInt = cso.AttributeValues.Where(av => av.AttributeId == cso.ExternalIdAttributeId).Select(av => av.IntValue).FirstOrDefault(),
                ExtIdLong = cso.AttributeValues.Where(av => av.AttributeId == cso.ExternalIdAttributeId).Select(av => av.LongValue).FirstOrDefault(),
                ExtIdGuid = cso.AttributeValues.Where(av => av.AttributeId == cso.ExternalIdAttributeId).Select(av => av.GuidValue).FirstOrDefault(),
                ExtIdBool = cso.AttributeValues.Where(av => av.AttributeId == cso.ExternalIdAttributeId).Select(av => av.BoolValue).FirstOrDefault(),
                TypeName = cso.Type!.Name
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.Id, r => new ConnectedSystemObjectDisplaySnapshot
        {
            ConnectedSystemObjectId = r.Id,
            ExternalId = FormatExternalIdSnapshotValue(r.ExtIdString, r.ExtIdDateTime, r.ExtIdInt, r.ExtIdLong, r.ExtIdGuid, r.ExtIdBool),
            TypeName = r.TypeName
        });
    }

    /// <summary>
    /// Formats a Connected System Object external-ID attribute value from its typed columns, mirroring
    /// the priority order in <see cref="ConnectedSystemObjectAttributeValue.ToStringNoName"/>.
    /// </summary>
    private static string? FormatExternalIdSnapshotValue(string? stringValue, DateTime? dateTimeValue, int? intValue, long? longValue, Guid? guidValue, bool? boolValue)
    {
        if (!string.IsNullOrEmpty(stringValue))
            return stringValue;
        if (dateTimeValue != null)
            return dateTimeValue.ToString();
        if (intValue != null)
            return intValue.ToString();
        if (longValue != null)
            return longValue.ToString();
        if (guidValue != null)
            return guidValue.ToString();
        if (boolValue != null)
            return boolValue.ToString();
        return null;
    }

    /// <summary>
    /// The reference recall existence query (#1003). Matches by resolved reference id or by
    /// case-insensitive raw reference string (values pre-lowered by the caller; LOWER() here
    /// mirrors the OrdinalIgnoreCase DN comparison export evaluation uses). Driven by the
    /// composite (ConnectedSystemObjectId, AttributeId) index, so the worst case is a scan of one
    /// group's member rows - milliseconds - rather than materialising them all through EF Core.
    /// Call per target Connected System so identical values cannot cross-match between systems.
    /// </summary>
    public async Task<List<CsoReferenceValueMatch>> GetCsoReferenceValueMatchesAsync(
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        IReadOnlyCollection<int> connectedSystemAttributeIds,
        IReadOnlyCollection<Guid> deletedReferenceCsoIds,
        IReadOnlyCollection<string> loweredReferenceValues)
    {
        if (connectedSystemObjectIds.Count == 0 || connectedSystemAttributeIds.Count == 0 ||
            (deletedReferenceCsoIds.Count == 0 && loweredReferenceValues.Count == 0))
            return new List<CsoReferenceValueMatch>();

        var matches = new List<CsoReferenceValueMatch>();
        var connection = _context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            @"SELECT ""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""ReferenceValueId"", ""UnresolvedReferenceValue""
              FROM ""ConnectedSystemObjectAttributeValues""
              WHERE ""ConnectedSystemObjectId"" = ANY(@csoIds)
                AND ""AttributeId"" = ANY(@attributeIds)
                AND (""ReferenceValueId"" = ANY(@deletedCsoIds)
                     OR LOWER(""UnresolvedReferenceValue"") = ANY(@loweredValues))";
        var csoIdsParameter = command.CreateParameter();
        csoIdsParameter.ParameterName = "csoIds";
        csoIdsParameter.Value = connectedSystemObjectIds.ToArray();
        command.Parameters.Add(csoIdsParameter);
        var attributeIdsParameter = command.CreateParameter();
        attributeIdsParameter.ParameterName = "attributeIds";
        attributeIdsParameter.Value = connectedSystemAttributeIds.ToArray();
        command.Parameters.Add(attributeIdsParameter);
        var deletedCsoIdsParameter = command.CreateParameter();
        deletedCsoIdsParameter.ParameterName = "deletedCsoIds";
        deletedCsoIdsParameter.Value = deletedReferenceCsoIds.ToArray();
        command.Parameters.Add(deletedCsoIdsParameter);
        var loweredValuesParameter = command.CreateParameter();
        loweredValuesParameter.ParameterName = "loweredValues";
        loweredValuesParameter.Value = loweredReferenceValues.ToArray();
        command.Parameters.Add(loweredValuesParameter);

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
    /// Disconnects the given CSOs from their MVOs in one set-based statement: nulls
    /// <c>MetaverseObjectId</c> and <c>DateJoined</c> and resets <c>JoinType</c> to
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
              SET ""MetaverseObjectId"" = NULL, ""JoinType"" = {1}, ""DateJoined"" = NULL
              WHERE ""Id"" = ANY({0})",
            csoIds, (int)ConnectedSystemObjectJoinType.NotJoined);

        var csoIdSet = csoIds.ToHashSet();
        foreach (var trackedCso in _context.ChangeTracker.Entries<ConnectedSystemObject>()
            .Where(e => csoIdSet.Contains(e.Entity.Id)))
        {
            trackedCso.Entity.MetaverseObjectId = null;
            trackedCso.Entity.MetaverseObject = null;
            trackedCso.Entity.JoinType = ConnectedSystemObjectJoinType.NotJoined;
            trackedCso.Entity.DateJoined = null;
        }
    }

    #endregion
}
