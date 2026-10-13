// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    /// <inheritdoc />
    public async Task<Dictionary<int, SyncRuleInitialPassword>> GetInitialPasswordConfigurationsAsync(IReadOnlyCollection<int> syncRuleIds)
    {
        if (syncRuleIds.Count == 0)
            return [];

        return await _context.SyncRuleInitialPasswords
            .AsNoTracking()
            .Where(ip => syncRuleIds.Contains(ip.SyncRuleId))
            .ToDictionaryAsync(ip => ip.SyncRuleId);
    }

    /// <inheritdoc />
    public async Task<ConnectedSystemPasswordPolicy?> GetDiscoveredPasswordPolicyAsync(int connectedSystemId)
    {
        return await _context.ConnectedSystemPasswordPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(pp => pp.ConnectedSystemId == connectedSystemId);
    }

    #region Password Synchronisation queue (#1119)

    /// <inheritdoc />
    public async Task<List<PendingPasswordChange>> QueuePasswordChangesAsync(IEnumerable<PendingPasswordChange> changes)
    {
        var queueing = changes.ToList();
        var notQueued = new List<PendingPasswordChange>();
        if (queueing.Count == 0)
            return notQueued;

        foreach (var change in queueing.Where(c => c.Id == Guid.Empty))
            change.Id = Guid.NewGuid();

        // Requirement 8's coalescing, done in one statement per change rather than read-then-write. The unique
        // index on (MetaverseObjectId, ConnectedSystemId) is what makes last-write-wins atomic: two password
        // changes for the same identity arriving together cannot both insert, and neither can read a row the
        // other is about to replace.
        //
        // The password and everything describing it is replaced; the obligation to deliver an account's first password
        // is kept where the row is still live and the newer change brings none of its own (#2160): passwords replace
        // each other, promises about the account carry forward. One exception: a propagated change does not replace a
        // live first password on a system whose Password Synchronisation is switched off. The account's first password
        // is delivered whatever the pause, and the propagated change would be the person's password reaching a paused
        // system, so the Initial Password wins, as it does over a held change at staging. Such a change is returned,
        // so the caller can say it was not queued there.
        //
        // Column lists come from the constants so they cannot drift from the model; the parameters are added in
        // PendingPasswordChangeBulkColumns.PendingPasswordChanges order by AddPendingPasswordChangeParameters.
        var keepsObligation = KeepsObligationSql("existing", "EXCLUDED");
        var sql = $"""
            INSERT INTO "PendingPasswordChanges" AS existing ({BulkSqlHelpers.ToQuotedList(PendingPasswordChangeBulkColumns.PendingPasswordChanges)})
            VALUES ({PendingPasswordChangeValuePlaceholders()})
            ON CONFLICT ("MetaverseObjectId", "ConnectedSystemId") DO UPDATE SET
                {string.Join(",\n    ", PendingPasswordChangeBulkColumns.PendingPasswordChangesSupersedeUpdate.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""))},
                {string.Join(",\n    ", PendingPasswordChangeBulkColumns.PendingPasswordChangesInitialPasswordObligation.Select(c =>
                    $"\"{c}\" = CASE WHEN {keepsObligation} THEN {KeptObligationValueSql(c, "existing")} ELSE EXCLUDED.\"{c}\" END"))}
            WHERE NOT ({keepsObligation}
                       AND EXCLUDED."Origin" = {(int)PendingPasswordChangeOrigin.Propagated}
                       AND NOT {PasswordSynchronisationEnabledSql("existing")})
            """;

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var change in queueing)
        {
            await using var command = new NpgsqlCommand(sql, npgsqlConn, npgsqlTx);
            AddPendingPasswordChangeParameters(command, change);

            // An ON CONFLICT DO UPDATE whose WHERE declined the update affects no row: the exception above.
            if (await command.ExecuteNonQueryAsync() == 0)
                notQueued.Add(change);
        }

        return notQueued;
    }

    /// <inheritdoc />
    public async Task<List<ProvisionedPasswordStagingOutcome>> StageProvisionedPasswordChangesAsync(IReadOnlyCollection<PendingPasswordChange> changes)
    {
        var staging = changes.ToList();
        var outcomes = new List<ProvisionedPasswordStagingOutcome>(staging.Count);
        if (staging.Count == 0)
            return outcomes;

        foreach (var change in staging.Where(c => c.Id == Guid.Empty))
            change.Id = Guid.NewGuid();

        // One statement per account (#1697, #2160), deciding what the account's first password is against the row the
        // queue already holds for the person and system, if any. One statement, not an insert followed by a second
        // write, because a delivery lane can delete the waiting row in between, and the account would then be reported
        // as not queued and left with no password at all.
        //
        // - Taken over (the Initial Password replaces the row): a dead row (Expired, Cancelled); one parked for a reason
        //   no attempt can cure (a policy rejection, an unreadable password, an unsupported operation); a propagated
        //   change held because Password Synchronisation is switched off on the system, which has not reached the
        //   system and would not while the account's first password is delivered whatever the pause; an
        //   administrator's set whose account is gone or no longer this person's; or an earlier Initial Password.
        // - Given the obligation (the row carries the account's first password, #2160): a live change that will reach
        //   the new account, being a propagated change for the Object Type the system's Password Synchronisation
        //   targets, or an administrator's set naming the account. It keeps its password, origin, expiry and Activity,
        //   takes the account, the rule and whether the Create wrote the account's flags, is due at once (its claim
        //   taken back, a park the account cures released, attempts against a missing account reset), and its window
        //   extends to the Initial Password's.
        // - Anything else is a live propagated change for another Object Type than the new account's: the WHERE
        //   declines the update and nothing is returned (Conflicted).
        //
        // No row can owe another live account of this person its first password: the new account is joined to the
        // person, and a person has at most one joined Connected System Object per Connected System
        // (IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique). So an earlier Initial Password, and an
        // administrator's set naming any other account, are for an account that is gone or no longer the person's, and
        // both give way; a change that carried an earlier account's first password is judged on its own terms.
        //
        // RETURNING reads the row as it stood before (PostgreSQL 18's OLD), so the caller's Activity describes exactly
        // what was decided on. Run through raw Npgsql rather than EF's Database.SqlQueryRaw: EF composes a SqlQuery as a
        // subquery, and PostgreSQL refuses a data-modifying statement inside one.
        //
        // Column lists come from the constants so they cannot drift from the model; the parameters are added in
        // PendingPasswordChangeBulkColumns.PendingPasswordChanges order by AddPendingPasswordChangeParameters.
        var takeover = $"""
            (existing."Status" IN ({(int)PendingPasswordChangeStatus.Expired}, {(int)PendingPasswordChangeStatus.Cancelled})
             OR (existing."Status" = {(int)PendingPasswordChangeStatus.Parked}
                 AND existing."FailureReason" IS DISTINCT FROM {(int)PasswordSetFailureReason.TargetObjectNotFound}
                 AND existing."FailureReason" IS DISTINCT FROM {(int)PasswordSetFailureReason.Transient})
             OR (existing."Origin" = {(int)PendingPasswordChangeOrigin.Propagated} AND NOT {PasswordSynchronisationEnabledSql("existing")})
             OR (existing."Origin" = {(int)PendingPasswordChangeOrigin.Explicit} AND NOT {AccountStillThePersonsSql("existing")})
             OR existing."Origin" = {(int)PendingPasswordChangeOrigin.Provisioned})
            """;
        var reachesTheAccount = $"""
            ((existing."Origin" = {(int)PendingPasswordChangeOrigin.Explicit} AND existing."ConnectedSystemObjectId" = EXCLUDED."ConnectedSystemObjectId")
             OR (existing."Origin" = {(int)PendingPasswordChangeOrigin.Propagated}
                 AND EXISTS (SELECT 1 FROM "ConnectedSystemPasswordSynchronisations" target
                             JOIN "ConnectedSystemObjects" provisioned ON provisioned."Id" = EXCLUDED."ConnectedSystemObjectId"
                             WHERE target."ConnectedSystemId" = existing."ConnectedSystemId"
                               AND target."TargetObjectTypeId" = provisioned."TypeId")))
            """;
        var curedByTheAccount =
            $"""(existing."Status" = {(int)PendingPasswordChangeStatus.Parked} OR existing."FailureReason" = {(int)PasswordSetFailureReason.TargetObjectNotFound})""";

        // What a change given the obligation becomes, column by column; any column not named keeps its value. Assigned
        // as one row from a sub-select that decides takeover once (decided.takeover), rather than restating the whole
        // decision in every column's CASE: the statement runs once per provisioned account on the export path.
        var carried = new Dictionary<string, string>
        {
            ["ConnectedSystemObjectId"] = "EXCLUDED.\"ConnectedSystemObjectId\"",
            ["Status"] = ((int)PendingPasswordChangeStatus.Pending).ToString(),
            ["NextRetryAt"] = "NULL",
            ["AttemptCount"] = "CASE WHEN decided.cured THEN 0 ELSE existing.\"AttemptCount\" END",
            ["FailureReason"] = "CASE WHEN decided.cured THEN NULL ELSE existing.\"FailureReason\" END",
            ["TargetMessage"] = "CASE WHEN decided.cured THEN NULL ELSE existing.\"TargetMessage\" END",
            ["ExpiresAt"] = "GREATEST(existing.\"ExpiresAt\", EXCLUDED.\"ExpiresAt\")",
            ["ClaimedAt"] = "NULL",
            ["ClaimedBy"] = "NULL",
            ["SyncRuleId"] = "EXCLUDED.\"SyncRuleId\"",
            ["ProvisionedConnectedSystemObjectId"] = "EXCLUDED.\"ProvisionedConnectedSystemObjectId\"",
            ["EnabledStateSetByAttributeFlow"] =
                "EXCLUDED.\"EnabledStateSetByAttributeFlow\" OR COALESCE(existing.\"ProvisionedConnectedSystemObjectId\" = EXCLUDED.\"ProvisionedConnectedSystemObjectId\" AND existing.\"EnabledStateSetByAttributeFlow\", FALSE)"
        };
        var assigned = PendingPasswordChangeBulkColumns.PendingPasswordChangesSupersedeUpdate
            .Concat(PendingPasswordChangeBulkColumns.PendingPasswordChangesInitialPasswordObligation)
            .ToArray();
        var values = string.Join(",\n        ", assigned
            .Select(c => $"CASE WHEN decided.takeover THEN EXCLUDED.\"{c}\" ELSE {carried.GetValueOrDefault(c, $"existing.\"{c}\"")} END"));

        var sql = $"""
            INSERT INTO "PendingPasswordChanges" AS existing ({BulkSqlHelpers.ToQuotedList(PendingPasswordChangeBulkColumns.PendingPasswordChanges)})
            VALUES ({PendingPasswordChangeValuePlaceholders()})
            ON CONFLICT ("MetaverseObjectId", "ConnectedSystemId") DO UPDATE SET ({BulkSqlHelpers.ToQuotedList(assigned)}) = (
                SELECT
                    {values}
                FROM (SELECT {takeover} AS takeover, {curedByTheAccount} AS cured) AS decided)
            WHERE {takeover} OR {reachesTheAccount}
            RETURNING new."Id", new."Origin", old."Origin", old."Status", old."FailureReason", old."TargetMessage", old."ActivityId",
                COALESCE(old."Origin" = {(int)PendingPasswordChangeOrigin.Propagated} AND NOT {PasswordSynchronisationEnabledSql("old")}, FALSE),
                COALESCE(old."Origin" = {(int)PendingPasswordChangeOrigin.Explicit} AND NOT {AccountStillThePersonsSql("old")}, FALSE)
            """;

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var change in staging)
        {
            var requestedId = change.Id;

            await using var command = new NpgsqlCommand(sql, npgsqlConn, npgsqlTx);
            AddPendingPasswordChangeParameters(command, change);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                // The WHERE declined: a live propagated change for another Object Type holds the row, and nothing was written.
                outcomes.Add(new ProvisionedPasswordStagingOutcome
                {
                    RowId = Guid.Empty,
                    RequestedId = requestedId,
                    Disposition = ProvisionedPasswordStagingDisposition.Conflicted
                });
                continue;
            }

            var rowId = reader.GetGuid(0);
            var origin = (PendingPasswordChangeOrigin)reader.GetInt32(1);
            var inserted = reader.IsDBNull(2);

            // The row now in the table keeps its own id; the caller must hold that rather than the id it offered, or a
            // later write against this change would target a row that no longer exists.
            change.Id = rowId;

            outcomes.Add(new ProvisionedPasswordStagingOutcome
            {
                RowId = rowId,
                RequestedId = requestedId,
                Disposition = inserted
                    ? ProvisionedPasswordStagingDisposition.Inserted
                    : origin == PendingPasswordChangeOrigin.Provisioned
                        ? ProvisionedPasswordStagingDisposition.Superseded
                        : ProvisionedPasswordStagingDisposition.Coalesced,
                ExistingOrigin = inserted ? null : (PendingPasswordChangeOrigin)reader.GetInt32(2),
                ExistingStatus = inserted ? null : (PendingPasswordChangeStatus)reader.GetInt32(3),
                ExistingFailureReason = reader.IsDBNull(4) ? null : (PasswordSetFailureReason)reader.GetInt32(4),
                ExistingTargetMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
                ExistingActivityId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                ExistingHeld = reader.GetBoolean(7),
                ExistingAccountGone = reader.GetBoolean(8)
            });
        }

        return outcomes;
    }

    /// <summary>The statuses in which a row is still owed: waiting, being delivered, or waiting on a person.</summary>
    private static readonly string LiveStatusesSql =
        $"{(int)PendingPasswordChangeStatus.Pending}, {(int)PendingPasswordChangeStatus.Delivering}, {(int)PendingPasswordChangeStatus.Parked}";

    /// <summary>
    /// The account a row owes its first password (#2160), in SQL over the row aliased <paramref name="row"/>: its
    /// recorded account, or a Provisioned row's own account where an older writer did not record it.
    /// </summary>
    private static string OwedAccountSql(string row) =>
        $"""COALESCE({row}."ProvisionedConnectedSystemObjectId", CASE WHEN {row}."Origin" = {(int)PendingPasswordChangeOrigin.Provisioned} THEN {row}."ConnectedSystemObjectId" END)""";

    /// <summary>
    /// Whether the live row <paramref name="row"/> keeps its obligation to deliver an account's first password while
    /// the offered change <paramref name="offered"/> replaces its password: the SQL twin of
    /// <see cref="PendingPasswordChange.KeepsInitialPasswordObligationOver"/>.
    /// </summary>
    private static string KeepsObligationSql(string row, string offered) => $"""
        ({OwedAccountSql(row)} IS NOT NULL
         AND {row}."Status" IN ({LiveStatusesSql})
         AND {offered}."Origin" <> {(int)PendingPasswordChangeOrigin.Provisioned}
         AND {offered}."ProvisionedConnectedSystemObjectId" IS NULL)
        """;

    /// <summary>
    /// The value an obligation column keeps when <see cref="KeepsObligationSql"/> holds: the row's own, with the
    /// account filled in for a Provisioned row an older writer left without it.
    /// </summary>
    private static string KeptObligationValueSql(string column, string row) =>
        column == "ProvisionedConnectedSystemObjectId" ? OwedAccountSql(row) : $"{row}.\"{column}\"";

    /// <summary>Whether the Connected System of the row aliased <paramref name="row"/> is taking propagated passwords.</summary>
    private static string PasswordSynchronisationEnabledSql(string row) => $"""
        EXISTS (SELECT 1 FROM "ConnectedSystemPasswordSynchronisations" configuration
                WHERE configuration."ConnectedSystemId" = {row}."ConnectedSystemId" AND configuration."Enabled")
        """;

    /// <summary>Whether the account the row aliased <paramref name="row"/> names still exists and is still this person's.</summary>
    private static string AccountStillThePersonsSql(string row) => $"""
        EXISTS (SELECT 1 FROM "ConnectedSystemObjects" named
                WHERE named."Id" = {row}."ConnectedSystemObjectId" AND named."MetaverseObjectId" = {row}."MetaverseObjectId")
        """;

    /// <summary>The VALUES placeholders for a whole row, @p0 onwards in column-list order.</summary>
    private static string PendingPasswordChangeValuePlaceholders() =>
        string.Join(", ", Enumerable.Range(0, PendingPasswordChangeBulkColumns.PendingPasswordChanges.Length).Select(i => $"@p{i}"));

    /// <summary>
    /// Adds a whole row's values as @p0 onwards. The order below MUST match
    /// PendingPasswordChangeBulkColumns.PendingPasswordChanges exactly; every raw writer of a whole row goes through
    /// here, so there is one place to extend when the table gains a column.
    /// </summary>
    private static void AddPendingPasswordChangeParameters(NpgsqlCommand command, PendingPasswordChange change)
    {
        command.Parameters.Add(new NpgsqlParameter("p0", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = change.Id });
        command.Parameters.Add(new NpgsqlParameter("p1", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = change.MetaverseObjectId });
        command.Parameters.Add(new NpgsqlParameter("p2", NpgsqlTypes.NpgsqlDbType.Integer) { Value = change.ConnectedSystemId });
        command.Parameters.Add(new NpgsqlParameter("p3", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)change.ConnectedSystemObjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p4", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.EncryptedPassword ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p5", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)change.ExpiryBehaviour });
        command.Parameters.Add(new NpgsqlParameter("p6", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)change.Status });
        command.Parameters.Add(new NpgsqlParameter("p7", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)(int?)change.FailureReason ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p8", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.TargetMessage ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p9", NpgsqlTypes.NpgsqlDbType.Integer) { Value = change.AttemptCount });
        command.Parameters.Add(new NpgsqlParameter("p10", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.NextRetryAt ?? DBNull.Value });
        // CreatedAt and ExpiresAt are inferred, as the writers before this one inferred them: the column type follows the
        // Npgsql timestamp mode the repository runs under.
        command.Parameters.Add(new NpgsqlParameter("p11", change.CreatedAt));
        command.Parameters.Add(new NpgsqlParameter("p12", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.LastAttemptedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p13", change.ExpiresAt));
        command.Parameters.Add(new NpgsqlParameter("p14", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = change.ActivityId });
        command.Parameters.Add(new NpgsqlParameter("p15", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.CancelledAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p16", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)change.CancelledById ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p17", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.CancelledByName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p18", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.ClaimedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p19", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.ClaimedBy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p20", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)change.Origin });
        command.Parameters.Add(new NpgsqlParameter("p21", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = (object?)change.EnableAccount ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p22", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)change.SyncRuleId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p23", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = change.EnabledStateSetByAttributeFlow });
        command.Parameters.Add(new NpgsqlParameter("p24", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)change.ProvisionedConnectedSystemObjectId ?? DBNull.Value });
    }

    /// <inheritdoc />
    public async Task<int> MarkOwedInitialPasswordsAsSetByAttributeFlowAsync(int connectedSystemId, IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return 0;

        // A narrow status mark on one column (#2064), so exempt from the bulk column list rule. Only a row owing an
        // account its first password reads the flag: an Initial Password, or a change carrying the first password in
        // its place (#2160), matched by the account it owes rather than by the account it is delivered to. A Provisioned
        // row is matched by its own account too, so one written without the obligation's account is still marked
        // (belt and braces with the delivery's own check). Every status is marked, not only the ones due now: a
        // Cancelled row comes back to Pending on a Retry, and a Delivering row's attempt reads the flag afresh before it
        // decides; marking a row that can never be delivered (Expired) costs nothing. A row already marked is left
        // alone, so the count says what this export changed.
        return await _context.Database.ExecuteSqlRawAsync("""
            UPDATE "PendingPasswordChanges" SET "EnabledStateSetByAttributeFlow" = TRUE
            WHERE "ConnectedSystemId" = {0}
              AND ("ProvisionedConnectedSystemObjectId" = ANY({1})
                   OR ("Origin" = {2} AND "ConnectedSystemObjectId" = ANY({1})))
              AND NOT "EnabledStateSetByAttributeFlow"
            """,
            connectedSystemId,
            new NpgsqlParameter { Value = connectedSystemObjectIds.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid },
            (int)PendingPasswordChangeOrigin.Provisioned);
    }

    /// <inheritdoc />
    public async Task CreateActivitiesAsync(IReadOnlyCollection<Activity> activities)
    {
        if (activities.Count == 0)
            return;

        _context.Activities.AddRange(activities);
        await _context.SaveChangesAsync();

        // Detached so a run-long tracker does not accumulate these: callers set no navigations, so nothing else
        // needs tracking on their account, and the Worker's DbContext otherwise lives for the whole run profile
        // execution.
        foreach (var activity in activities)
            _context.Entry(activity).State = EntityState.Detached;
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
        // nothing it may deliver. Enabling the system releases them, and that row update wakes the service.
        // Anything but a propagated change (explicit sets, #1635 decision D1; provisioned first passwords,
        // #1697), and a propagated change carrying an account's first password (#2160), is claimed whatever the
        // configuration says, so it always counts.
        var claimExpiredBefore = asOf - claimLease;
        return await _context.PendingPasswordChanges
            .AsNoTracking()
            .Where(c => (c.Status == PendingPasswordChangeStatus.Pending && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                         || c.Status == PendingPasswordChangeStatus.Delivering && c.ClaimedAt != null && c.ClaimedAt <= claimExpiredBefore)
                        && (c.Origin != PendingPasswordChangeOrigin.Propagated
                            || c.ProvisionedConnectedSystemObjectId != null
                            || _context.ConnectedSystemPasswordSynchronisations
                                .Any(ps => ps.ConnectedSystemId == c.ConnectedSystemId && ps.Enabled)))
            .Select(c => c.ConnectedSystemId)
            .Distinct()
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<PendingPasswordChange>> ClaimDuePasswordChangesAsync(int connectedSystemId, string claimedBy, DateTime asOf, TimeSpan lease, int maximum, bool excludePropagated)
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
        // propagated passwords the lane passes the propagated origin to exclude and claims everything but
        // administrators' sets and provisioned first passwords; over a live system it passes null and claims
        // everything due (#1635; widened from explicit-only by #1697, decision D1). A propagated change carrying an
        // account's first password is claimed either way, like the Initial Password it stands in for (#2160).
        var claimExpiredBefore = asOf - lease;
        const string sql = """
            WITH due AS (
                SELECT "Id"
                FROM "PendingPasswordChanges"
                WHERE "ConnectedSystemId" = {0}
                  AND (("Status" = {1} AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= {2}))
                    OR ("Status" = {3} AND "ClaimedAt" IS NOT NULL AND "ClaimedAt" <= {4}))
                  AND ({7} IS NULL OR "Origin" <> {7} OR "ProvisionedConnectedSystemObjectId" IS NOT NULL)
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
                BulkSqlHelpers.NullableParam(excludePropagated ? (int?)PendingPasswordChangeOrigin.Propagated : null, NpgsqlTypes.NpgsqlDbType.Integer))
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
        // will not make (see PasswordQueueDeliveryOutlook); anything but a propagated change counts wherever it
        // is (#1635; widened by #1697).
        var claimExpiredBefore = asOf - claimLease;
        var outlook = await _context.PendingPasswordChanges.AsNoTracking()
            .Where(c => c.Origin != PendingPasswordChangeOrigin.Propagated
                        || c.ProvisionedConnectedSystemObjectId != null
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

        // Guarded on status (#1697, decision D9), mirroring RecordPasswordChangeAttemptsAsync: a row
        // superseded or retried mid-flight is Pending again and carries newer work, so the older delivery's
        // success must not delete it out from under the retry. A Cancelled row whose password nevertheless
        // landed at the target is still removed.
        await _context.PendingPasswordChanges
            .Where(c => deleting.Contains(c.Id)
                        && (c.Status == PendingPasswordChangeStatus.Delivering || c.Status == PendingPasswordChangeStatus.Cancelled))
            .ExecuteDeleteAsync();
    }

    /// <inheritdoc />
    public async Task<int> ExpirePasswordChangesAsync(int connectedSystemId, DateTime asOf, bool excludePropagated)
    {
        // Deliberately hand-written rather than driven from the bulk-columns constant: this marks exactly three
        // columns, and a future column must not be swept into it. The status filter lives in the WHERE rather
        // than at the caller so a parked change cannot be expired out from under the administrator who was asked
        // to look at it, whichever caller asks.
        return await _context.PendingPasswordChanges
            .Where(c => c.ConnectedSystemId == connectedSystemId
                        && c.Status == PendingPasswordChangeStatus.Pending
                        && c.ExpiresAt <= asOf
                        && (!excludePropagated || c.Origin != PendingPasswordChangeOrigin.Propagated || c.ProvisionedConnectedSystemObjectId != null))
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
    public async Task<int> ReleaseParkedProvisionedPasswordChangesAsync(int syncRuleId)
    {
        // The Synchronisation Rule counterpart of ReleasePasswordChangesForDeliveryAsync above: same columns,
        // scoped to the one rule's Provisioned rows rather than a whole Connected System. The update fires the
        // queue's own NOTIFY trigger, so the Password Delivery Service attempts the released rows within
        // seconds; the Parked filter lives here, not in the caller.
        return await _context.PendingPasswordChanges
            .Where(c => c.SyncRuleId == syncRuleId
                        && c.Origin == PendingPasswordChangeOrigin.Provisioned
                        && c.Status == PendingPasswordChangeStatus.Parked)
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

        // A settled Connected System is absent from the dictionary rather than present with zeroes, so a
        // caller can tell "nothing to report" from "reported nothing".
        return counts
            .GroupBy(c => c.ConnectedSystemId)
            .ToDictionary(g => g.Key, g => new PasswordQueueAttention
            {
                ParkedCount = g.Where(c => c.Status == PendingPasswordChangeStatus.Parked).Sum(c => c.Count),
                ExpiredCount = g.Where(c => c.Status == PendingPasswordChangeStatus.Expired).Sum(c => c.Count)
            });
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, InitialPasswordAttention>> GetProvisionedPasswordAttentionBySyncRuleAsync(IReadOnlyCollection<int> syncRuleIds)
    {
        if (syncRuleIds.Count == 0)
            return [];

        // The Synchronisation Rule counterpart of GetPasswordQueueAttentionAsync above, over the same table but
        // grouped by rule and narrowed to Provisioned rows: those are the ones a rule's own settings can park.
        var counts = await _context.PendingPasswordChanges
            .AsNoTracking()
            .Where(c => c.Origin == PendingPasswordChangeOrigin.Provisioned
                        && c.SyncRuleId.HasValue && syncRuleIds.Contains(c.SyncRuleId.Value)
                        && (c.Status == PendingPasswordChangeStatus.Parked
                            || c.Status == PendingPasswordChangeStatus.Expired))
            .GroupBy(c => new { SyncRuleId = c.SyncRuleId!.Value, c.Status })
            .Select(g => new { g.Key.SyncRuleId, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        // A settled rule is absent from the dictionary rather than present with zeroes, so a caller can tell
        // "nothing to report" from "reported nothing".
        return counts
            .GroupBy(c => c.SyncRuleId)
            .ToDictionary(g => g.Key, g => new InitialPasswordAttention
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
                SyncRuleId = change.SyncRuleId,
                ProvisionedConnectedSystemObjectId = change.ProvisionedConnectedSystemObjectId,
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
                // switched off. Anything but a propagated change is due wherever it is (#1635; widened by
                // #1697).
                DueCount = g.Count(c => c.Status == PendingPasswordChangeStatus.Pending
                                        && (c.NextRetryAt == null || c.NextRetryAt <= asOf)
                                        && (c.Origin != PendingPasswordChangeOrigin.Propagated
                                            || c.ProvisionedConnectedSystemObjectId != null
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

    /// <inheritdoc />
    public async Task<List<InitialPasswordRejection>> GetParkedProvisionedPasswordReasonsAsync(int syncRuleId)
    {
        // The rule surfaces' rejection-reasons source, over this queue's Provisioned rows.
        return await _context.PendingPasswordChanges
            .AsNoTracking()
            .Where(c => c.SyncRuleId == syncRuleId
                        && c.Origin == PendingPasswordChangeOrigin.Provisioned
                        && c.Status == PendingPasswordChangeStatus.Parked)
            .GroupBy(c => c.TargetMessage)
            .Select(g => new InitialPasswordRejection
            {
                TargetMessage = g.Key,
                FailureReason = g.Max(c => c.FailureReason),
                AccountCount = g.Count(),
                FirstSeenAt = g.Min(c => c.LastAttemptedAt)
            })
            .OrderByDescending(r => r.AccountCount)
            .ToListAsync();
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
            var searchPattern = LikePattern.Contains(filter.SearchText.Trim());
            query = query.Where(h =>
                (h.MetaverseObjectDisplayName != null && EF.Functions.ILike(h.MetaverseObjectDisplayName, searchPattern))
                || EF.Functions.ILike(h.ConnectedSystemName, searchPattern));
        }

        return query;
    }

    #endregion

    #region Initial Password hold (#2039)

    public async Task<HashSet<Guid>> GetMetaverseObjectIdsOwedPasswordAsync(int connectedSystemId, IReadOnlyCollection<Guid> metaverseObjectIds)
    {
        if (metaverseObjectIds.Count == 0)
            return [];

        // Raw SQL per the worker hot-path rule: asked once per confirming-import page that holds a change awaiting an
        // Initial Password (#2039). The unique (MetaverseObjectId, ConnectedSystemId) index serves the lookup, and a
        // read-side projection is exempt from the bulk column list rule.
        const string sql = """
            SELECT DISTINCT "MetaverseObjectId"
            FROM "PendingPasswordChanges"
            WHERE "ConnectedSystemId" = @connectedSystemId
              AND "MetaverseObjectId" = ANY(@metaverseObjectIds)
              AND "Status" = ANY(@owedStatuses)
            """;
        int[] owedStatuses =
        [
            (int)PendingPasswordChangeStatus.Pending,
            (int)PendingPasswordChangeStatus.Delivering,
            (int)PendingPasswordChangeStatus.Parked
        ];

        return await ReadGuidSetAsync(sql,
            new NpgsqlParameter("connectedSystemId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = connectedSystemId },
            new NpgsqlParameter("metaverseObjectIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = metaverseObjectIds.ToArray() },
            new NpgsqlParameter("owedStatuses", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = owedStatuses });
    }

    #endregion
}
