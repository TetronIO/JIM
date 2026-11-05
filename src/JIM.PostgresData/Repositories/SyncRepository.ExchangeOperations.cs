// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Exchange;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Exchange queue (#2021)

    /// <summary>
    /// The five values that make up a change's declared state, compared to decide whether a re-stage leaves the row as
    /// it is, whatever its status. The SQL twin of <see cref="PendingExchangeChange.HasSameDeclaredStateAs"/>.
    /// </summary>
    private static readonly string[] ExchangeDeclaredStateColumns =
    [
        "RecipientType", "Alias", "Database", "PrimarySmtpAddress", "ExternalAddress"
    ];

    /// <summary>
    /// What a supersede sets each attempt and claim column to, rather than the offered row's value: the
    /// values of a change staged afresh, so a deliverable row is due at once whatever the caller passed. Every other
    /// column in <see cref="PendingExchangeChangeBulkColumns.PendingExchangeChangesSupersedeUpdate"/> takes the offered
    /// value, the status and failure fields included: Pending with no failure for a deliverable change, Refused and its
    /// reason for a refusal.
    /// </summary>
    private static readonly Dictionary<string, string> ExchangeSupersedeResetValues = new()
    {
        ["AttemptCount"] = "0",
        ["LastAttemptedAt"] = "NULL",
        ["FirstFailedAt"] = "NULL",
        ["NextRetryAt"] = "NULL",
        ["ClaimedAt"] = "NULL",
        ["ClaimedBy"] = "NULL"
    };

    /// <inheritdoc />
    public async Task<List<ExchangeChangeStagingOutcome>> StageExchangeChangesAsync(IReadOnlyCollection<PendingExchangeChange> changes)
    {
        var staging = changes.ToList();
        var outcomes = new List<ExchangeChangeStagingOutcome>(staging.Count);
        if (staging.Count == 0)
            return outcomes;

        // Refused before anything is written: the queue coalesces on the account, and a change for no account is one
        // nothing could deliver. The unique index is filtered on a non-null account, so such a row would also escape
        // the one-change-per-account guarantee.
        var unaddressed = staging.FirstOrDefault(c => c.ConnectedSystemObjectId == null);
        if (unaddressed != null)
            throw new ArgumentException($"Exchange change {unaddressed.Id} names no Connected System Object; staging is per account.", nameof(changes));

        foreach (var change in staging.Where(c => c.Id == Guid.Empty))
            change.Id = Guid.NewGuid();

        var sql = BuildStageExchangeChangeSql();

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var change in staging)
        {
            var requestedId = change.Id;

            await using var command = new NpgsqlCommand(sql, npgsqlConn, npgsqlTx);
            AddPendingExchangeChangeParameters(command, change);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                // The statement either writes (and returns the row it wrote) or declines because the account's row
                // already declares this state (and returns that row). Neither coming back means another writer's row
                // appeared between the statement's snapshot and its conflict check; fail loudly rather than report the
                // change as handled when nothing is known about it.
                throw new InvalidOperationException(
                    $"Staging the Exchange change for Connected System Object {change.ConnectedSystemObjectId} returned no row.");
            }

            var rowId = reader.GetGuid(0);
            var unchanged = reader.GetBoolean(1);
            var inserted = !unchanged && reader.IsDBNull(2);

            // The row now in the table keeps its own id; the caller must hold that rather than the id it offered, or a
            // later write against this change would target a row that does not exist.
            change.Id = rowId;

            outcomes.Add(new ExchangeChangeStagingOutcome
            {
                RequestedId = requestedId,
                RowId = rowId,
                Disposition = unchanged
                    ? ExchangeChangeStagingDisposition.Unchanged
                    : inserted
                        ? ExchangeChangeStagingDisposition.Inserted
                        : ExchangeChangeStagingDisposition.Superseded,
                PreviousActivityId = unchanged || inserted ? null : reader.GetGuid(2),
                ExistingStatus = unchanged ? (PendingExchangeChangeStatus)reader.GetInt32(3) : null
            });
        }

        return outcomes;
    }

    /// <inheritdoc />
    public async Task<HashSet<Guid>> GetConnectedSystemObjectIdsWithExchangeChangeAsync(int connectedSystemId, IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return [];

        // Raw SQL, asked once per reconciliation page holding a change awaiting Exchange. Served by the unique index on
        // the account, so it reads at most one row per object asked. Every status counts: the row is the hold. A
        // read-side projection, so exempt from the bulk column list rule.
        const string sql = """
            SELECT pec."ConnectedSystemObjectId"
            FROM "PendingExchangeChanges" pec
            WHERE pec."ConnectedSystemObjectId" = ANY(@connectedSystemObjectIds)
              AND pec."ConnectedSystemId" = @connectedSystemId
            """;

        return await ReadGuidSetAsync(sql,
            new NpgsqlParameter("connectedSystemObjectIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = connectedSystemObjectIds.ToArray() },
            new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId });
    }

    /// <inheritdoc />
    public async Task<List<PendingExchangeChange>> GetExchangeChangesByActivityAsync(IReadOnlyCollection<Guid> activityIds)
    {
        if (activityIds.Count == 0)
            return [];

        // A portal and API read, a page of follow-ups at a time; served by IX_PendingExchangeChanges_ActivityId.
        return await _context.PendingExchangeChanges
            .AsNoTracking()
            .Where(c => activityIds.Contains(c.ActivityId))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<PendingExchangeChange?> GetExchangeChangeForConnectedSystemObjectAsync(Guid connectedSystemObjectId)
    {
        // Served by the unique index on the account, which is what makes "at most one" a guarantee rather than a hope.
        return await _context.PendingExchangeChanges
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.ConnectedSystemObjectId == connectedSystemObjectId);
    }

    /// <inheritdoc />
    public async Task<HashSet<Guid>> GetConnectedSystemObjectIdsAwaitingExchangeAsync(int connectedSystemId, IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return [];

        // Raw SQL, asked once per synchronisation page with drift to evaluate. Led by the partial index on the flag
        // (IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsExchange), so the probe is near-free where no
        // change awaits Exchange; each hit joins to its Pending Export by primary key. A read-side projection, so exempt
        // from the bulk column list rule.
        const string sql = """
            SELECT DISTINCT pe."ConnectedSystemObjectId"
            FROM "PendingExportAttributeValueChanges" avc
            JOIN "PendingExports" pe ON pe."Id" = avc."PendingExportId"
            WHERE avc."AwaitsExchange"
              AND avc."Status" = @awaitingConfirmation
              AND pe."ConnectedSystemId" = @connectedSystemId
              AND pe."ConnectedSystemObjectId" = ANY(@connectedSystemObjectIds)
            """;

        return await ReadGuidSetAsync(sql,
            new NpgsqlParameter("awaitingConfirmation", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation },
            new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId },
            new NpgsqlParameter("connectedSystemObjectIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = connectedSystemObjectIds.ToArray() });
    }

    /// <inheritdoc />
    public async Task<HashSet<Guid>> GetConnectedSystemObjectIdsWithReleasedExchangeHoldsAsync(int connectedSystemId)
    {
        // Raw SQL, asked once per import after reconciliation. Led by the partial index on the flag
        // (IX_PendingExportAttributeValueChanges_PendingExportId_AwaitsExchange), which holds only the changes awaiting
        // Exchange, so where none exists the probe reads an empty index; each hit joins to its Pending Export by primary
        // key and probes the queue by the unique index on its account. A read-side projection, so exempt from the bulk
        // column list rule.
        const string sql = """
            SELECT DISTINCT pe."ConnectedSystemObjectId"
            FROM "PendingExportAttributeValueChanges" avc
            JOIN "PendingExports" pe ON pe."Id" = avc."PendingExportId"
            WHERE avc."AwaitsExchange"
              AND avc."Status" = @awaitingConfirmation
              AND pe."ConnectedSystemId" = @connectedSystemId
              AND pe."ConnectedSystemObjectId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "PendingExchangeChanges" pec
                  WHERE pec."ConnectedSystemObjectId" = pe."ConnectedSystemObjectId")
            """;

        return await ReadGuidSetAsync(sql,
            new NpgsqlParameter("awaitingConfirmation", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)PendingExportAttributeChangeStatus.ExportedPendingConfirmation },
            new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId });
    }

    // ---- Delivery (Phase 5) ----------------------------------------------------------------------------------------
    // Modelled on the Password Synchronisation queue's delivery statements (SyncRepository.PasswordOperations.cs), with
    // the Exchange queue's own status ordinals. Every write a lane makes is guarded on the row still being Delivering
    // under that lane's claim: a supersession clears the claim, and another deliverer reclaiming an expired lease
    // re-stamps it, and each of those is an outcome that must survive.

    /// <inheritdoc />
    public async Task<List<int>> GetConnectedSystemIdsWithDueExchangeChangesAsync(DateTime asOf, TimeSpan claimLease)
    {
        // A system not managing Exchange recipients now is held: counting its changes as due would have the service see
        // permanent work and run a lane on every poll, as the password queue's own hold explains.
        var claimExpiredBefore = asOf - claimLease;
        return await ManagedExchangeChanges()
            .Where(c => c.Status == PendingExchangeChangeStatus.Pending && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                        || c.Status == PendingExchangeChangeStatus.Delivering && c.ClaimedAt != null && c.ClaimedAt <= claimExpiredBefore)
            .Select(c => c.ConnectedSystemId)
            .Distinct()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<DeliveryQueueOutlook> GetExchangeQueueDeliveryOutlookAsync(DateTime asOf, TimeSpan claimLease)
    {
        // One grouped round trip, read on every iteration of the delivery loop.
        var claimExpiredBefore = asOf - claimLease;
        var outlook = await ManagedExchangeChanges()
            .Where(c => c.Status == PendingExchangeChangeStatus.Pending || c.Status == PendingExchangeChangeStatus.Delivering)
            .GroupBy(_ => 1)
            .Select(g => new DeliveryQueueOutlook
            {
                DueCount = g.Count(c => c.Status == PendingExchangeChangeStatus.Pending && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                                        || c.Status == PendingExchangeChangeStatus.Delivering && c.ClaimedAt != null && c.ClaimedAt <= claimExpiredBefore),
                RetryingCount = g.Count(c => c.Status == PendingExchangeChangeStatus.Pending && c.NextRetryAt != null && c.NextRetryAt > asOf),
                NextAttemptAt = g.Where(c => c.Status == PendingExchangeChangeStatus.Pending && c.NextRetryAt != null && c.NextRetryAt > asOf)
                    .Min(c => c.NextRetryAt)
            })
            .SingleOrDefaultAsync();

        // An empty queue groups to nothing rather than to a row of zeroes, which is the answer the caller wants.
        return outlook ?? new DeliveryQueueOutlook();
    }

    /// <inheritdoc />
    public async Task<List<PendingExchangeChange>> ClaimDueExchangeChangesAsync(int connectedSystemId, string claimedBy, DateTime asOf, TimeSpan lease, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);
        if (maximum < 1)
            return [];

        // Select and update in one statement, which is what makes the claim safe against a second deliverer: the
        // sub-select locks the rows it chooses, SKIP LOCKED makes a concurrent claimer step over them rather than wait
        // and then re-read them, and the update lands before the lock is released at commit. Parked and Refused
        // rows are never chosen.
        //
        // Deliberately hand-written rather than driven from the bulk-columns constant: it marks exactly three
        // call-site-computed columns (a status mark and the claim stamp), and a future column must not be swept into it.
        // RETURNING c.* hands the rows back in the entity's own shape, so the caller holds exactly what it claimed.
        var claimExpiredBefore = asOf - lease;
        const string sql = """
            WITH due AS (
                SELECT "Id"
                FROM "PendingExchangeChanges"
                WHERE "ConnectedSystemId" = {0}
                  AND EXISTS (SELECT 1 FROM "ConnectedSystems" s WHERE s."Id" = {0} AND s."ManagesExchangeRecipients")
                  AND (("Status" = {1} AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= {2}))
                    OR ("Status" = {3} AND "ClaimedAt" IS NOT NULL AND "ClaimedAt" <= {4}))
                ORDER BY "CreatedAt", "Id"
                LIMIT {5}
                FOR UPDATE SKIP LOCKED
            )
            UPDATE "PendingExchangeChanges" AS c
            SET "Status" = {3}, "ClaimedAt" = {2}, "ClaimedBy" = {6}
            FROM due
            WHERE c."Id" = due."Id"
            RETURNING c.*
            """;

        return await _context.PendingExchangeChanges
            .FromSqlRaw(sql,
                connectedSystemId,
                (int)PendingExchangeChangeStatus.Pending,
                new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = asOf },
                (int)PendingExchangeChangeStatus.Delivering,
                new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = claimExpiredBefore },
                maximum,
                claimedBy)
            .AsNoTracking()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> ReleaseExchangeChangeClaimsAsync(IEnumerable<Guid> ids, string claimedBy)
    {
        var releasing = ids.ToList();
        if (releasing.Count == 0)
            return 0;

        return await _context.PendingExchangeChanges
            .Where(c => releasing.Contains(c.Id) && c.Status == PendingExchangeChangeStatus.Delivering && c.ClaimedBy == claimedBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, PendingExchangeChangeStatus.Pending)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null)
                .SetProperty(c => c.ClaimedBy, (string?)null));
    }

    /// <inheritdoc />
    public async Task RecordExchangeChangeAttemptsAsync(IEnumerable<PendingExchangeChange> changes, string claimedBy)
    {
        var recording = changes.ToList();
        if (recording.Count == 0)
            return;

        // Assignment list from the constant, so a migration adding a column an attempt can change fails the completeness
        // test rather than silently going unwritten. The guard's parameters follow the assignments.
        var columns = PendingExchangeChangeBulkColumns.PendingExchangeChangesAttemptUpdate;
        var assignments = string.Join(", ", columns.Select((c, i) => $"\"{c}\" = {{{i}}}"));
        var idPlaceholder = $"{{{columns.Length}}}";
        var deliveringPlaceholder = $"{{{columns.Length + 1}}}";
        var claimedByPlaceholder = $"{{{columns.Length + 2}}}";
        var sql = $"""UPDATE "PendingExchangeChanges" SET {assignments} WHERE "Id" = {idPlaceholder} AND "Status" = {deliveringPlaceholder} AND "ClaimedBy" = {claimedByPlaceholder}""";

        foreach (var change in recording)
        {
            // Values in exactly the order of PendingExchangeChangesAttemptUpdate.
            await _context.Database.ExecuteSqlRawAsync(sql,
                (int)change.Status,
                BulkSqlHelpers.NullableParam((int?)change.FailureKind, NpgsqlTypes.NpgsqlDbType.Integer),
                BulkSqlHelpers.NullableParam(change.FailureMessage, NpgsqlTypes.NpgsqlDbType.Text),
                change.AttemptCount,
                BulkSqlHelpers.NullableParam(change.LastAttemptedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.FirstFailedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.NextRetryAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.ClaimedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.ClaimedBy, NpgsqlTypes.NpgsqlDbType.Text),
                change.Id,
                (int)PendingExchangeChangeStatus.Delivering,
                claimedBy);
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteExchangeChangesAsync(IEnumerable<Guid> ids, string claimedBy)
    {
        var deleting = ids.ToList();
        if (deleting.Count == 0)
            return 0;

        return await _context.PendingExchangeChanges
            .Where(c => deleting.Contains(c.Id) && c.Status == PendingExchangeChangeStatus.Delivering && c.ClaimedBy == claimedBy)
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<int> DeleteTerminalExchangeChangesAsync(DateTime olderThan, int maxRecords)
    {
        // Batched through a sub-select so one pass cannot become a long transaction; oldest first, so a repeated pass
        // drains rather than churning the same rows.
        return await _context.PendingExchangeChanges
            .Where(c => _context.PendingExchangeChanges
                .Where(t => t.Status == PendingExchangeChangeStatus.Parked && (t.LastAttemptedAt ?? t.CreatedAt) < olderThan)
                .OrderBy(t => t.LastAttemptedAt ?? t.CreatedAt)
                .Take(maxRecords)
                .Select(t => t.Id)
                .Contains(c.Id))
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<List<ExchangeDeliveryObject>> GetExchangeDeliveryObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return [];

        var ids = connectedSystemObjectIds.ToList();

        // Read once per claimed batch (at most a hundred objects), off the synchronisation path, so EF projections.
        var objects = await _context.ConnectedSystemObjects.AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => new { o.Id, ObjectType = o.Type.Name, o.ExternalIdAttributeId, o.SecondaryExternalIdAttributeId })
            .ToListAsync();
        if (objects.Count == 0)
            return [];

        var identifierAttributeIds = objects.Select(o => o.ExternalIdAttributeId)
            .Concat(objects.Where(o => o.SecondaryExternalIdAttributeId.HasValue).Select(o => o.SecondaryExternalIdAttributeId!.Value))
            .Distinct()
            .ToList();
        var exchangeAttributeNames = ExchangeRecipientAttributes.All.ToList();

        var values = await _context.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .Where(v => ids.Contains(v.ConnectedSystemObject.Id)
                        && (identifierAttributeIds.Contains(v.AttributeId)
                            || v.Attribute.IsConnectorDefined && exchangeAttributeNames.Contains(v.Attribute.Name)))
            .Select(v => new { ObjectId = v.ConnectedSystemObject.Id, v.AttributeId, AttributeName = v.Attribute.Name, v.Attribute.IsConnectorDefined, v.StringValue, v.GuidValue })
            .ToListAsync();

        var held = await (
                from c in _context.PendingExportAttributeValueChanges.AsNoTracking()
                join pe in _context.PendingExports on c.PendingExportId equals (Guid?)pe.Id
                where c.AwaitsExchange
                      && c.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation
                      && pe.ConnectedSystemObjectId != null
                      && ids.Contains(pe.ConnectedSystemObjectId.Value)
                select new { ObjectId = pe.ConnectedSystemObjectId!.Value, AttributeName = c.Attribute.Name, c.ChangeType, c.StringValue })
            .ToListAsync();

        var valuesByObject = values.ToLookup(v => v.ObjectId);
        var heldByObject = held.ToLookup(h => h.ObjectId);
        return objects.Select(o =>
        {
            var own = valuesByObject[o.Id].ToList();
            var anchor = own.FirstOrDefault(v => v.AttributeId == o.ExternalIdAttributeId && !v.IsConnectorDefined);
            var secondary = o.SecondaryExternalIdAttributeId is { } secondaryId
                ? own.FirstOrDefault(v => v.AttributeId == secondaryId && !v.IsConnectorDefined)
                : null;
            return new ExchangeDeliveryObject
            {
                ConnectedSystemObjectId = o.Id,
                ObjectType = o.ObjectType,
                ObjectGuid = anchor?.GuidValue,
                DistinguishedName = secondary?.StringValue ?? anchor?.StringValue,
                ImportedValues = own.Where(v => v.IsConnectorDefined && exchangeAttributeNames.Contains(v.AttributeName))
                    .GroupBy(v => v.AttributeName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().StringValue, StringComparer.Ordinal),
                HeldChanges = heldByObject[o.Id].Select(h => new ExchangeHeldChange(h.AttributeName, h.ChangeType, h.StringValue)).ToList()
            };
        }).ToList();
    }

    /// <summary>
    /// One statement per change, deciding against the row the queue already holds for the account, if any (#2021):
    /// insert where there is none; leave a row that is the same change (the same declared state, refused or not alike)
    /// untouched, whatever its status; supersede any other row in place, taking the offered status and failure fields.
    /// <para>
    /// One statement rather than a read followed by a write, so no other writer (a parallel export batch staging the
    /// same account, or the delivery lane recording an attempt) can act between the decision and the write. The unique
    /// index on the account makes the insert-or-supersede atomic; the conflict clause's WHERE declines the update for a
    /// row already declaring this state, and the outer SELECT then reads that row's id and status from the statement's
    /// own snapshot, so the unchanged row is reported without being written (no new row version, no wake-up of the
    /// delivery lane, and a Parked or retrying row keeps its attempts and backoff).
    /// </para>
    /// <para>
    /// RETURNING reads the superseded row as it stood (PostgreSQL 18's OLD), so the caller can record the supersession
    /// under the Activity the row carried. Run through raw Npgsql rather than EF's Database.SqlQueryRaw: EF composes a
    /// SqlQuery as a subquery, and PostgreSQL refuses a data-modifying statement inside one. Column lists come from the
    /// constants so they cannot drift from the model; the parameters are added in
    /// PendingExchangeChangeBulkColumns.PendingExchangeChanges order by AddPendingExchangeChangeParameters.
    /// </para>
    /// </summary>
    private static string BuildStageExchangeChangeSql()
    {
        var update = PendingExchangeChangeBulkColumns.PendingExchangeChangesSupersedeUpdate;
        var values = string.Join(",\n            ", update.Select(c => ExchangeSupersedeResetValues.GetValueOrDefault(c, $"EXCLUDED.\"{c}\"")));
        var sameDeclaredState = string.Join("\n              AND ", ExchangeDeclaredStateColumns
            .Select(c => $"COALESCE(existing.\"{c}\", '') = COALESCE(EXCLUDED.\"{c}\", '')"));
        // A refusal is part of what the change is (PendingExchangeChange.IsSameChangeAs): refused before and not now,
        // or the reverse, the same declared state is a different change.
        var refused = (int)PendingExchangeChangeStatus.Refused;
        var sameRefusal = $"(existing.\"Status\" = {refused}) = (EXCLUDED.\"Status\" = {refused})";
        var accountParameter = $"@p{Array.IndexOf(PendingExchangeChangeBulkColumns.PendingExchangeChanges, "ConnectedSystemObjectId")}";

        return $"""
            WITH staged AS (
                INSERT INTO "PendingExchangeChanges" AS existing ({BulkSqlHelpers.ToQuotedList(PendingExchangeChangeBulkColumns.PendingExchangeChanges)})
                VALUES ({PendingExchangeChangeValuePlaceholders()})
                ON CONFLICT ("ConnectedSystemObjectId") WHERE "ConnectedSystemObjectId" IS NOT NULL DO UPDATE SET ({BulkSqlHelpers.ToQuotedList(update)}) = (
                    {values})
                WHERE NOT ({sameDeclaredState}
              AND {sameRefusal})
                RETURNING new."Id", old."ActivityId"
            )
            SELECT staged."Id", FALSE, staged."ActivityId", NULL::integer FROM staged
            UNION ALL
            SELECT unchanged."Id", TRUE, NULL, unchanged."Status" FROM "PendingExchangeChanges" AS unchanged
            WHERE unchanged."ConnectedSystemObjectId" = {accountParameter} AND NOT EXISTS (SELECT 1 FROM staged)
            """;
    }

    /// <summary>The VALUES placeholders for a whole row, @p0 onwards in column-list order.</summary>
    private static string PendingExchangeChangeValuePlaceholders() =>
        string.Join(", ", Enumerable.Range(0, PendingExchangeChangeBulkColumns.PendingExchangeChanges.Length).Select(i => $"@p{i}"));

    /// <summary>
    /// Adds a whole row's values as @p0 onwards. The order below MUST match
    /// PendingExchangeChangeBulkColumns.PendingExchangeChanges exactly; every raw writer of a whole row goes through
    /// here, so there is one place to extend when the table gains a column.
    /// </summary>
    private static void AddPendingExchangeChangeParameters(NpgsqlCommand command, PendingExchangeChange change)
    {
        command.Parameters.Add(new NpgsqlParameter("p0", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = change.Id });
        command.Parameters.Add(new NpgsqlParameter("p1", NpgsqlTypes.NpgsqlDbType.Integer) { Value = change.ConnectedSystemId });
        command.Parameters.Add(new NpgsqlParameter("p2", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)change.ConnectedSystemObjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p3", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)change.MetaverseObjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p4", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)change.SyncRuleId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p5", NpgsqlTypes.NpgsqlDbType.Text) { Value = change.RecipientType });
        command.Parameters.Add(new NpgsqlParameter("p6", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.Alias ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p7", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.Database ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p8", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.PrimarySmtpAddress ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p9", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.ExternalAddress ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p10", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.DomainController ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p11", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)change.Status });
        command.Parameters.Add(new NpgsqlParameter("p12", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)(int?)change.FailureKind ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p13", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.FailureMessage ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p14", NpgsqlTypes.NpgsqlDbType.Integer) { Value = change.AttemptCount });
        // CreatedAt is inferred, as the password queue's writer infers it: the column type follows the Npgsql timestamp
        // mode the repository runs under.
        command.Parameters.Add(new NpgsqlParameter("p15", change.CreatedAt));
        command.Parameters.Add(new NpgsqlParameter("p16", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.LastAttemptedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p17", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.FirstFailedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p18", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.NextRetryAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p19", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.ClaimedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p20", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.ClaimedBy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p21", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = change.ActivityId });
    }

    #endregion

    #region The queue as administrators see and act on it (#2021, Phase 6b)

    /// <inheritdoc />
    public async Task<RangeResultSet<PendingExchangeChangeHeader>> GetPendingExchangeChangeHeadersAsync(
        PendingExchangeChangeFilter filter,
        int startIndex,
        int count,
        string sortBy,
        bool sortDescending,
        bool includeTotalCount)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = ApplyExchangeChangeFilter(ExchangeChangeHeaders(), filter);

        // Counted before windowing, and only when asked: a scroll re-reads windows far more often than the filters change.
        int? total = includeTotalCount ? await query.CountAsync() : null;

        query = sortBy?.ToLowerInvariant() switch
        {
            "identity" => sortDescending ? query.OrderByDescending(h => h.MetaverseObjectDisplayName) : query.OrderBy(h => h.MetaverseObjectDisplayName),
            "system" => sortDescending ? query.OrderByDescending(h => h.ConnectedSystemName) : query.OrderBy(h => h.ConnectedSystemName),
            "attempts" => sortDescending ? query.OrderByDescending(h => h.AttemptCount) : query.OrderBy(h => h.AttemptCount),
            "nextattempt" => sortDescending ? query.OrderByDescending(h => h.NextRetryAt) : query.OrderBy(h => h.NextRetryAt),
            "lastattempt" => sortDescending ? query.OrderByDescending(h => h.LastAttemptedAt) : query.OrderBy(h => h.LastAttemptedAt),
            // Queued time is the default and the fallback: the one column every row has a value for.
            _ => sortDescending ? query.OrderByDescending(h => h.CreatedAt) : query.OrderBy(h => h.CreatedAt)
        };

        // Id breaks ties, so paging over changes queued in the same instant cannot repeat or skip one.
        var ordered = ((IOrderedQueryable<PendingExchangeChangeHeader>)query).ThenBy(h => h.Id);

        return new RangeResultSet<PendingExchangeChangeHeader>
        {
            Results = await ordered.Skip(startIndex).Take(count).ToListAsync(),
            TotalResults = total
        };
    }

    /// <inheritdoc />
    public Task<int> CountWaitingExchangeChangesAsync(int connectedSystemId) =>
        _context.PendingExchangeChanges.AsNoTracking()
            .CountAsync(c => c.ConnectedSystemId == connectedSystemId
                             && (c.Status == PendingExchangeChangeStatus.Pending || c.Status == PendingExchangeChangeStatus.Delivering));

    /// <inheritdoc />
    public async Task<HashSet<int>> GetConnectedSystemIdsNotManagingExchangeRecipientsAsync(IReadOnlyCollection<int> connectedSystemIds)
    {
        if (connectedSystemIds.Count == 0)
            return [];
        var ids = connectedSystemIds.ToList();
        return (await _context.ConnectedSystems.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && !s.ManagesExchangeRecipients)
            .Select(s => s.Id)
            .ToListAsync()).ToHashSet();
    }

    /// <summary>The queue's changes on Connected Systems that manage Exchange recipients now: the ones delivery may take.</summary>
    private IQueryable<PendingExchangeChange> ManagedExchangeChanges() =>
        from change in _context.PendingExchangeChanges.AsNoTracking()
        join system in _context.ConnectedSystems.AsNoTracking() on change.ConnectedSystemId equals system.Id
        where system.ManagesExchangeRecipients
        select change;

    /// <inheritdoc />
    public async Task<ExchangeQueueSummary> GetExchangeQueueSummaryAsync(DateTime asOf)
    {
        // One aggregate over the queue, with the change Activity joined only to tell a refusal from a type change this
        // release does not make (the same reading ExchangeChangeOutcome.StateOf makes). An Activity removed by retention
        // reads as a refusal, the state a row with no Activity left can only be.
        var sql = $"""
            SELECT
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Pending} AND s."ManagesExchangeRecipients" AND c."LastAttemptedAt" IS NULL)::int AS "QueuedCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Delivering})::int AS "DeliveringCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Pending} AND s."ManagesExchangeRecipients" AND c."LastAttemptedAt" IS NOT NULL)::int AS "RetryingCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Pending} AND NOT s."ManagesExchangeRecipients")::int AS "HeldCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Parked})::int AS "ParkedCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Refused}
                                   AND a."Status" IS DISTINCT FROM {(int)ActivityStatus.CompleteWithWarning})::int AS "RefusedCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Refused}
                                   AND a."Status" = {(int)ActivityStatus.CompleteWithWarning})::int AS "NotMadeCount",
                COUNT(*) FILTER (WHERE c."Status" = {(int)PendingExchangeChangeStatus.Pending} AND s."ManagesExchangeRecipients"
                                   AND (c."NextRetryAt" IS NULL OR c."NextRetryAt" <= @asOf))::int AS "DueCount"
            FROM "PendingExchangeChanges" c
            JOIN "ConnectedSystems" s ON s."Id" = c."ConnectedSystemId"
            LEFT JOIN "Activities" a ON a."Id" = c."ActivityId"
            """;

        var counts = await _context.Database
            .SqlQueryRaw<ExchangeQueueCounts>(sql, new NpgsqlParameter("asOf", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = asOf })
            .SingleAsync();

        return new ExchangeQueueSummary
        {
            QueuedCount = counts.QueuedCount,
            DeliveringCount = counts.DeliveringCount,
            RetryingCount = counts.RetryingCount,
            ParkedCount = counts.ParkedCount,
            RefusedCount = counts.RefusedCount,
            NotMadeCount = counts.NotMadeCount,
            DueCount = counts.DueCount,
            HeldCount = counts.HeldCount
        };
    }

    /// <inheritdoc />
    public async Task<List<RetriedExchangeChange>> RetryExchangeChangesAsync(PendingExchangeChangeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Chosen by the list's own query and filter, so an action on "the changes shown" acts on exactly those (#2725).
        var candidates = await ApplyExchangeChangeFilter(ExchangeChangeHeaders(), filter)
            .Where(h => h.Status == PendingExchangeChangeStatus.Parked
                        || (h.Status == PendingExchangeChangeStatus.Pending && h.LastAttemptedAt != null && h.ConnectedSystemManagesExchangeRecipients))
            .Select(h => new { h.Id, h.ConnectedSystemName })
            .ToListAsync();
        if (candidates.Count == 0)
            return [];
        var systemNames = candidates.ToDictionary(c => c.Id, c => c.ConnectedSystemName);

        // The reset rechecks each row is still retryable as it writes it, so a change a deliverer claimed after the list
        // was read is left to that deliverer, and one whose system stopped managing Exchange recipients meanwhile stays
        // held with its retry clock (PendingExchangeChange.IsRetryable). The columns cleared here MUST stay in step with PendingExchangeChange.Retry(),
        // which is the same transition in memory. Deliberately hand-written rather than driven from the bulk-columns
        // constant: it resets a fixed set of attempt and claim columns, and a future column must not be swept into it.
        // The update fires jim_exchange_change, which wakes the delivery lane.
        var sql = $$"""
            UPDATE "PendingExchangeChanges"
            SET "Status" = {{(int)PendingExchangeChangeStatus.Pending}}, "AttemptCount" = 0, "LastAttemptedAt" = NULL, "FirstFailedAt" = NULL,
                "NextRetryAt" = NULL, "FailureKind" = NULL, "FailureMessage" = NULL, "ClaimedAt" = NULL, "ClaimedBy" = NULL
            WHERE "Id" = ANY({0})
              AND ("Status" = {{(int)PendingExchangeChangeStatus.Parked}}
                   OR ("Status" = {{(int)PendingExchangeChangeStatus.Pending}} AND "LastAttemptedAt" IS NOT NULL
                       AND EXISTS (SELECT 1 FROM "ConnectedSystems" s
                                   WHERE s."Id" = "PendingExchangeChanges"."ConnectedSystemId" AND s."ManagesExchangeRecipients")))
            RETURNING *
            """;

        var retried = await _context.PendingExchangeChanges
            .FromSqlRaw(sql, new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Value = systemNames.Keys.ToArray() })
            .AsNoTracking()
            .ToListAsync();

        return retried
            .Select(c => new RetriedExchangeChange
            {
                Id = c.Id,
                ActivityId = c.ActivityId,
                ConnectedSystemId = c.ConnectedSystemId,
                ConnectedSystemName = systemNames[c.Id],
                ConnectedSystemObjectId = c.ConnectedSystemObjectId,
                MetaverseObjectId = c.MetaverseObjectId,
                RecipientType = c.RecipientType
            })
            .ToList();
    }

    /// <summary>
    /// Every queued Exchange change as a list row: its Connected System and identity by name, and its own Activity's
    /// status, which is what tells a refusal from a type change this release does not make. Projected in the database:
    /// the list needs a handful of names and columns per row, not the entities behind them.
    /// </summary>
    private IQueryable<PendingExchangeChangeHeader> ExchangeChangeHeaders() =>
        from change in _context.PendingExchangeChanges.AsNoTracking()
        join system in _context.ConnectedSystems.AsNoTracking() on change.ConnectedSystemId equals system.Id
        join activity in _context.Activities.AsNoTracking() on change.ActivityId equals activity.Id into activities
        from activity in activities.DefaultIfEmpty()
        join mvo in _context.MetaverseObjects.AsNoTracking() on change.MetaverseObjectId equals (Guid?)mvo.Id into identities
        from mvo in identities.DefaultIfEmpty()
        select new PendingExchangeChangeHeader
        {
            Id = change.Id,
            ActivityId = change.ActivityId,
            ConnectedSystemId = change.ConnectedSystemId,
            ConnectedSystemName = system.Name,
            ConnectedSystemManagesExchangeRecipients = system.ManagesExchangeRecipients,
            ConnectedSystemObjectId = change.ConnectedSystemObjectId,
            MetaverseObjectId = change.MetaverseObjectId,
            MetaverseObjectDisplayName = mvo == null ? null : mvo.CachedDisplayName,
            // Through the navigation rather than a join of its own: the Object Type foreign key is a shadow property.
            MetaverseObjectTypePluralName = mvo == null ? null : mvo.Type.PluralName,
            RecipientType = change.RecipientType,
            Status = change.Status,
            FailureKind = change.FailureKind,
            FailureMessage = change.FailureMessage,
            AttemptCount = change.AttemptCount,
            CreatedAt = change.CreatedAt,
            LastAttemptedAt = change.LastAttemptedAt,
            FirstFailedAt = change.FirstFailedAt,
            NextRetryAt = change.NextRetryAt,
            // An Activity removed by retention leaves a refused row reading as a refusal, which is all it can be known as.
            ChangeActivityStatus = activity == null ? ActivityStatus.CompleteWithError : activity.Status
        };

    /// <summary>
    /// Narrows the list to what a filter names. The retry chooses its changes through this same function over the same
    /// rows, so the list and the action cannot narrow differently.
    /// </summary>
    private static IQueryable<PendingExchangeChangeHeader> ApplyExchangeChangeFilter(
        IQueryable<PendingExchangeChangeHeader> query,
        PendingExchangeChangeFilter filter)
    {
        if (filter.ConnectedSystemId.HasValue)
        {
            var connectedSystemId = filter.ConnectedSystemId.Value;
            query = query.Where(h => h.ConnectedSystemId == connectedSystemId);
        }

        if (filter.State.HasValue)
        {
            // The states as ExchangeChangeOutcome.StateOf reads them, in terms the database can filter on.
            query = filter.State.Value switch
            {
                ExchangeChangeState.Queued => query.Where(h => h.Status == PendingExchangeChangeStatus.Pending && h.ConnectedSystemManagesExchangeRecipients
                                                               && h.LastAttemptedAt == null),
                ExchangeChangeState.Retrying => query.Where(h => h.Status == PendingExchangeChangeStatus.Pending && h.ConnectedSystemManagesExchangeRecipients
                                                                 && h.LastAttemptedAt != null),
                ExchangeChangeState.Held => query.Where(h => h.Status == PendingExchangeChangeStatus.Pending && !h.ConnectedSystemManagesExchangeRecipients),
                ExchangeChangeState.Delivering => query.Where(h => h.Status == PendingExchangeChangeStatus.Delivering),
                ExchangeChangeState.Parked => query.Where(h => h.Status == PendingExchangeChangeStatus.Parked),
                ExchangeChangeState.Refused => query.Where(h => h.Status == PendingExchangeChangeStatus.Refused
                                                                && h.ChangeActivityStatus != ActivityStatus.CompleteWithWarning),
                ExchangeChangeState.NotMade => query.Where(h => h.Status == PendingExchangeChangeStatus.Refused
                                                                && h.ChangeActivityStatus == ActivityStatus.CompleteWithWarning),
                // Delivered, Withdrawn and Replaced are reached only by leaving the queue.
                _ => query.Where(_ => false)
            };
        }

        if (filter.FailureKind.HasValue)
        {
            var failureKind = filter.FailureKind.Value;
            query = query.Where(h => h.FailureKind == failureKind);
        }

        if (filter.MetaverseObjectId.HasValue)
        {
            var metaverseObjectId = filter.MetaverseObjectId.Value;
            query = query.Where(h => h.MetaverseObjectId == metaverseObjectId);
        }

        if (filter.ConnectedSystemObjectId.HasValue)
        {
            var connectedSystemObjectId = filter.ConnectedSystemObjectId.Value;
            query = query.Where(h => h.ConnectedSystemObjectId == connectedSystemObjectId);
        }

        if (filter.Ids is { Count: > 0 })
        {
            var ids = filter.Ids.ToList();
            query = query.Where(h => ids.Contains(h.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var searchPattern = LikePattern.Contains(filter.SearchText.Trim());
            query = query.Where(h =>
                (h.MetaverseObjectDisplayName != null && EF.Functions.ILike(h.MetaverseObjectDisplayName, searchPattern))
                || EF.Functions.ILike(h.ConnectedSystemName, searchPattern));
        }

        return query;
    }

    /// <summary>The summary's counts as the database returns them, before the summary adds what it derives.</summary>
    private sealed class ExchangeQueueCounts
    {
        public int QueuedCount { get; set; }
        public int DeliveringCount { get; set; }
        public int RetryingCount { get; set; }
        public int ParkedCount { get; set; }
        public int RefusedCount { get; set; }
        public int NotMadeCount { get; set; }
        public int DueCount { get; set; }
        public int HeldCount { get; set; }
    }

    #endregion
}
