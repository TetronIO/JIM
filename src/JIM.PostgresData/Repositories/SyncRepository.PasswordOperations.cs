// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Password Synchronisation queue (#1119)

    /// <inheritdoc />
    public async Task QueuePasswordChangesAsync(IEnumerable<PendingPasswordChange> changes)
    {
        var queueing = changes.ToList();
        if (queueing.Count == 0)
            return;

        foreach (var change in queueing.Where(c => c.Id == Guid.Empty))
            change.Id = Guid.NewGuid();

        // Requirement 8's coalescing, done in one statement per change rather than read-then-write. The unique
        // index on (MetaverseObjectId, ConnectedSystemId) is what makes last-write-wins atomic: two password
        // changes for the same identity arriving together cannot both insert, and neither can read a row the
        // other is about to replace.
        //
        // Column lists come from the constants so they cannot drift from the model; the parameter order below
        // MUST match PendingPasswordChangeBulkColumns.PendingPasswordChanges exactly.
        var columns = BulkSqlHelpers.ToQuotedList(PendingPasswordChangeBulkColumns.PendingPasswordChanges);
        var placeholders = string.Join(", ", Enumerable.Range(0, PendingPasswordChangeBulkColumns.PendingPasswordChanges.Length).Select(i => $"{{{i}}}"));
        var assignments = string.Join(", ", PendingPasswordChangeBulkColumns.PendingPasswordChangesSupersedeUpdate
            .Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));

        var sql = $"""
            INSERT INTO "PendingPasswordChanges" ({columns}) VALUES ({placeholders})
            ON CONFLICT ("MetaverseObjectId", "ConnectedSystemId") DO UPDATE SET {assignments}
            """;

        foreach (var change in queueing)
        {
            await _context.Database.ExecuteSqlRawAsync(sql,
                change.Id,
                change.MetaverseObjectId,
                change.ConnectedSystemId,
                BulkSqlHelpers.NullableParam(change.ConnectedSystemObjectId, NpgsqlTypes.NpgsqlDbType.Uuid),
                change.EncryptedPassword,
                (int)change.ExpiryBehaviour,
                (int)change.Status,
                BulkSqlHelpers.NullableParam((int?)change.FailureReason, NpgsqlTypes.NpgsqlDbType.Integer),
                BulkSqlHelpers.NullableParam(change.TargetMessage, NpgsqlTypes.NpgsqlDbType.Text),
                change.AttemptCount,
                BulkSqlHelpers.NullableParam(change.NextRetryAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                change.CreatedAt,
                BulkSqlHelpers.NullableParam(change.LastAttemptedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                change.ExpiresAt,
                change.ActivityId,
                BulkSqlHelpers.NullableParam(change.CancelledAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.CancelledById, NpgsqlTypes.NpgsqlDbType.Uuid),
                BulkSqlHelpers.NullableParam(change.CancelledByName, NpgsqlTypes.NpgsqlDbType.Text),
                BulkSqlHelpers.NullableParam(change.ClaimedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.ClaimedBy, NpgsqlTypes.NpgsqlDbType.Text),
                (int)change.Origin,
                BulkSqlHelpers.NullableParam(change.EnableAccount, NpgsqlTypes.NpgsqlDbType.Boolean));
        }
    }

    /// <inheritdoc />
    public async Task<List<PendingPasswordChange>> GetDuePasswordChangesAsync(int connectedSystemId, DateTime asOf, int maximum)
    {
        return await _context.PendingPasswordChanges
            .AsNoTracking()
            .Where(c => c.ConnectedSystemId == connectedSystemId
                        && c.Status == PendingPasswordChangeStatus.Pending
                        && (c.NextRetryAt == null || c.NextRetryAt <= asOf))
            // Oldest first, so a change that has been waiting longest is delivered first and nothing starves
            // behind a steady stream of newer work.
            .OrderBy(c => c.CreatedAt)
            .Take(maximum)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<int>> GetConnectedSystemIdsWithDuePasswordChangesAsync(DateTime asOf, TimeSpan claimLease)
    {
        // Restricted to what a lane would actually claim, because "due" here means "a lane would attempt this".
        // A propagated change on a system that is not taking passwords is held, not due: once a switched-off
        // system accumulates changes rather than discarding them, counting them would make the service see
        // permanent work and run a lane on every poll, for as long as the system stayed off, each one finding
        // nothing it may deliver. Enabling the system releases them, and that row update wakes the service. An
        // explicit set (#1635, decision D1) is claimed whatever the configuration says, so it always counts.
        var claimExpiredBefore = asOf - claimLease;
        return await _context.PendingPasswordChanges
            .AsNoTracking()
            .Where(c => (c.Status == PendingPasswordChangeStatus.Pending && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                         || c.Status == PendingPasswordChangeStatus.Delivering && c.ClaimedAt != null && c.ClaimedAt <= claimExpiredBefore)
                        && (c.Origin == PendingPasswordChangeOrigin.Explicit
                            || _context.ConnectedSystemPasswordSynchronisations
                                .Any(ps => ps.ConnectedSystemId == c.ConnectedSystemId && ps.Enabled)))
            .Select(c => c.ConnectedSystemId)
            .Distinct()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<PendingPasswordChange>> ClaimDuePasswordChangesAsync(int connectedSystemId, string claimedBy, DateTime asOf, TimeSpan lease, int maximum, bool explicitOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);
        if (maximum < 1)
            return [];

        // Select and update in one statement, which is what makes the claim safe against a second deliverer:
        // the sub-select locks the rows it chooses, SKIP LOCKED makes a concurrent claimer step over them rather
        // than wait and then re-read them, and the update lands before the lock is released at commit. A read
        // followed by a write would leave a window in which both read the same rows.
        //
        // Deliberately hand-written rather than driven from the bulk-columns constant: this marks exactly three
        // call-site-computed columns (a status mark and the claim stamp), and a future column must not be swept
        // into it. RETURNING c.* hands the rows back in the entity's own shape, so nothing is re-read and the
        // caller holds exactly what it claimed.
        //
        // The origin filter is a parameter rather than two statements: over a system that is not taking
        // propagated passwords the lane passes the explicit origin and claims only administrators' sets; over a
        // live system it passes null and claims everything due (#1635).
        var claimExpiredBefore = asOf - lease;
        const string sql = """
            WITH due AS (
                SELECT "Id"
                FROM "PendingPasswordChanges"
                WHERE "ConnectedSystemId" = {0}
                  AND (("Status" = {1} AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= {2}))
                    OR ("Status" = {3} AND "ClaimedAt" IS NOT NULL AND "ClaimedAt" <= {4}))
                  AND ({7} IS NULL OR "Origin" = {7})
                ORDER BY "CreatedAt", "Id"
                LIMIT {5}
                FOR UPDATE SKIP LOCKED
            )
            UPDATE "PendingPasswordChanges" AS c
            SET "Status" = {3}, "ClaimedAt" = {2}, "ClaimedBy" = {6}
            FROM due
            WHERE c."Id" = due."Id"
            RETURNING c.*
            """;

        return await _context.PendingPasswordChanges
            .FromSqlRaw(sql,
                connectedSystemId,
                (int)PendingPasswordChangeStatus.Pending,
                new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = asOf },
                (int)PendingPasswordChangeStatus.Delivering,
                new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz, Value = claimExpiredBefore },
                maximum,
                claimedBy,
                BulkSqlHelpers.NullableParam(explicitOnly ? (int?)PendingPasswordChangeOrigin.Explicit : null, NpgsqlTypes.NpgsqlDbType.Integer))
            .AsNoTracking()
            // Materialised in the statement's own order: EF Core composes nothing over a query that is read
            // straight out, so the ORDER BY inside the claim is the order the caller sees.
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> ReleasePasswordChangeClaimsAsync(IEnumerable<Guid> ids)
    {
        var releasing = ids.ToList();
        if (releasing.Count == 0)
            return 0;

        // Guarded on Delivering: a row cancelled or superseded while the lane held it has an outcome of its own
        // now, and this must not turn a cancelled change back into a pending one.
        return await _context.PendingPasswordChanges
            .Where(c => releasing.Contains(c.Id) && c.Status == PendingPasswordChangeStatus.Delivering)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, PendingPasswordChangeStatus.Pending)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null)
                .SetProperty(c => c.ClaimedBy, (string?)null));
    }

    /// <inheritdoc />
    public async Task<PasswordQueueDeliveryOutlook> GetPasswordQueueDeliveryOutlookAsync(DateTime asOf, TimeSpan claimLease)
    {
        // One grouped round trip, read on every iteration of the delivery loop: three numbers from one scan of a
        // table that is small whenever the service is keeping up. Restricted to what a lane would claim, so a
        // paused system's held propagated changes neither inflate the counts nor wake the service for retries it
        // will not make (see PasswordQueueDeliveryOutlook); an explicit set counts wherever it is (#1635).
        var claimExpiredBefore = asOf - claimLease;
        var outlook = await _context.PendingPasswordChanges.AsNoTracking()
            .Where(c => c.Origin == PendingPasswordChangeOrigin.Explicit
                        || _context.ConnectedSystemPasswordSynchronisations
                            .Any(ps => ps.ConnectedSystemId == c.ConnectedSystemId && ps.Enabled))
            .GroupBy(_ => 1)
            .Select(g => new PasswordQueueDeliveryOutlook
            {
                DueCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Pending && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                                        || c.Status == PendingPasswordChangeStatus.Delivering && c.ClaimedAt != null && c.ClaimedAt <= claimExpiredBefore),
                RetryingCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Pending && c.NextRetryAt != null && c.NextRetryAt > asOf),
                NextAttemptAt = g.Where(c => c.Status == PendingPasswordChangeStatus.Pending && c.NextRetryAt != null && c.NextRetryAt > asOf)
                    .Min(c => c.NextRetryAt)
            })
            .SingleOrDefaultAsync();

        // An empty queue groups to nothing rather than to a row of zeroes, which is the answer the caller wants.
        return outlook ?? new PasswordQueueDeliveryOutlook();
    }

    /// <inheritdoc />
    public async Task<List<PendingPasswordChange>> GetPasswordChangesByActivityAsync(Guid activityId)
    {
        return await _context.PendingPasswordChanges
            .AsNoTracking()
            .Where(c => c.ActivityId == activityId)
            .OrderBy(c => c.ConnectedSystemId)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task RecordPasswordChangeAttemptsAsync(IEnumerable<PendingPasswordChange> changes)
    {
        var recording = changes.ToList();
        if (recording.Count == 0)
            return;

        // Assignment list from the constant, so a migration adding a column that an attempt can change fails the
        // completeness test rather than silently going unwritten. The id parameter is last.
        var assignments = string.Join(", ", PendingPasswordChangeBulkColumns.PendingPasswordChangesAttemptUpdate
            .Select((c, i) => $"\"{c}\" = {{{i}}}"));
        var idPlaceholder = $"{{{PendingPasswordChangeBulkColumns.PendingPasswordChangesAttemptUpdate.Length}}}";
        var deliveringPlaceholder = $"{{{PendingPasswordChangeBulkColumns.PendingPasswordChangesAttemptUpdate.Length + 1}}}";

        // Guarded on the row still being Delivering (#1635). The lane wrote its claim before attempting, so a row
        // that is no longer Delivering was taken away in the meantime: cancelled from the queue page, retried,
        // or superseded by a newer password. Each of those is an outcome that must survive; overwriting it with
        // this attempt would turn "the administrator cancelled it" into "JIM will try again".
        var sql = $"""UPDATE "PendingPasswordChanges" SET {assignments} WHERE "Id" = {idPlaceholder} AND "Status" = {deliveringPlaceholder}""";

        foreach (var change in recording)
        {
            await _context.Database.ExecuteSqlRawAsync(sql,
                BulkSqlHelpers.NullableParam(change.ConnectedSystemObjectId, NpgsqlTypes.NpgsqlDbType.Uuid),
                (int)change.Status,
                BulkSqlHelpers.NullableParam((int?)change.FailureReason, NpgsqlTypes.NpgsqlDbType.Integer),
                BulkSqlHelpers.NullableParam(change.TargetMessage, NpgsqlTypes.NpgsqlDbType.Text),
                change.AttemptCount,
                BulkSqlHelpers.NullableParam(change.NextRetryAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.LastAttemptedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.ClaimedAt, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                BulkSqlHelpers.NullableParam(change.ClaimedBy, NpgsqlTypes.NpgsqlDbType.Text),
                change.Id,
                (int)PendingPasswordChangeStatus.Delivering);
        }
    }

    /// <inheritdoc />
    public async Task DeletePasswordChangesAsync(IEnumerable<Guid> ids)
    {
        var deleting = ids.ToList();
        if (deleting.Count == 0)
            return;

        await _context.PendingPasswordChanges
            .Where(c => deleting.Contains(c.Id))
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<int> ExpirePasswordChangesAsync(int connectedSystemId, DateTime asOf, bool explicitOnly)
    {
        // Deliberately hand-written rather than driven from the bulk-columns constant: this marks exactly three
        // columns, and a future column must not be swept into it. The status filter lives in the WHERE rather
        // than at the caller so a parked change cannot be expired out from under the administrator who was asked
        // to look at it, whichever caller asks.
        return await _context.PendingPasswordChanges
            .Where(c => c.ConnectedSystemId == connectedSystemId
                        && c.Status == PendingPasswordChangeStatus.Pending
                        && c.ExpiresAt <= asOf
                        && (!explicitOnly || c.Origin == PendingPasswordChangeOrigin.Explicit))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, PendingPasswordChangeStatus.Expired)
                .SetProperty(c => c.NextRetryAt, (DateTime?)null));
    }

    /// <inheritdoc />
    public async Task<int> ReleasePasswordChangesForDeliveryAsync(int connectedSystemId)
    {
        // Requirement 3's drain-on-enable, and the same mechanic behind releasing work when a Connected System's
        // Password Synchronisation settings change: everything parked against this system becomes due now.
        //
        // Expired changes are deliberately left alone. Their window passed, so the password they carry may well
        // have been superseded by one JIM never saw; delivering it would set a password the person has already
        // replaced, which is precisely what the time to live exists to prevent.
        return await _context.PendingPasswordChanges
            .Where(c => c.ConnectedSystemId == connectedSystemId && c.Status == PendingPasswordChangeStatus.Parked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, PendingPasswordChangeStatus.Pending)
                .SetProperty(c => c.AttemptCount, 0)
                .SetProperty(c => c.NextRetryAt, (DateTime?)null)
                .SetProperty(c => c.FailureReason, (Models.Staging.PasswordSetFailureReason?)null)
                .SetProperty(c => c.TargetMessage, (string?)null)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null)
                .SetProperty(c => c.ClaimedBy, (string?)null));
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, PasswordQueueAttention>> GetPasswordQueueAttentionAsync(IReadOnlyCollection<int> connectedSystemIds)
    {
        if (connectedSystemIds.Count == 0)
            return [];

        var counts = await _context.PendingPasswordChanges
            .AsNoTracking()
            // Named rather than "not pending": a cancelled change waits on nobody, so counting it here would
            // report a system as needing attention it does not need.
            .Where(c => connectedSystemIds.Contains(c.ConnectedSystemId)
                        && (c.Status == PendingPasswordChangeStatus.Parked
                            || c.Status == PendingPasswordChangeStatus.Expired))
            .GroupBy(c => new { c.ConnectedSystemId, c.Status })
            .Select(g => new { g.Key.ConnectedSystemId, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        // A settled Connected System is absent from the dictionary rather than present with zeroes, matching
        // GetInitialPasswordAttentionByConnectedSystemAsync, so a caller can tell "nothing to report" from
        // "reported nothing".
        return counts
            .GroupBy(c => c.ConnectedSystemId)
            .ToDictionary(g => g.Key, g => new PasswordQueueAttention
            {
                ParkedCount = g.Where(c => c.Status == PendingPasswordChangeStatus.Parked).Sum(c => c.Count),
                ExpiredCount = g.Where(c => c.Status == PendingPasswordChangeStatus.Expired).Sum(c => c.Count)
            });
    }

    /// <inheritdoc />
    public async Task<int> DeleteTerminalPasswordChangesAsync(DateTime olderThan, int maxRecords)
    {
        // Batched through a sub-select so one pass cannot become a long transaction on a queue that has been
        // accumulating; oldest first, so a repeated pass drains rather than churning the same rows.
        // Named terminal states rather than "not pending": a Delivering row is live work in a deliverer's hands,
        // and a retention pass removing it would lose a password mid-delivery.
        return await _context.PendingPasswordChanges
            .Where(c => _context.PendingPasswordChanges
                .Where(t => (t.Status == PendingPasswordChangeStatus.Parked
                             || t.Status == PendingPasswordChangeStatus.Expired
                             || t.Status == PendingPasswordChangeStatus.Cancelled)
                            && (t.LastAttemptedAt ?? t.CreatedAt) < olderThan)
                .OrderBy(t => t.LastAttemptedAt ?? t.CreatedAt)
                .Take(maxRecords)
                .Select(t => t.Id)
                .Contains(c.Id))
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<RangeResultSet<PendingPasswordChangeHeader>> GetPendingPasswordChangeHeadersAsync(
        PendingPasswordChangeFilter filter,
        int startIndex,
        int count,
        string sortBy,
        bool sortDescending,
        bool includeTotalCount)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Joined and projected in the database rather than loaded: the queue can hold a row per identity per
        // system, and the list needs two names off each row, not the entity behind it. Projection is also what
        // guarantees the encrypted password is never materialised on a read path.
        var query =
            from change in _context.PendingPasswordChanges.AsNoTracking()
            join system in _context.ConnectedSystems.AsNoTracking() on change.ConnectedSystemId equals system.Id
            join mvo in _context.MetaverseObjects.AsNoTracking() on change.MetaverseObjectId equals mvo.Id
            select new PendingPasswordChangeHeader
            {
                Id = change.Id,
                MetaverseObjectId = change.MetaverseObjectId,
                MetaverseObjectDisplayName = mvo.CachedDisplayName,
                // Reached through the navigation rather than an explicit join: the Object Type foreign key is a
                // shadow property, and EF translates this into the same join without naming it here.
                MetaverseObjectTypePluralName = mvo.Type.PluralName,
                ConnectedSystemId = change.ConnectedSystemId,
                ConnectedSystemName = system.Name,
                // Read per row rather than assumed: a system switched off after its changes were queued holds
                // them, and a row that said "Due now" for one of those would contradict the summary above it.
                ConnectedSystemTakingPasswords = _context.ConnectedSystemPasswordSynchronisations
                    .Any(ps => ps.ConnectedSystemId == change.ConnectedSystemId && ps.Enabled),
                Origin = change.Origin,
                Status = change.Status,
                FailureReason = change.FailureReason,
                TargetMessage = change.TargetMessage,
                AttemptCount = change.AttemptCount,
                NextRetryAt = change.NextRetryAt,
                CreatedAt = change.CreatedAt,
                LastAttemptedAt = change.LastAttemptedAt,
                ExpiresAt = change.ExpiresAt,
                CancelledAt = change.CancelledAt,
                CancelledByName = change.CancelledByName
            };

        query = ApplyHeaderFilter(query, filter);

        // Counted before windowing, and only when asked: the count is the expensive half of a window read, and
        // a scroll re-reads windows far more often than the filters change.
        int? total = includeTotalCount ? await query.CountAsync() : null;

        query = sortBy?.ToLowerInvariant() switch
        {
            "identity" => sortDescending
                ? query.OrderByDescending(h => h.MetaverseObjectDisplayName)
                : query.OrderBy(h => h.MetaverseObjectDisplayName),
            "system" => sortDescending
                ? query.OrderByDescending(h => h.ConnectedSystemName)
                : query.OrderBy(h => h.ConnectedSystemName),
            "status" => sortDescending
                ? query.OrderByDescending(h => h.Status)
                : query.OrderBy(h => h.Status),
            "attempts" => sortDescending
                ? query.OrderByDescending(h => h.AttemptCount)
                : query.OrderBy(h => h.AttemptCount),
            "nextattempt" => sortDescending
                ? query.OrderByDescending(h => h.NextRetryAt)
                : query.OrderBy(h => h.NextRetryAt),
            "expires" => sortDescending
                ? query.OrderByDescending(h => h.ExpiresAt)
                : query.OrderBy(h => h.ExpiresAt),
            // Queued time is the default and the fallback: it is the one column every row has a value for.
            _ => sortDescending
                ? query.OrderByDescending(h => h.CreatedAt)
                : query.OrderBy(h => h.CreatedAt)
        };

        // Id breaks ties, so paging over rows queued in the same instant cannot repeat or skip one.
        var ordered = ((IOrderedQueryable<PendingPasswordChangeHeader>)query).ThenBy(h => h.Id);

        return new RangeResultSet<PendingPasswordChangeHeader>
        {
            Results = await ordered.Skip(startIndex).Take(count).ToListAsync(),
            TotalResults = total
        };
    }

    /// <inheritdoc />
    public async Task<PasswordQueueSummary> GetPasswordQueueSummaryAsync(DateTime asOf)
    {
        // One grouped round trip rather than five counts: the summary sits above a list that is already reading,
        // and five queries to populate four numbers is four too many.
        var counts = await _context.PendingPasswordChanges.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new PasswordQueueSummary
            {
                // A claimed change is still work JIM intends to deliver; it is simply being delivered right now.
                WaitingCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Pending
                                            || c.Status == PendingPasswordChangeStatus.Delivering),
                // Held changes (propagated ones queued for a system that is switched off) are Waiting but not
                // Due, matching GetConnectedSystemIdsWithDuePasswordChangesAsync and the number's own meaning: a
                // lane would not attempt them. Counting them here would make a large Due count, which is meant to
                // read as "the queue is not being drained", the ordinary state of any deployment with a system
                // switched off. An explicit set is due wherever it is (#1635).
                DueCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Pending
                                        && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                                        && (c.Origin == PendingPasswordChangeOrigin.Explicit
                                            || _context.ConnectedSystemPasswordSynchronisations
                                                .Any(ps => ps.ConnectedSystemId == c.ConnectedSystemId && ps.Enabled))),
                ParkedCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Parked),
                ExpiredCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Expired),
                CancelledCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Cancelled)
            })
            .SingleOrDefaultAsync();

        // An empty queue groups to nothing rather than to a row of zeroes, which is the answer the caller wants.
        return counts ?? new PasswordQueueSummary();
    }

    /// <inheritdoc />
    public async Task<int> RetryPasswordChangesAsync(PendingPasswordChangeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Set-based rather than load-mutate-save: "retry everything parked on this system" is the action the
        // page exists for, and it must not depend on how many rows that is. The columns cleared here MUST stay
        // in step with PendingPasswordChange.Retry(), which is the same transition done in memory.
        return await ApplyChangeFilter(_context.PendingPasswordChanges, filter)
            // An expired change has no password left to send, so retrying one would queue an empty delivery; a
            // change being delivered right now is already getting the attempt a retry asks for.
            .Where(c => c.Status != PendingPasswordChangeStatus.Expired
                        && c.Status != PendingPasswordChangeStatus.Delivering)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, PendingPasswordChangeStatus.Pending)
                .SetProperty(c => c.AttemptCount, 0)
                .SetProperty(c => c.NextRetryAt, (DateTime?)null)
                .SetProperty(c => c.FailureReason, (PasswordSetFailureReason?)null)
                .SetProperty(c => c.TargetMessage, (string?)null)
                .SetProperty(c => c.CancelledAt, (DateTime?)null)
                .SetProperty(c => c.CancelledById, (Guid?)null)
                .SetProperty(c => c.CancelledByName, (string?)null)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null)
                .SetProperty(c => c.ClaimedBy, (string?)null));
    }

    /// <inheritdoc />
    public async Task<int> CancelPasswordChangesAsync(
        PendingPasswordChangeFilter filter,
        Guid? cancelledById,
        string? cancelledByName,
        DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return await ApplyChangeFilter(_context.PendingPasswordChanges, filter)
            // Cancelling something already finished would overwrite the outcome that actually happened to it. A
            // change being delivered right now can be cancelled: the deliverer's outcome write is guarded on the
            // row still being Delivering, so the cancellation stands unless the password actually lands, in
            // which case the row is deleted and there is nothing left to have cancelled.
            .Where(c => c.Status == PendingPasswordChangeStatus.Pending
                        || c.Status == PendingPasswordChangeStatus.Parked
                        || c.Status == PendingPasswordChangeStatus.Delivering)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, PendingPasswordChangeStatus.Cancelled)
                .SetProperty(c => c.NextRetryAt, (DateTime?)null)
                .SetProperty(c => c.CancelledAt, asOf)
                .SetProperty(c => c.CancelledById, cancelledById)
                .SetProperty(c => c.CancelledByName, cancelledByName)
                .SetProperty(c => c.ClaimedAt, (DateTime?)null)
                .SetProperty(c => c.ClaimedBy, (string?)null));
    }

    /// <summary>
    /// Narrows a queue query to what a filter names. Kept separate from the header projection so the retry and
    /// cancel actions apply exactly the same rules the list did, rather than a re-typed approximation of them.
    /// </summary>
    private static IQueryable<PendingPasswordChange> ApplyChangeFilter(
        IQueryable<PendingPasswordChange> query,
        PendingPasswordChangeFilter filter)
    {
        if (filter.ConnectedSystemId.HasValue)
        {
            var connectedSystemId = filter.ConnectedSystemId.Value;
            query = query.Where(c => c.ConnectedSystemId == connectedSystemId);
        }

        if (filter.Status.HasValue)
        {
            // Pending is the portal's "Waiting", and a change the Password Delivery Service has claimed is still
            // waiting from the administrator's side (#1635): the summary counts it so, and a list that dropped it
            // would show one fewer row than the card above it. Every other status is exact.
            var status = filter.Status.Value;
            query = status == PendingPasswordChangeStatus.Pending
                ? query.Where(c => c.Status == PendingPasswordChangeStatus.Pending || c.Status == PendingPasswordChangeStatus.Delivering)
                : query.Where(c => c.Status == status);
        }

        if (filter.FailureReason.HasValue)
        {
            var reason = filter.FailureReason.Value;
            query = query.Where(c => c.FailureReason == reason);
        }

        if (filter.MetaverseObjectId.HasValue)
        {
            var metaverseObjectId = filter.MetaverseObjectId.Value;
            query = query.Where(c => c.MetaverseObjectId == metaverseObjectId);
        }

        if (filter.Ids is { Count: > 0 })
        {
            var ids = filter.Ids.ToList();
            query = query.Where(c => ids.Contains(c.Id));
        }

        return query;
    }

    /// <summary>
    /// The header-side twin of <see cref="ApplyChangeFilter"/>, applied after projection so the free-text search
    /// can reach the resolved identity and Connected System names.
    /// </summary>
    private static IQueryable<PendingPasswordChangeHeader> ApplyHeaderFilter(
        IQueryable<PendingPasswordChangeHeader> query,
        PendingPasswordChangeFilter filter)
    {
        if (filter.ConnectedSystemId.HasValue)
        {
            var connectedSystemId = filter.ConnectedSystemId.Value;
            query = query.Where(h => h.ConnectedSystemId == connectedSystemId);
        }

        if (filter.Status.HasValue)
        {
            // Pending covers Delivering here too; see ApplyChangeFilter.
            var status = filter.Status.Value;
            query = status == PendingPasswordChangeStatus.Pending
                ? query.Where(h => h.Status == PendingPasswordChangeStatus.Pending || h.Status == PendingPasswordChangeStatus.Delivering)
                : query.Where(h => h.Status == status);
        }

        if (filter.FailureReason.HasValue)
        {
            var reason = filter.FailureReason.Value;
            query = query.Where(h => h.FailureReason == reason);
        }

        if (filter.MetaverseObjectId.HasValue)
        {
            var metaverseObjectId = filter.MetaverseObjectId.Value;
            query = query.Where(h => h.MetaverseObjectId == metaverseObjectId);
        }

        if (filter.Ids is { Count: > 0 })
        {
            var ids = filter.Ids.ToList();
            query = query.Where(h => ids.Contains(h.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var search = filter.SearchText.Trim();
            query = query.Where(h =>
                (h.MetaverseObjectDisplayName != null && EF.Functions.ILike(h.MetaverseObjectDisplayName, $"%{search}%"))
                || EF.Functions.ILike(h.ConnectedSystemName, $"%{search}%"));
        }

        return query;
    }

    #endregion
}
