// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Text;
using JIM.Models.Core;
using JIM.Models.Transactional;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Metaverse Object — Parallel Bulk Create

    /// <summary>
    /// Spans for the sync write path's Metaverse Object stages, picked up by the Worker's diagnostic listener, which
    /// listens to every source named "JIM.*" (#2492).
    /// </summary>
    private static readonly ActivitySource MvoActivitySource = new("JIM.PostgresData.Sync");

    /// <summary>
    /// Bulk creates MVOs with their attribute values using parallel multi-connection writes.
    /// Each partition of MVOs is written on its own <see cref="NpgsqlConnection"/>, allowing
    /// PostgreSQL to utilise multiple CPU cores during the INSERT phase.
    /// <para>
    /// Mirrors the proven pattern from <see cref="CreateConnectedSystemObjectsAsync"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The objects are left untracked (#2598). Everything the synchronisation page does with them afterwards is
    /// raw SQL (attribute value updates, change records, Pending Exports, join states, deletion), and the page runs
    /// with change detection off, so no EF save discovers them through a tracked object's navigation. Attaching them
    /// instead cost roughly the square of a group's membership in EF's change tracker: 211 s for one page at
    /// Scale750k40kGroups. Code that does save through EF on this context must not walk a graph that can reach a
    /// new object (<c>DbSet.Update</c>, <c>Remove</c> on a detached root), or it inserts or updates it a second time.
    /// </remarks>
    public async Task CreateMetaverseObjectsBulkAsync(List<MetaverseObject> metaverseObjects)
    {
        using var span = MvoActivitySource.StartActivity("Mvo.Create");
        span?.SetTag("attributeValueCount", metaverseObjects.Sum(mvo => mvo.AttributeValues.Count));

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

        using (MvoActivitySource.StartActivity("Mvo.Create.BulkWrite"))
        {
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
        }
    }

    /// <summary>
    /// Below-threshold fallback for <see cref="CreateMetaverseObjectsBulkAsync"/>: writes MVO rows
    /// then their attribute values via COPY binary import on the EF connection's own transaction,
    /// reusing the same on-connection writers the parallel branch above uses
    /// (<see cref="BulkInsertMvosOnConnectionAsync"/>, <see cref="BulkInsertMvoAttributeValuesOnConnectionAsync"/>)
    /// rather than opening N independent connections. COPY binary is markedly faster than
    /// parameterised multi-row INSERT even on a single connection. No partition-based reference
    /// filtering is needed (unlike the CSO single-connection path, MVO attribute values carry no such
    /// filter at all): this whole batch is one transaction on one connection, and MVO rows are
    /// COPY'd, so visible to this session, before attribute values are COPY'd, so a same-batch
    /// forward reference resolves without cross-partition isolation.
    /// </summary>
    private async Task CreateMvosOnSingleConnectionAsync(List<MetaverseObject> metaverseObjects)
    {
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        await using var transaction = await _context.Database.BeginTransactionAsync();

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction)_context.Database.CurrentTransaction!.GetDbTransaction();

        await BulkInsertMvosOnConnectionAsync(npgsqlConn, npgsqlTx, metaverseObjects);

        var allAttributeValues = metaverseObjects
            .SelectMany(mvo => mvo.AttributeValues.Select(av => (MvoId: mvo.Id, Value: av)))
            .ToList();

        if (allAttributeValues.Count > 0)
            await BulkInsertMvoAttributeValuesOnConnectionAsync(npgsqlConn, npgsqlTx, allAttributeValues);

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
            $"""COPY "MetaverseObjects" ({BulkSqlHelpers.ToQuotedList(MvoBulkInsertColumns.MetaverseObjects)}) FROM STDIN (FORMAT binary)""");

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
            if (mvo.DeletionTriggeredBySystemId.HasValue)
                await writer.WriteAsync(mvo.DeletionTriggeredBySystemId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (mvo.DeletionTriggeredBySystemName is not null)
                await writer.WriteAsync(mvo.DeletionTriggeredBySystemName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (mvo.DeletionPolicySnapshotJson is not null)
                await writer.WriteAsync(mvo.DeletionPolicySnapshotJson, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (mvo.DeletionScheduledByRunProfileExecutionItemId.HasValue)
                await writer.WriteAsync(mvo.DeletionScheduledByRunProfileExecutionItemId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
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
            if (mvo.UnsynchronisedChangesJson is not null)
                await writer.WriteAsync(mvo.UnsynchronisedChangesJson, NpgsqlTypes.NpgsqlDbType.Jsonb);
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
            $"""COPY "MetaverseObjectAttributeValues" ({BulkSqlHelpers.ToQuotedList(MvoBulkInsertColumns.MetaverseObjectAttributeValues)}) FROM STDIN (FORMAT binary)""");

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
            if (av.ContributedByKind.HasValue)
                await writer.WriteAsync((int)av.ContributedByKind.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (av.ContributedByClaimType is not null)
                await writer.WriteAsync(av.ContributedByClaimType, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Inserts MVO attribute value rows using parameterised multi-row INSERT on the main EF
    /// connection. Used solely by <see cref="UpdateMetaverseObjectsBulkAsync"/>'s reconciliation
    /// insert (a small, variable-shaped set of newly added values diffed against what already
    /// exists); the create path (<see cref="CreateMvosOnSingleConnectionAsync"/>) uses the COPY
    /// binary writer <see cref="BulkInsertMvoAttributeValuesOnConnectionAsync"/> instead.
    /// </summary>
    private async Task BulkInsertMvoAttributeValuesViaEfAsync(
        List<(Guid MvoId, MetaverseObjectAttributeValue Value)> attributeValues)
    {
        var columnsPerRow = MvoBulkInsertColumns.MetaverseObjectAttributeValues.Length;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in BulkSqlHelpers.ChunkList(attributeValues, chunkSize))
        {
            var sql = new StringBuilder();
            sql.Append($@"INSERT INTO ""MetaverseObjectAttributeValues"" ({BulkSqlHelpers.ToQuotedList(MvoBulkInsertColumns.MetaverseObjectAttributeValues)}) VALUES ");

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
                parameters.Add(BulkSqlHelpers.NullableParam(BulkSqlHelpers.CanonicalDate(av.DateTimeValue), NpgsqlTypes.NpgsqlDbType.TimestampTz));
                parameters.Add(BulkSqlHelpers.NullableParam(av.IntValue, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(av.LongValue, NpgsqlTypes.NpgsqlDbType.Bigint));
                parameters.Add(BulkSqlHelpers.NullableParam(av.DecimalValue, NpgsqlTypes.NpgsqlDbType.Numeric));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ByteValue, NpgsqlTypes.NpgsqlDbType.Bytea));
                parameters.Add(BulkSqlHelpers.NullableParam(av.GuidValue, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.BoolValue, NpgsqlTypes.NpgsqlDbType.Boolean));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ReferenceValueId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.UnresolvedReferenceValueId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ContributedBySystemId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ContributedBySyncRuleId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(av.NullValue);
                parameters.Add(BulkSqlHelpers.NullableParam(av.ContributedByKind.HasValue ? (int?)av.ContributedByKind.Value : null, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(av.ContributedByClaimType, NpgsqlTypes.NpgsqlDbType.Text));
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
    /// Scenario-014-AttributePriority failure). Keying updates by <c>Id</c> and dropping the xmin predicate removes that
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

        // Defensive collapse (belt and braces): SyncTaskProcessorBase's page-wide Metaverse Object identity
        // map (MetaverseObjectPageIdentityMap, #1612) is the PRIMARY mechanism preventing a same-page load
        // from ever producing two distinct instances of the same Id in the first place; QueueMvoForUpdate's
        // own by-Id dedupe is a tripwire for a load site that bypasses the map. This collapse is the last
        // line of defence before the batch UPDATE/INSERT below, which cannot tolerate a duplicate Id at all
        // - a duplicate-keyed VALUES row makes the UPDATE nondeterministic (PostgreSQL picks an arbitrary
        // row) and a duplicate attribute value Id in the INSERT fails outright on the unique constraint,
        // aborting the whole Activity. Collapsing here means a regression at every earlier layer degrades to
        // a logged consolidation instead of a failed Activity.
        metaverseObjects = CollapseDuplicateMvoEntries(metaverseObjects);

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

            // 2. Reconcile attribute values: insert the added values, delete the removed ones, and write the
            //    provenance of any surviving value whose contributing rule or system changed (#1292).
            var existingAvsByMvo = await LoadExistingAttributeValuesAsync(mvoIds);
            var attributeValuesToInsert = new List<(Guid MvoId, MetaverseObjectAttributeValue Value)>();
            var attributeValueIdsToDelete = new List<Guid>();
            var provenanceUpdates = new List<(Guid Id, int? SystemId, int? SyncRuleId, ValueContributorKind? Kind, string? ClaimType)>();

            foreach (var mvo in metaverseObjects)
            {
                var existing = existingAvsByMvo.GetValueOrDefault(mvo.Id) ?? new Dictionary<Guid, (int?, int?, ValueContributorKind?, string?)>();
                var currentIds = new HashSet<Guid>();

                foreach (var av in mvo.AttributeValues)
                {
                    currentIds.Add(av.Id);
                    if (!existing.TryGetValue(av.Id, out var storedProvenance))
                        attributeValuesToInsert.Add((mvo.Id, av));
                    else if (storedProvenance.SystemId != av.ContributedBySystemId ||
                             storedProvenance.SyncRuleId != av.ContributedBySyncRuleId ||
                             storedProvenance.Kind != av.ContributedByKind ||
                             !string.Equals(storedProvenance.ClaimType, av.ContributedByClaimType, StringComparison.Ordinal))
                        provenanceUpdates.Add((av.Id, av.ContributedBySystemId, av.ContributedBySyncRuleId, av.ContributedByKind, av.ContributedByClaimType));
                }

                foreach (var existingId in existing.Keys.Where(existingId => !currentIds.Contains(existingId)))
                    attributeValueIdsToDelete.Add(existingId);
            }

            if (attributeValuesToInsert.Count > 0)
            {
                // Final guard: dedupe by value Id. The Id-collapse above removes whole duplicate MVO
                // entries, but if two entries somehow still contribute the same newly-created attribute
                // value Id (the collision this method exists to survive, not merely tolerate), the INSERT
                // below is a raw SQL statement with no ON CONFLICT clause and would fail outright on the
                // unique constraint. Deduping here is strictly defensive; queue-time consolidation is the
                // fix, this is the belt-and-braces backstop.
                var dedupedInserts = DeduplicateByValueId(attributeValuesToInsert);
                if (dedupedInserts.Count != attributeValuesToInsert.Count)
                    Log.Warning(
                        "UpdateMetaverseObjectsBulkAsync: Collapsed {DuplicateCount} duplicate attribute value id(s) " +
                        "from the insert batch (an upstream page-flush queueing defect); investigate SyncTaskProcessorBase's " +
                        "QueueMvoForUpdate dedup guard.",
                        attributeValuesToInsert.Count - dedupedInserts.Count);

                await BulkInsertMvoAttributeValuesViaEfAsync(dedupedInserts);
            }

            if (attributeValueIdsToDelete.Count > 0)
                await DeleteMetaverseObjectAttributeValuesByIdsAsync(attributeValueIdsToDelete);

            if (provenanceUpdates.Count > 0)
                await BulkUpdateMvoAttributeValueProvenanceViaEfAsync(provenanceUpdates);

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
        //    here as raw SQL (a duplicate-key violation). Detaching is the definitive guard against recurrence: a detached
        //    entity cannot be re-persisted by EF at all, which is also why the create path leaves its objects untracked.
        DetachPersistedMvoGraphs(metaverseObjects);
    }

    /// <summary>
    /// Collapses entries that share an Id but are distinct object instances (see the SamePageJoinConflict
    /// note on <c>SyncTaskProcessorBase.QueueMvoForUpdate</c>, JIM.Worker, and the page identity map,
    /// <c>MetaverseObjectPageIdentityMap</c>, #1612, that is now the primary mechanism preventing the
    /// split), keeping the later entry and copying any deletion-marker scalars the earlier entry carried
    /// across before it is dropped. Ordinary callers reach this method with an already-deduped list, so the
    /// common case is a same-size list returned untouched; this only does work when every earlier layer -
    /// the identity map and the page-flush queueing guard alike - has regressed.
    /// </summary>
    private static List<MetaverseObject> CollapseDuplicateMvoEntries(List<MetaverseObject> metaverseObjects)
    {
        if (metaverseObjects.Count < 2)
            return metaverseObjects;

        var byId = new Dictionary<Guid, MetaverseObject>(metaverseObjects.Count);
        var order = new List<Guid>(metaverseObjects.Count);
        var duplicateCount = 0;

        foreach (var mvo in metaverseObjects)
        {
            if (!byId.TryGetValue(mvo.Id, out var existing))
            {
                byId[mvo.Id] = mvo;
                order.Add(mvo.Id);
                continue;
            }

            if (ReferenceEquals(existing, mvo))
                continue;

            duplicateCount++;

            // Preserve deletion-marker scalars from whichever entry carries them: only one of the
            // colliding instances is expected to have gone through the grace-period deletion-marking
            // path, and the survivor must not silently lose that state.
            if (existing.LastConnectorDisconnectedDate.HasValue && !mvo.LastConnectorDisconnectedDate.HasValue)
            {
                mvo.LastConnectorDisconnectedDate = existing.LastConnectorDisconnectedDate;
                mvo.DeletionInitiatedByType = existing.DeletionInitiatedByType;
                mvo.DeletionInitiatedById = existing.DeletionInitiatedById;
                mvo.DeletionInitiatedByName = existing.DeletionInitiatedByName;
                mvo.DeletionTriggeredBySystemId = existing.DeletionTriggeredBySystemId;
                mvo.DeletionTriggeredBySystemName = existing.DeletionTriggeredBySystemName;
                mvo.DeletionPolicySnapshotJson = existing.DeletionPolicySnapshotJson;
                mvo.DeletionScheduledByRunProfileExecutionItemId = existing.DeletionScheduledByRunProfileExecutionItemId;
            }

            byId[mvo.Id] = mvo;
        }

        if (duplicateCount > 0)
            Log.Warning(
                "UpdateMetaverseObjectsBulkAsync: Collapsed {DuplicateCount} duplicate Metaverse Object id(s) from the " +
                "batch (an upstream page-flush queueing defect - distinct object instances queued for the same Id); " +
                "investigate SyncTaskProcessorBase's QueueMvoForUpdate dedup guard.",
                duplicateCount);

        return order.Select(id => byId[id]).ToList();
    }

    /// <summary>
    /// Dedupes newly-created attribute value inserts by value Id, keeping the first occurrence. Belt-and-braces
    /// backstop for <see cref="UpdateMetaverseObjectsBulkAsync"/>'s raw SQL INSERT, which has no ON CONFLICT
    /// clause and fails outright on a duplicate Id.
    /// </summary>
    private static List<(Guid MvoId, MetaverseObjectAttributeValue Value)> DeduplicateByValueId(
        List<(Guid MvoId, MetaverseObjectAttributeValue Value)> values)
    {
        var seen = new HashSet<Guid>(values.Count);
        var result = new List<(Guid MvoId, MetaverseObjectAttributeValue Value)>(values.Count);
        foreach (var entry in values)
        {
            if (seen.Add(entry.Value.Id))
                result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Issues the batched, VALUES-list UPDATE of the MetaverseObjects rows on the EF connection (so it joins the
    /// ambient transaction). Only the columns in <see cref="MvoBulkInsertColumns.MetaverseObjectsUpdate"/> are
    /// written, which leaves out the scope review columns other writers own (see its documentation); there is no
    /// xmin predicate, so the update is keyed solely on the immutable Id.
    /// </summary>
    private async Task BulkUpdateMvoRowsViaEfAsync(List<MetaverseObject> objects)
    {
        // Id plus the thirteen updatable columns.
        const int columnsPerRow = 14;
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
                    "DeletionTriggeredBySystemId" = v."DeletionTriggeredBySystemId",
                    "DeletionTriggeredBySystemName" = v."DeletionTriggeredBySystemName",
                    "DeletionPolicySnapshotJson" = v."DeletionPolicySnapshotJson",
                    "DeletionScheduledByRunProfileExecutionItemId" = v."DeletionScheduledByRunProfileExecutionItemId",
                    "CachedDisplayName" = v."CachedDisplayName"
                FROM (VALUES
                """);

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(',');
                var o = i * columnsPerRow;
                // Explicit casts give the VALUES columns a definite type even when a whole chunk is null for a column.
                sql.Append($"({{{o}}}::uuid,{{{o + 1}}}::timestamptz,{{{o + 2}}}::int,{{{o + 3}}}::int,{{{o + 4}}}::int,{{{o + 5}}}::timestamptz,{{{o + 6}}}::int,{{{o + 7}}}::uuid,{{{o + 8}}}::text,{{{o + 9}}}::int,{{{o + 10}}}::text,{{{o + 11}}}::text,{{{o + 12}}}::uuid,{{{o + 13}}}::text)");

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
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionTriggeredBySystemId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionTriggeredBySystemName, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionPolicySnapshotJson, NpgsqlTypes.NpgsqlDbType.Text));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.DeletionScheduledByRunProfileExecutionItemId, NpgsqlTypes.NpgsqlDbType.Uuid));
                parameters.Add(BulkSqlHelpers.NullableParam(mvo.CachedDisplayName, NpgsqlTypes.NpgsqlDbType.Text));
            }

            sql.Append("""
                ) AS v("Id","LastUpdated","TypeId","Status","Origin","LastConnectorDisconnectedDate","DeletionInitiatedByType","DeletionInitiatedById","DeletionInitiatedByName","DeletionTriggeredBySystemId","DeletionTriggeredBySystemName","DeletionPolicySnapshotJson","DeletionScheduledByRunProfileExecutionItemId","CachedDisplayName")
                WHERE m."Id" = v."Id"
                """);

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Loads the current database attribute-value ids for the given Metaverse Objects, grouped by Metaverse Object id,
    /// each carrying its stored provenance. Used to compute the insert/delete delta for the update path, and the
    /// provenance delta beside it (#1292): a surviving value's provenance (contributing rule and system, and source kind
    /// and claim type, #2472) is the only
    /// thing the synchronisation engine can change on a row it does not replace, so it is all that is diffed here. Runs on
    /// the EF connection and ambient transaction.
    /// </summary>
    private async Task<Dictionary<Guid, Dictionary<Guid, (int? SystemId, int? SyncRuleId, ValueContributorKind? Kind, string? ClaimType)>>> LoadExistingAttributeValuesAsync(List<Guid> mvoIds)
    {
        var result = new Dictionary<Guid, Dictionary<Guid, (int? SystemId, int? SyncRuleId, ValueContributorKind? Kind, string? ClaimType)>>();
        if (mvoIds.Count == 0)
            return result;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(
            @"SELECT ""Id"", ""MetaverseObjectId"", ""ContributedBySystemId"", ""ContributedBySyncRuleId"",
                     ""ContributedByKind"", ""ContributedByClaimType""
              FROM ""MetaverseObjectAttributeValues"" WHERE ""MetaverseObjectId"" = ANY(@ids)",
            connection, transaction);
        command.Parameters.AddWithValue("ids", mvoIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var attributeValueId = reader.GetGuid(0);
            var metaverseObjectId = reader.GetGuid(1);
            if (!result.TryGetValue(metaverseObjectId, out var values))
            {
                values = [];
                result[metaverseObjectId] = values;
            }

            values[attributeValueId] = (
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : (ValueContributorKind)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        return result;
    }

    /// <summary>
    /// Writes the taken-over provenance of surviving Metaverse Object attribute values (#1292) as a single batched,
    /// VALUES-list UPDATE. A deliberately narrow provenance fix-up (the contributing system and rule, and the source kind
    /// and claim type beside them, #2472) on rows that already exist, in the shape of
    /// <see cref="FixupMvoReferenceValueIdsAsync"/>: the value columns of a surviving row never change (the attribute
    /// flow writers replace a row rather than edit it), so this is not the entity's write path and takes no bulk
    /// column list. Runs on the EF connection so it joins the ambient transaction.
    /// </summary>
    private async Task BulkUpdateMvoAttributeValueProvenanceViaEfAsync(List<(Guid Id, int? SystemId, int? SyncRuleId, ValueContributorKind? Kind, string? ClaimType)> provenanceUpdates)
    {
        const int columnsPerRow = 5;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;

        // Reuse one StringBuilder across chunks (Clear() each iteration) rather than allocating per chunk.
        var sql = new StringBuilder();
        foreach (var chunk in BulkSqlHelpers.ChunkList(provenanceUpdates, chunkSize))
        {
            sql.Clear();
            sql.Append("""
                UPDATE "MetaverseObjectAttributeValues" AS mav SET
                    "ContributedBySystemId" = v."ContributedBySystemId",
                    "ContributedBySyncRuleId" = v."ContributedBySyncRuleId",
                    "ContributedByKind" = v."ContributedByKind",
                    "ContributedByClaimType" = v."ContributedByClaimType"
                FROM (VALUES
                """);

            var parameters = new List<object>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(',');
                var o = i * columnsPerRow;
                // Explicit casts give the VALUES columns a definite type even when a whole chunk is null for a column.
                sql.Append($"({{{o}}}::uuid,{{{o + 1}}}::int,{{{o + 2}}}::int,{{{o + 3}}}::int,{{{o + 4}}}::text)");

                parameters.Add(chunk[i].Id);
                parameters.Add(BulkSqlHelpers.NullableParam(chunk[i].SystemId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(chunk[i].SyncRuleId, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(chunk[i].Kind.HasValue ? (int?)chunk[i].Kind!.Value : null, NpgsqlTypes.NpgsqlDbType.Integer));
                parameters.Add(BulkSqlHelpers.NullableParam(chunk[i].ClaimType, NpgsqlTypes.NpgsqlDbType.Text));
            }

            sql.Append("""
                ) AS v("Id","ContributedBySystemId","ContributedBySyncRuleId","ContributedByKind","ContributedByClaimType")
                WHERE mav."Id" = v."Id"
                """);

            await _context.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }

        Log.Debug("BulkUpdateMvoAttributeValueProvenanceViaEfAsync: Took over provenance on {Count} Metaverse Object attribute values",
            provenanceUpdates.Count);
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
    /// by one raw <c>DELETE</c>. Semantics per object are identical to the singular method.
    /// </summary>
    /// <remarks>
    /// The rows are deleted by raw SQL, not EF's <c>RemoveRange</c>, because a deletion candidate arrives detached
    /// (the bulk update that wrote its marker detaches what it writes) and <c>RemoveRange</c> on a detached object
    /// attaches its whole graph: through its reference values that reaches objects created on the same page, which
    /// the bulk create leaves untracked (#2598), and the save then wrote their references to the deleted object, rows
    /// the cleanup above had already removed, and threw DbUpdateConcurrencyException. Every foreign key into the
    /// table either cascades or sets null in the database, or is cleared by a statement above, so the database does
    /// what EF's cascade did; <see cref="ReflectDeletedMetaverseObjectsInTracker"/> then does the same to anything the
    /// context still tracks.
    /// </remarks>
    public async Task<IReadOnlyList<GeneratedValueRetirement>> DeleteMetaverseObjectsAsync(IReadOnlyCollection<MetaverseObject> metaverseObjects)
    {
        if (metaverseObjects.Count == 0)
            return [];

        var mvoIds = metaverseObjects.Select(mvo => mvo.Id).ToArray();

        // One transaction for the whole deletion (Unique Value Generation, #242, Phase 6): the retirement below must
        // land with the deletion or not at all. A caller already inside a transaction keeps it; this joins it.
        await using var ownTransaction = _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync()
            : null;

        // Retire the objects' generated values BEFORE the rows go: the assignments are removed by the deletion's
        // foreign-key cascade, so once the objects are deleted there is nothing left to read them from.
        var retirements = await RetiredGeneratedValueSql.RetireForMetaverseObjectsAsync(_context, mvoIds);

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
        // SaveChangesAsync does not write the stale FK value back. The join record (#348) goes with the join;
        // JoinType and DateJoined are left as they always have been here.
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""MetaverseObjectId"" = NULL, ""JoinMethod"" = NULL, ""JoinSyncRuleId"" = NULL, ""JoinSyncRuleName"" = NULL, ""JoinObjectMatchingRuleId"" = NULL, ""JoinMatchValue"" = NULL WHERE ""MetaverseObjectId"" = ANY({0})",
            mvoIds);
        var mvoIdSet = mvoIds.ToHashSet();
        foreach (var trackedCso in _context.ChangeTracker.Entries<Models.Staging.ConnectedSystemObject>()
            .Where(e => e.Entity.MetaverseObjectId.HasValue && mvoIdSet.Contains(e.Entity.MetaverseObjectId.Value)))
        {
            trackedCso.Entity.MetaverseObjectId = null;
            trackedCso.Entity.MetaverseObject = null;
            trackedCso.Entity.JoinMethod = null;
            trackedCso.Entity.JoinSyncRuleId = null;
            trackedCso.Entity.JoinSyncRuleName = null;
            trackedCso.Entity.JoinObjectMatchingRuleId = null;
            trackedCso.Entity.JoinMatchValue = null;
        }

        // Reference attribute values on other MVOs that point at the deleted MVOs (e.g. Manager or
        // member references): valueless rows are deleted, payload-carrying rows are nulled, and
        // tracked instances are surgically detached (#1019). Reference values in change tracking
        // records are nulled (audit rows carry their own payload and must be preserved).
        await MetaverseReferenceRowCleanup.CleanUpReferencesToDeletedMvosAsync(_context, mvoIds);
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectChangeAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = ANY({0})",
            mvoIds);

        await _context.Database.ExecuteSqlRawAsync(@"DELETE FROM ""MetaverseObjects"" WHERE ""Id"" = ANY({0})", mvoIds);
        ReflectDeletedMetaverseObjectsInTracker(mvoIdSet);

        if (ownTransaction != null)
            await ownTransaction.CommitAsync();

        Log.Information("DeleteMetaverseObjectsAsync: Deleted {Count} Metaverse Objects in bulk, retiring {RetiredCount} generated value(s)",
            metaverseObjects.Count, retirements.Count);
        return retirements;
    }

    /// <summary>
    /// Brings the change tracker into line with a raw delete of Metaverse Objects: the deleted objects are detached,
    /// a tracked entity whose foreign key to one of them cascades in the database is detached too (its row is gone),
    /// and one whose foreign key sets null has the null recorded as already persisted, so no later EF save writes the
    /// stale value back or acts on a row that no longer exists. Driven by the model's foreign keys, so a child table
    /// added later is covered without being named here. Change detection is suppressed while enumerating, for the
    /// reason given on <see cref="DetachTrackedEntities{T}"/>.
    /// </summary>
    private void ReflectDeletedMetaverseObjectsInTracker(HashSet<Guid> deletedIds)
    {
        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries().ToList();

            foreach (var entry in entries.Where(e => e.Entity is MetaverseObject mvo && deletedIds.Contains(mvo.Id)))
                entry.State = EntityState.Detached;

            foreach (var (entry, references) in entries
                .Where(e => e.Entity is not MetaverseObject)
                .Select(e => (Entry: e, References: ReferencesToDeletedMetaverseObjects(e, deletedIds)))
                .Where(x => x.References.Count > 0))
            {
                if (references.Any(r => r.ForeignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade))
                {
                    entry.State = EntityState.Detached;
                }
                else
                {
                    foreach (var (foreignKey, keyProperty) in references)
                    {
                        keyProperty.CurrentValue = null;
                        keyProperty.OriginalValue = null;
                        keyProperty.IsModified = false;
                        foreignKey.DependentToPrincipal?.PropertyInfo?.SetValue(entry.Entity, null);
                    }
                }
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    /// <summary>
    /// The single-column foreign keys from <paramref name="entry"/> to Metaverse Objects whose current value is one of
    /// <paramref name="deletedIds"/>.
    /// </summary>
    private static List<(IForeignKey ForeignKey, PropertyEntry KeyProperty)> ReferencesToDeletedMetaverseObjects(
        EntityEntry entry, HashSet<Guid> deletedIds)
        => entry.Metadata.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(MetaverseObject) && fk.Properties.Count == 1)
            .Select(fk => (ForeignKey: fk, KeyProperty: entry.Property(fk.Properties[0].Name)))
            .Where(r => r.KeyProperty.CurrentValue is Guid id && deletedIds.Contains(id))
            .ToList();

    #endregion
}
