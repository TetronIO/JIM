using System.Text;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
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

            await ParallelBatchWriter.ExecuteAsync(
                metaverseObjects,
                parallelism,
                connectionString,
                async (connection, partition) =>
                {
                    await using var transaction = await connection!.BeginTransactionAsync();

                    await BulkInsertMvosOnConnectionAsync(connection, transaction, partition);

                    var attributeValues = partition
                        .SelectMany(mvo => mvo.AttributeValues.Select(av => (MvoId: mvo.Id, Value: av)))
                        .ToList();

                    if (attributeValues.Count > 0)
                        await BulkInsertMvoAttributeValuesOnConnectionAsync(connection, transaction, attributeValues);

                    await transaction.CommitAsync();
                });
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
        // (it's inferred by EF from the Type navigation). The xmin concurrency token is excluded
        // from tracking by using Entry().Property — PostgreSQL assigned a real xmin during COPY,
        // but we don't know its value. Setting OriginalValue to the current DB value would require
        // a round-trip. Instead, we rely on the fact that MVOs created in this batch are not
        // updated again in the same page flush (updates go to _pendingMvoUpdates, which is a
        // separate collection). If a future code path does update a just-created MVO in the same
        // flush, the xmin mismatch will surface as a DbUpdateConcurrencyException — a clear signal
        // to convert that path to raw SQL as well.
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
            """
            COPY "MetaverseObjects" (
                "Id", "Created", "LastUpdated", "TypeId", "Status", "Origin",
                "LastConnectorDisconnectedDate", "DeletionInitiatedByType",
                "DeletionInitiatedById", "DeletionInitiatedByName"
            ) FROM STDIN (FORMAT binary)
            """);

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
            """
            COPY "MetaverseObjectAttributeValues" (
                "Id", "MetaverseObjectId", "AttributeId", "StringValue",
                "DateTimeValue", "IntValue", "LongValue", "ByteValue",
                "GuidValue", "BoolValue", "ReferenceValueId",
                "UnresolvedReferenceValueId", "ContributedBySystemId"
            ) FROM STDIN (FORMAT binary)
            """);

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
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts MVO rows using the main EF connection (single-connection fallback for small batches).
    /// </summary>
    private async Task BulkInsertMvosViaEfAsync(List<MetaverseObject> objects)
    {
        const int columnsPerRow = 10;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(objects, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append(@"INSERT INTO ""MetaverseObjects"" (""Id"", ""Created"", ""LastUpdated"", ""TypeId"", ""Status"", ""Origin"", ""LastConnectorDisconnectedDate"", ""DeletionInitiatedByType"", ""DeletionInitiatedById"", ""DeletionInitiatedByName"") VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"({{{offset}}}, {{{offset + 1}}}, {{{offset + 2}}}, {{{offset + 3}}}, {{{offset + 4}}}, {{{offset + 5}}}, {{{offset + 6}}}, {{{offset + 7}}}, {{{offset + 8}}}, {{{offset + 9}}})");

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
        const int columnsPerRow = 13;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(attributeValues, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append(@"INSERT INTO ""MetaverseObjectAttributeValues"" (""Id"", ""MetaverseObjectId"", ""AttributeId"", ""StringValue"", ""DateTimeValue"", ""IntValue"", ""LongValue"", ""ByteValue"", ""GuidValue"", ""BoolValue"", ""ReferenceValueId"", ""UnresolvedReferenceValueId"", ""ContributedBySystemId"") VALUES ");

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"({{{offset}}}, {{{offset + 1}}}, {{{offset + 2}}}, {{{offset + 3}}}, {{{offset + 4}}}, {{{offset + 5}}}, {{{offset + 6}}}, {{{offset + 7}}}, {{{offset + 8}}}, {{{offset + 9}}}, {{{offset + 10}}}, {{{offset + 11}}}, {{{offset + 12}}})");

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
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
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
}
