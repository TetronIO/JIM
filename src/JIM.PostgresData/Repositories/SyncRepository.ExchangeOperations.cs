// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Exchange queue (#2021)

    /// <summary>
    /// The five values that make up a change's declared state, compared to decide whether a re-stage of a cancelled
    /// change leaves the cancellation held. The SQL twin of <see cref="PendingExchangeChange.HasSameDeclaredStateAs"/>.
    /// </summary>
    private static readonly string[] ExchangeDeclaredStateColumns =
    [
        "RecipientType", "Alias", "Database", "PrimarySmtpAddress", "ExternalAddress"
    ];

    /// <summary>
    /// What a supersede sets each attempt, claim and cancellation column to, rather than the offered row's value: the
    /// values of a change staged afresh, so the row is due at once whatever the caller passed. Every other column in
    /// <see cref="PendingExchangeChangeBulkColumns.PendingExchangeChangesSupersedeUpdate"/> takes the offered value.
    /// </summary>
    private static readonly Dictionary<string, string> ExchangeSupersedeResetValues = new()
    {
        ["Status"] = ((int)PendingExchangeChangeStatus.Pending).ToString(),
        ["FailureKind"] = "NULL",
        ["FailureMessage"] = "NULL",
        ["AttemptCount"] = "0",
        ["LastAttemptedAt"] = "NULL",
        ["FirstFailedAt"] = "NULL",
        ["NextRetryAt"] = "NULL",
        ["ClaimedAt"] = "NULL",
        ["ClaimedBy"] = "NULL",
        ["CancelledAt"] = "NULL",
        ["CancelledById"] = "NULL",
        ["CancelledByName"] = "NULL"
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
                // The statement either writes (and returns the row it wrote) or declines because the account's row is a
                // held cancellation (and returns that row). Neither coming back means a cancelled row appeared between
                // the statement's snapshot and its conflict check, which staging never inserts; fail loudly rather than
                // report the change as handled when nothing is known about it.
                throw new InvalidOperationException(
                    $"Staging the Exchange change for Connected System Object {change.ConnectedSystemObjectId} returned no row.");
            }

            var rowId = reader.GetGuid(0);
            var held = reader.GetBoolean(1);
            var inserted = !held && reader.IsDBNull(2);

            // The row now in the table keeps its own id; the caller must hold that rather than the id it offered, or a
            // later write against this change would target a row that does not exist.
            change.Id = rowId;

            outcomes.Add(new ExchangeChangeStagingOutcome
            {
                RequestedId = requestedId,
                RowId = rowId,
                Disposition = held
                    ? ExchangeChangeStagingDisposition.HeldCancelled
                    : inserted
                        ? ExchangeChangeStagingDisposition.Inserted
                        : ExchangeChangeStagingDisposition.Superseded,
                PreviousActivityId = held || inserted ? null : reader.GetGuid(2)
            });
        }

        return outcomes;
    }

    /// <summary>
    /// One statement per change, deciding against the row the queue already holds for the account, if any (#2021):
    /// insert where there is none; leave a Cancelled row declaring the same state untouched; supersede anything else in
    /// place.
    /// <para>
    /// One statement rather than a read followed by a write, so no other writer (a parallel export batch staging the
    /// same account, or the delivery lane recording an attempt) can act between the decision and the write. The unique
    /// index on the account makes the insert-or-supersede atomic; the conflict clause's WHERE declines the update for a
    /// held cancellation, and the outer SELECT then reads that row's id from the statement's own snapshot, so the
    /// held row is reported without being written (no new row version, no wake-up of the delivery lane).
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
        var accountParameter = $"@p{Array.IndexOf(PendingExchangeChangeBulkColumns.PendingExchangeChanges, "ConnectedSystemObjectId")}";

        return $"""
            WITH staged AS (
                INSERT INTO "PendingExchangeChanges" AS existing ({BulkSqlHelpers.ToQuotedList(PendingExchangeChangeBulkColumns.PendingExchangeChanges)})
                VALUES ({PendingExchangeChangeValuePlaceholders()})
                ON CONFLICT ("ConnectedSystemObjectId") WHERE "ConnectedSystemObjectId" IS NOT NULL DO UPDATE SET ({BulkSqlHelpers.ToQuotedList(update)}) = (
                    {values})
                WHERE NOT (existing."Status" = {(int)PendingExchangeChangeStatus.Cancelled}
                  AND {sameDeclaredState})
                RETURNING new."Id", old."ActivityId"
            )
            SELECT staged."Id", FALSE, staged."ActivityId" FROM staged
            UNION ALL
            SELECT held."Id", TRUE, NULL FROM "PendingExchangeChanges" AS held
            WHERE held."ConnectedSystemObjectId" = {accountParameter} AND NOT EXISTS (SELECT 1 FROM staged)
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
        command.Parameters.Add(new NpgsqlParameter("p22", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)change.CancelledAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p23", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)change.CancelledById ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("p24", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)change.CancelledByName ?? DBNull.Value });
    }

    #endregion
}
