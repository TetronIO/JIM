// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
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

    public async Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        if (rpeis.Count == 0)
            return true;

        // Pre-generate IDs for all RPEIs (bypasses EF ValueGeneratedOnAdd)
        foreach (var rpei in rpeis)
        {
            if (rpei.Id == Guid.Empty)
                rpei.Id = Guid.NewGuid();
        }

        // Increase command timeout for large RPEI persistence. With 5K+ RPEIs, the bulk
        // insert of RPEIs, sync outcomes, and CSO change records can exceed the default 30s.
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        var parallelism = ParallelBatchWriter.GetWriteParallelism();
        var connectionString = _connectionStringForParallelWrites;

        // Flatten sync outcomes upfront (before any persistence) so we can count them.
        var allOutcomes = rpeis.SelectMany(r => FlattenSyncOutcomes(r)).ToList();

        // Persist CSO change records linked to sync outcomes (PendingExportCreated snapshots)
        // on the main EF connection — this is a small subset and needs EF AddRange.
        var outcomeCsoChanges = allOutcomes
            .Where(o => o.ConnectedSystemObjectChange != null)
            .Select(o => o.ConnectedSystemObjectChange!)
            .ToList();

        // Stat counter deltas for this batch (#1078): computed once from the in-memory RPEIs and
        // outcomes, upserted alongside the batch so the Activity stats read stays O(counter rows).
        var counterDeltas = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(rpeis, allOutcomes);

        // Use parallel writes for large batches; fall back to single connection for small ones.
        var useParallel = rpeis.Count >= parallelism * 50 && connectionString != null;

        if (useParallel)
        {
            // Step 1: Insert RPEIs in parallel (no FK dependencies on other new rows)
            Log.Information("BulkInsertRpeisAsync: Writing {RpeiCount} RPEIs and {OutcomeCount} outcomes across {Parallelism} parallel connections",
                rpeis.Count, allOutcomes.Count, parallelism);

            await ParallelBatchWriter.ExecuteAsync(
                rpeis,
                parallelism,
                connectionString,
                async (connection, partition) =>
                {
                    await using var tx = await connection!.BeginTransactionAsync();
                    await BulkInsertRpeisOnConnectionAsync(connection, tx, partition.ToList());
                    await tx.CommitAsync();
                });

            // Step 2: Persist CSO change records via raw SQL (same approach as PersistRpeiCsoChangesAsync).
            // EF AddRange + SaveChangesAsync is not safe here: it traverses navigation properties into
            // shared ConnectedSystemObjectTypeAttribute entities causing identity conflicts at scale,
            // and SaveChangesAsync flushes all dirty entities in the tracker (not just CSO changes).
            if (outcomeCsoChanges.Count > 0)
            {
                await InsertCsoChangeHierarchyRawAsync(outcomeCsoChanges);
                foreach (var outcome in allOutcomes.Where(o => o.ConnectedSystemObjectChange != null))
                    outcome.ConnectedSystemObjectChangeId = outcome.ConnectedSystemObjectChange!.Id;
            }

            // Step 3: Insert sync outcomes in parallel (RPEIs and CSO changes now exist).
            // Outcomes have a self-referencing FK (ParentSyncOutcomeId) forming a tree per RPEI.
            // We must partition by RPEI to keep each outcome tree on one connection — otherwise
            // a child on connection 2 may commit before its parent on connection 1, causing
            // an FK violation.
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
                    });
            }

            // Step 4: Upsert the batch's stat counter deltas on the main EF connection. Not
            // transactional with the COPY partitions (which have already committed); a crash
            // between the two leaves advisory drift that completion-time finalisation reconciles.
            await ActivityStatCounterWriter.UpsertDeltasAsync(_context, counterDeltas);
        }
        else
        {
            // Small batch — single-connection path (existing behaviour)
            IDbContextTransaction? transaction = null;
            var existingTransaction = _context.Database.CurrentTransaction;
            if (existingTransaction == null)
                transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                await BulkInsertRpeisRawAsync(rpeis);

                if (outcomeCsoChanges.Count > 0)
                {
                    // Use raw SQL INSERT (see parallel path comment for explanation)
                    await InsertCsoChangeHierarchyRawAsync(outcomeCsoChanges);
                    foreach (var outcome in allOutcomes.Where(o => o.ConnectedSystemObjectChange != null))
                        outcome.ConnectedSystemObjectChangeId = outcome.ConnectedSystemObjectChange!.Id;
                }

                if (allOutcomes.Count > 0)
                    await BulkInsertSyncOutcomesRawAsync(allOutcomes);

                await ActivityStatCounterWriter.UpsertDeltasAsync(_context, counterDeltas);

                if (transaction != null)
                    await transaction.CommitAsync();
            }
            finally
            {
                if (transaction != null)
                    await transaction.DisposeAsync();
            }
        }

        _context.Database.SetCommandTimeout(previousTimeout);
        return true; // Raw SQL used — RPEIs persisted outside EF change tracker
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
    private async Task InsertCsoChangeHierarchyRawAsync(List<ConnectedSystemObjectChange> changes)
    {
        // Pre-generate IDs for all entities in the hierarchy
        foreach (var change in changes)
        {
            if (change.Id == Guid.Empty)
                change.Id = Guid.NewGuid();

            foreach (var attrChange in change.AttributeChanges)
            {
                if (attrChange.Id == Guid.Empty)
                    attrChange.Id = Guid.NewGuid();

                foreach (var valueChange in attrChange.ValueChanges)
                {
                    if (valueChange.Id == Guid.Empty)
                        valueChange.Id = Guid.NewGuid();
                }
            }
        }

        // Step 1: INSERT parent ConnectedSystemObjectChange rows
        await BulkInsertCsoChangesRawAsync(changes);

        // Step 2: INSERT ConnectedSystemObjectChangeAttribute rows
        var allAttrChanges = changes
            .SelectMany(c => c.AttributeChanges.Select(ac =>
                (ChangeId: c.Id, AttributeId: ac.Attribute?.Id ?? 0, AttrChange: ac)))
            .ToList();

        if (allAttrChanges.Count > 0)
            await BulkInsertCsoChangeAttributesRawAsync(allAttrChanges);

        // Step 3: INSERT ConnectedSystemObjectChangeAttributeValue rows
        var allValueChanges = changes
            .SelectMany(c => c.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => (AttrChangeId: ac.Id, Value: vc))))
            .ToList();

        if (allValueChanges.Count > 0)
            await BulkInsertCsoChangeAttributeValuesRawAsync(allValueChanges);
    }

    /// <inheritdoc />
    public async Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        var changes = rpeis
            .Where(r => r.ConnectedSystemObjectChange != null)
            .Select(r => r.ConnectedSystemObjectChange!)
            .ToList();

        if (changes.Count == 0)
            return;

        // Pre-generate IDs for all entities in the graph so FK relationships are known
        // before we build the raw SQL statements.
        foreach (var change in changes)
        {
            if (change.Id == Guid.Empty)
                change.Id = Guid.NewGuid();

            foreach (var attrChange in change.AttributeChanges)
            {
                if (attrChange.Id == Guid.Empty)
                    attrChange.Id = Guid.NewGuid();

                foreach (var valueChange in attrChange.ValueChanges)
                {
                    if (valueChange.Id == Guid.Empty)
                        valueChange.Id = Guid.NewGuid();
                }
            }
        }

        // Increase command timeout for large change record persistence. With 5K+ CSOs and
        // 20+ attributes each, the three-table bulk insert can generate 100K+ rows.
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        // Use an EF transaction for atomicity — COPY binary imports on the underlying
        // NpgsqlConnection participate in the active transaction automatically.
        await using var transaction = await _context.Database.BeginTransactionAsync();

        // Step 1: INSERT parent ConnectedSystemObjectChange rows
        await BulkInsertCsoChangesRawAsync(changes);

        // Step 2: INSERT ConnectedSystemObjectChangeAttribute rows
        var allAttrChanges = changes
            .SelectMany(c => c.AttributeChanges.Select(ac => (ChangeId: c.Id, AttributeId: ac.Attribute!.Id, AttrChange: ac)))
            .ToList();

        if (allAttrChanges.Count > 0)
            await BulkInsertCsoChangeAttributesRawAsync(allAttrChanges);

        // Step 3: INSERT ConnectedSystemObjectChangeAttributeValue rows
        var allValueChanges = changes
            .SelectMany(c => c.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => (AttrChangeId: ac.Id, Value: vc))))
            .ToList();

        if (allValueChanges.Count > 0)
            await BulkInsertCsoChangeAttributeValuesRawAsync(allValueChanges);

        await transaction.CommitAsync();
        _context.Database.SetCommandTimeout(previousTimeout);
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
                   so."SyncRuleId", so."SyncRuleName"
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
                SyncRuleName = reader.IsDBNull(11) ? null : reader.GetString(11)
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

            if (transaction != null)
                await transaction.CommitAsync();
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    #region Private RPEI Bulk Helpers

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChange rows using COPY binary import.
    /// </summary>
    private async Task BulkInsertCsoChangesRawAsync(List<ConnectedSystemObjectChange> changes)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "ConnectedSystemObjectChanges" (
                "Id", "ActivityRunProfileExecutionItemId", "ConnectedSystemId", "ConnectedSystemObjectId",
                "ChangeTime", "ChangeType", "InitiatedByType", "InitiatedById", "InitiatedByName",
                "DeletedObjectTypeId", "DeletedObjectExternalIdAttributeValueId", "DeletedObjectExternalId", "DeletedObjectDisplayName"
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
            if (c.DeletedObjectExternalIdAttributeValue?.Id is { } deletedExtIdAvId)
                await writer.WriteAsync(deletedExtIdAvId, NpgsqlTypes.NpgsqlDbType.Uuid);
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
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Bulk inserts ConnectedSystemObjectChangeAttribute rows using COPY binary import.
    /// </summary>
    private async Task BulkInsertCsoChangeAttributesRawAsync(List<(Guid ChangeId, int AttributeId, ConnectedSystemObjectChangeAttribute AttrChange)> attrChanges)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "ConnectedSystemObjectChangeAttributes" ("Id", "ConnectedSystemChangeId", "AttributeId", "AttributeName", "AttributeType")
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
    /// Bulk inserts ConnectedSystemObjectChangeAttributeValue rows using COPY binary import.
    /// </summary>
    private async Task BulkInsertCsoChangeAttributeValuesRawAsync(List<(Guid AttrChangeId, ConnectedSystemObjectChangeAttributeValue Value)> valueChanges)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "ConnectedSystemObjectChangeAttributeValues" (
                "Id", "ConnectedSystemObjectChangeAttributeId", "ValueChangeType",
                "StringValue", "DateTimeValue", "IntValue", "LongValue",
                "ByteValueLength", "GuidValue", "BoolValue", "ReferenceValueId",
                "IsPendingExportStub"
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var (attrChangeId, v) in valueChanges)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(v.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(attrChangeId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync((int)v.ValueChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (v.StringValue is not null)
                await writer.WriteAsync(v.StringValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (v.DateTimeValue.HasValue)
                await writer.WriteAsync(v.DateTimeValue.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
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
            // Write the ReferenceValueId FK if the referenced CSO has a known ID.
            // With the two-phase parallel writer (Part C), CSO rows from the current batch are
            // committed before change records are persisted, so the FK constraint will succeed
            // for within-batch references. Cross-batch references (future batches) still have
            // ReferenceValue == null and are written as null — FixupCrossBatchChangeRecordReferenceIdsAsync
            // resolves these after all batches complete.
            var refId = v.ReferenceValue?.Id;
            if (refId.HasValue && refId.Value != Guid.Empty)
                await writer.WriteAsync(refId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(v.IsPendingExportStub, NpgsqlTypes.NpgsqlDbType.Boolean);
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
                        "AttributeFlowCount" int
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
            await using (var writer = await npgsqlConn.BeginBinaryImportAsync(
                @"COPY _rpei_bulk_update (""Id"", ""OutcomeSummary"", ""ErrorType"", ""ErrorMessage"", ""AttributeFlowCount"") FROM STDIN (FORMAT binary)"))
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
                    if (rpei.AttributeFlowCount.HasValue)
                        await writer.WriteAsync(rpei.AttributeFlowCount.Value, NpgsqlTypes.NpgsqlDbType.Integer);
                    else
                        await writer.WriteNullAsync();
                }
                await writer.CompleteAsync();
            }

            // Single UPDATE join — PostgreSQL uses the primary key index on ActivityRunProfileExecutionItems
            await using (var updateCmd = new NpgsqlCommand { Connection = npgsqlConn, Transaction = npgsqlTx })
            {
                updateCmd.CommandText = """
                    UPDATE "ActivityRunProfileExecutionItems" t
                    SET "OutcomeSummary" = v."OutcomeSummary",
                        "ErrorType" = v."ErrorType",
                        "ErrorMessage" = v."ErrorMessage",
                        "AttributeFlowCount" = v."AttributeFlowCount"
                    FROM _rpei_bulk_update v
                    WHERE t."Id" = v."Id"
                    """;
                await updateCmd.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItem rows using parameterised multi-row INSERT.
    /// Chunks automatically to stay within the PostgreSQL parameter limit.
    /// </summary>
    private async Task BulkInsertRpeisRawAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        const int columnsPerRow = 14;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(rpeis, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            sql.Append(@"INSERT INTO ""ActivityRunProfileExecutionItems"" (""Id"", ""ActivityId"", ""ObjectChangeType"", ""NoChangeReason"", ""ConnectedSystemObjectId"", ""ExternalIdSnapshot"", ""DisplayNameSnapshot"", ""ObjectTypeSnapshot"", ""ErrorType"", ""ErrorMessage"", ""ErrorStackTrace"", ""AttributeFlowCount"", ""OutcomeSummary"", ""PendingExportId"") VALUES ");

            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"(@p{offset}, @p{offset + 1}, @p{offset + 2}, @p{offset + 3}, @p{offset + 4}, @p{offset + 5}, @p{offset + 6}, @p{offset + 7}, @p{offset + 8}, @p{offset + 9}, @p{offset + 10}, @p{offset + 11}, @p{offset + 12}, @p{offset + 13})");

                var rpei = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rpei.Id });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rpei.ActivityId });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)rpei.ObjectChangeType });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = rpei.NoChangeReason.HasValue ? (object)(int)rpei.NoChangeReason.Value : DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 4}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)rpei.ConnectedSystemObjectId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 5}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ExternalIdSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 6}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.DisplayNameSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 7}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ObjectTypeSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 8}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = rpei.ErrorType.HasValue ? (object)(int)rpei.ErrorType.Value : DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 9}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ErrorMessage ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 10}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ErrorStackTrace ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 11}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)rpei.AttributeFlowCount ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 12}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.OutcomeSummary ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 13}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)rpei.PendingExportId ?? DBNull.Value });
            }

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItemSyncOutcome rows using parameterised multi-row INSERT.
    /// Chunks automatically to stay within the PostgreSQL parameter limit.
    /// </summary>
    private async Task BulkInsertSyncOutcomesRawAsync(List<ActivityRunProfileExecutionItemSyncOutcome> outcomes)
    {
        const int columnsPerRow = 12;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(outcomes, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            sql.Append(@"INSERT INTO ""ActivityRunProfileExecutionItemSyncOutcomes"" (""Id"", ""ActivityRunProfileExecutionItemId"", ""ParentSyncOutcomeId"", ""OutcomeType"", ""TargetEntityId"", ""TargetEntityDescription"", ""DetailCount"", ""DetailMessage"", ""Ordinal"", ""ConnectedSystemObjectChangeId"", ""SyncRuleId"", ""SyncRuleName"") VALUES ");

            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"(@p{offset}, @p{offset + 1}, @p{offset + 2}, @p{offset + 3}, @p{offset + 4}, @p{offset + 5}, @p{offset + 6}, @p{offset + 7}, @p{offset + 8}, @p{offset + 9}, @p{offset + 10}, @p{offset + 11})");

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
            """
            COPY "ActivityRunProfileExecutionItems" (
                "Id", "ActivityId", "ObjectChangeType", "NoChangeReason",
                "ConnectedSystemObjectId", "ExternalIdSnapshot", "DisplayNameSnapshot",
                "ObjectTypeSnapshot", "ErrorType", "ErrorMessage", "ErrorStackTrace",
                "AttributeFlowCount", "OutcomeSummary", "PendingExportId"
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
            if (rpei.ConnectedSystemObjectId.HasValue)
                await writer.WriteAsync(rpei.ConnectedSystemObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
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
            """
            COPY "ActivityRunProfileExecutionItemSyncOutcomes" (
                "Id", "ActivityRunProfileExecutionItemId", "ParentSyncOutcomeId",
                "OutcomeType", "TargetEntityId", "TargetEntityDescription",
                "DetailCount", "DetailMessage", "Ordinal", "ConnectedSystemObjectChangeId",
                "SyncRuleId", "SyncRuleName"
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
                outcome.Id = Guid.NewGuid();

            outcome.ActivityRunProfileExecutionItemId = rpeiId;
            outcome.ParentSyncOutcomeId = parentId;
            result.Add(outcome);

            if (outcome.Children.Count > 0)
                FlattenOutcomesRecursive(outcome.Children, rpeiId, outcome.Id, result);
        }
    }

    #endregion
}
