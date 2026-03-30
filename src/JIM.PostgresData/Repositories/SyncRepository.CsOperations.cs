using System.Text;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
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
    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
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
        var batchCsoIds = new HashSet<Guid>(connectedSystemObjects.Select(c => c.Id));
        foreach (var cso in connectedSystemObjects)
        {
            foreach (var av in cso.AttributeValues)
            {
                if (av.ReferenceValue != null && av.ReferenceValue.Id != Guid.Empty && !av.ReferenceValueId.HasValue
                    && batchCsoIds.Contains(av.ReferenceValue.Id))
                    av.ReferenceValueId = av.ReferenceValue.Id;

                // Null out ReferenceValueId for cross-batch references. ResolveReferencesAsync may
                // have set this to a pre-generated ID for a CSO in a future batch. Writing this FK
                // would cause an FK constraint violation. FixupCrossBatchReferenceIdsAsync resolves
                // these after all batches are persisted.
                if (av.ReferenceValueId.HasValue && av.ReferenceValueId.Value != Guid.Empty
                    && !batchCsoIds.Contains(av.ReferenceValueId.Value))
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

        await ParallelBatchWriter.ExecuteAsync(
            connectedSystemObjects,
            parallelism,
            connectionString,
            async (connection, partition) =>
            {
                await using var transaction = await connection!.BeginTransactionAsync();

                await BulkInsertCsosOnConnectionAsync(connection, transaction, partition);

                var attributeValues = partition
                    .SelectMany(cso => cso.AttributeValues.Select(av => (CsoId: cso.Id, Value: av)))
                    .ToList();

                if (attributeValues.Count > 0)
                {
                    var partitionCsoIds = new HashSet<Guid>(partition.Select(cso => cso.Id));
                    await BulkInsertCsoAttributeValuesOnConnectionAsync(connection, transaction, attributeValues, partitionCsoIds);
                }

                await transaction.CommitAsync();
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

    public async Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId)
    {
        // Change record attribute values (ConnectedSystemObjectChangeAttributeValues) store reference
        // DN strings in StringValue but have ReferenceValueId nulled during COPY binary persistence
        // to avoid FK violations when the referenced CSO is in a later batch. After all batches
        // complete, this method resolves those references by matching StringValue against the
        // secondary external ID attribute values of CSOs in the same connected system.
        //
        // Unlike the CSO attribute value fixup, there is no dedicated "UnresolvedReferenceValue"
        // column on change records — the DN is stored in StringValue alongside regular string values.
        // The UPDATE is safe because it only matches when StringValue equals a secondary external ID
        // value (case-insensitive), so non-reference string values are naturally excluded by the JOIN.
        //
        // Uses case-insensitive LOWER() comparison because LDAP Distinguished Names are
        // case-insensitive per RFC 4514.
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);
        try
        {
            return await _context.Database.ExecuteSqlRawAsync(
                """
                UPDATE "ConnectedSystemObjectChangeAttributeValues" cav
                SET "ReferenceValueId" = target_cso."Id"
                FROM "ConnectedSystemObjectChangeAttributes" ca
                JOIN "ConnectedSystemObjectChanges" cc ON cc."Id" = ca."ConnectedSystemChangeId"
                JOIN "ConnectedSystemObjects" target_cso ON target_cso."ConnectedSystemId" = {0}
                JOIN "ConnectedSystemObjectAttributeValues" target_av ON target_av."ConnectedSystemObjectId" = target_cso."Id"
                JOIN "ConnectedSystemAttributes" target_attr ON target_attr."Id" = target_av."AttributeId"
                    AND target_attr."IsSecondaryExternalId" = true
                WHERE cc."ConnectedSystemId" = {0}
                  AND cav."ConnectedSystemObjectChangeAttributeId" = ca."Id"
                  AND cav."StringValue" IS NOT NULL
                  AND cav."ReferenceValueId" IS NULL
                  AND target_av."StringValue" IS NOT NULL
                  AND LOWER(cav."StringValue") = LOWER(target_av."StringValue")
                """,
                connectedSystemId);
        }
        finally
        {
            _context.Database.SetCommandTimeout(previousTimeout);
        }
    }

    #endregion

    #region Pending Export — Worker-Only Bulk Operations

    public async Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        var pendingExportsList = pendingExports.ToList();
        if (pendingExportsList.Count == 0)
            return;

        // Pre-generate IDs for all pending exports and their attribute value changes
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

        foreach (var untrackedExport in exportList)
        {
            // Find the tracked instance if one exists, otherwise attach the untracked entity
            var trackedEntry = _context.ChangeTracker.Entries<PendingExport>()
                .FirstOrDefault(e => e.Entity.Id == untrackedExport.Id);

            if (trackedEntry != null)
            {
                // Copy scalar properties from the untracked entity to the tracked one
                trackedEntry.Entity.Status = untrackedExport.Status;
                trackedEntry.Entity.ChangeType = untrackedExport.ChangeType;
                trackedEntry.Entity.ErrorCount = untrackedExport.ErrorCount;
                trackedEntry.Entity.MaxRetries = untrackedExport.MaxRetries;
                trackedEntry.Entity.LastAttemptedAt = untrackedExport.LastAttemptedAt;
                trackedEntry.Entity.NextRetryAt = untrackedExport.NextRetryAt;
                trackedEntry.Entity.LastErrorMessage = untrackedExport.LastErrorMessage;
                trackedEntry.Entity.LastErrorStackTrace = untrackedExport.LastErrorStackTrace;
                trackedEntry.Entity.HasUnresolvedReferences = untrackedExport.HasUnresolvedReferences;
                trackedEntry.State = EntityState.Modified;
            }
            else
            {
                // Use detach-safe update to avoid graph traversal through
                // AttributeValueChanges → Attribute → ConnectedSystemObjectTypeAttribute.
                _repo.UpdateDetachedSafe(untrackedExport);
            }

            // Update attribute value changes that remain (those not deleted)
            foreach (var attrChange in untrackedExport.AttributeValueChanges)
            {
                var trackedAttrEntry = _context.ChangeTracker.Entries<PendingExportAttributeValueChange>()
                    .FirstOrDefault(e => e.Entity.Id == attrChange.Id);

                if (trackedAttrEntry != null)
                {
                    trackedAttrEntry.Entity.Status = attrChange.Status;
                    trackedAttrEntry.Entity.LastImportedValue = attrChange.LastImportedValue;
                    trackedAttrEntry.Entity.ExportAttemptCount = attrChange.ExportAttemptCount;
                    trackedAttrEntry.Entity.LastExportedAt = attrChange.LastExportedAt;
                    trackedAttrEntry.State = EntityState.Modified;
                }
                else
                {
                    // Ensure the parent FK is set before attaching.
                    attrChange.PendingExportId = untrackedExport.Id;
                    _repo.UpdateDetachedSafe(attrChange);
                }
            }
        }

        await _context.SaveChangesAsync();
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
    private void DetachTrackedEntities<T>(Func<T, bool> predicate) where T : class
    {
        var entries = _context.ChangeTracker.Entries<T>()
            .Where(e => predicate(e.Entity))
            .ToList();

        foreach (var entry in entries)
            entry.State = EntityState.Detached;
    }

    /// <summary>
    /// Detaches tracked PendingExportAttributeValueChange entities whose PendingExportId shadow FK
    /// matches any of the given parent IDs. Accesses the shadow property via the change tracker entry.
    /// </summary>
    private void DetachTrackedChildEntities(List<Guid> pendingExportIds)
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

    #endregion
}
