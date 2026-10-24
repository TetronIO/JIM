// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The Causality Explorer's reads (#2100): an object's causal timeline, execution items as timeline entries, and the
/// source-import hop for a page of items at once.
/// </summary>
/// <remarks>
/// Raw SQL into a flat DTO (the Summary tier): a timeline gathers an object's items from up to five places, and doing
/// that as one statement, ordered and counted in the database, is what keeps a page's cost independent of how long the
/// object's history is. Every enum value is a parameter, so a renumbered member cannot silently change what matches.
/// </remarks>
public partial class ActivityRepository
{
    /// <summary>
    /// The items about a Connected System Object, as the CTE <c>ids</c>. While the object exists its items carry its
    /// id. Its deletion nulls that id on every one of them and leaves it only on the deletion record, which names the
    /// item that deleted it and keeps the external ID and system that find the rest: items in that system, carrying
    /// that external ID, whose own id was nulled (so not a live record that reuses the external ID), and no later than
    /// the deletion (so not a record created after it and since deleted in turn).
    /// </summary>
    private const string ConnectedSystemObjectTimelineIdsSql = """
        WITH deletion AS (
            SELECT c."ConnectedSystemId" AS cs_id, c."DeletedObjectExternalId" AS external_id,
                   c."ChangeTime" AS deleted_at, c."ActivityRunProfileExecutionItemId" AS item_id
            FROM "ConnectedSystemObjectChanges" c
            WHERE c."DeletedConnectedSystemObjectId" = @subject_id AND c."ChangeType" = @deleted_change_type
        ),
        ids AS (
            SELECT r."Id" AS id
            FROM "ActivityRunProfileExecutionItems" r
            WHERE r."ConnectedSystemObjectId" = @subject_id

            UNION

            SELECT d.item_id FROM deletion d WHERE d.item_id IS NOT NULL

            UNION

            SELECT r."Id"
            FROM deletion d
            JOIN "ActivityRunProfileExecutionItems" r
                ON r."ExternalIdSnapshot" = d.external_id AND r."ConnectedSystemObjectId" IS NULL
            JOIN "Activities" a
                ON a."Id" = r."ActivityId" AND a."ConnectedSystemId" = d.cs_id AND a."Executed" <= d.deleted_at
        )
        """;

    /// <summary>
    /// The items about a Metaverse Object, as the CTE <c>ids</c>: those its change records name, under its own id while
    /// it exists and under the id its deletion re-keys them to after; the one that deleted it, which its deletion record
    /// names (#2460); those of the Connected System Objects joined to it; those that record it directly (an export scope
    /// review); and the one that scheduled its deletion, which a disconnection can leave with no change record of its own.
    /// </summary>
    private const string MetaverseObjectTimelineIdsSql = """
        WITH ids AS (
            SELECT c."ActivityRunProfileExecutionItemId" AS id
            FROM "MetaverseObjectChanges" c
            WHERE c."MetaverseObjectId" = @subject_id AND c."ActivityRunProfileExecutionItemId" IS NOT NULL

            UNION

            SELECT c."ActivityRunProfileExecutionItemId"
            FROM "MetaverseObjectChanges" c
            WHERE c."DeletedMetaverseObjectId" = @subject_id AND c."ActivityRunProfileExecutionItemId" IS NOT NULL

            UNION

            SELECT c."DeletedByRunProfileExecutionItemId"
            FROM "MetaverseObjectChanges" c
            WHERE c."DeletedMetaverseObjectId" = @subject_id AND c."DeletedByRunProfileExecutionItemId" IS NOT NULL

            UNION

            SELECT r."Id"
            FROM "ConnectedSystemObjects" cso
            JOIN "ActivityRunProfileExecutionItems" r ON r."ConnectedSystemObjectId" = cso."Id"
            WHERE cso."MetaverseObjectId" = @subject_id

            UNION

            SELECT r."Id"
            FROM "ActivityRunProfileExecutionItems" r
            WHERE r."MetaverseObjectId" = @subject_id

            UNION

            SELECT m."DeletionScheduledByRunProfileExecutionItemId"
            FROM "MetaverseObjects" m
            WHERE m."Id" = @subject_id AND m."DeletionScheduledByRunProfileExecutionItemId" IS NOT NULL
        )
        """;

    /// <summary>
    /// The columns of a <see cref="CausalTimelineEntry"/>, read by <see cref="ReadTimelineEntry"/> in this order, from
    /// an item <c>r</c> joined by <see cref="TimelineEntryJoinsSql"/>. Not every item records the object's type (an
    /// import updating a record it already holds records only the name), so the record's own type stands in where the
    /// item recorded none; a recorded type is kept, since it can name the Metaverse Object the item was about.
    /// </summary>
    private const string TimelineEntryColumnsSql = """
        r."Id" AS item_id, r."ActivityId" AS activity_id, a."Executed" AS executed, a."TargetType" AS target_type,
        a."TargetName" AS target_name, a."TargetContext" AS target_context, a."ConnectedSystemId" AS cs_id,
        a."ConnectedSystemRunType" AS run_type, r."ObjectChangeType" AS change_type, r."OutcomeSummary" AS outcome_summary,
        r."ErrorType" AS error_type, r."DisplayNameSnapshot" AS display_name,
        COALESCE(r."ObjectTypeSnapshot", cso_type."Name") AS object_type,
        r."ConnectedSystemObjectId" AS cso_id, r."ExternalIdSnapshot" AS external_id, r."MetaverseObjectId" AS mvo_id
        """;

    /// <summary>
    /// What <see cref="TimelineEntryColumnsSql"/> reads beside an item <c>r</c>: its Activity, and its record's type.
    /// </summary>
    private const string TimelineEntryJoinsSql = """
        JOIN "Activities" a ON a."Id" = r."ActivityId"
        LEFT JOIN "ConnectedSystemObjects" cso ON cso."Id" = r."ConnectedSystemObjectId"
        LEFT JOIN "ConnectedSystemObjectTypes" cso_type ON cso_type."Id" = cso."TypeId"
        """;

    private const int TimelineEntryColumnCount = 16;

    /// <inheritdoc />
    public async Task<(List<CausalTimelineEntry> Entries, int TotalCount)> GetCausalTimelinePageAsync(
        CausalTimelineSubject subject, int skip, int take)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();
        PrepareCausalTimelinePageCommand(command, subject, skip, take);

        var entries = new List<CausalTimelineEntry>();
        var total = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            total = (int)reader.GetInt64(TimelineEntryColumnCount);
            // The page joins onto a single row so the count arrives even when the page is past the end.
            if (!reader.IsDBNull(0))
                entries.Add(ReadTimelineEntry(reader));
        }

        return (entries, total);
    }

    /// <summary>
    /// Sets <paramref name="command"/> to read one page of <paramref name="subject"/>'s timeline: one row per item,
    /// newest first, each carrying the timeline's total, or a single row with no item and the total when the page is
    /// empty. Shared with the tests that assert the statement is read through its indexes.
    /// </summary>
    internal static void PrepareCausalTimelinePageCommand(DbCommand command, CausalTimelineSubject subject, int skip, int take)
    {
        // "timeline" is referenced twice, by the page and by the count, and so is materialised once. It holds only what
        // orders it; the entry's columns are read for the page's rows alone, however long the history.
        var idsSql = subject.Kind == CausalTimelineSubjectKind.ConnectedSystemObject
            ? ConnectedSystemObjectTimelineIdsSql
            : MetaverseObjectTimelineIdsSql;
        command.CommandText = idsSql + $"""
            ,
            timeline AS (
                SELECT r."Id" AS item_id, a."Executed" AS executed
                FROM ids
                JOIN "ActivityRunProfileExecutionItems" r ON r."Id" = ids.id
                JOIN "Activities" a ON a."Id" = r."ActivityId"
            )
            SELECT page.*, (SELECT count(*) FROM timeline) AS total
            FROM (SELECT 1) AS one
            LEFT JOIN LATERAL (
                SELECT {TimelineEntryColumnsSql}
                FROM (
                    SELECT item_id FROM timeline
                    ORDER BY executed DESC, item_id DESC
                    OFFSET @skip LIMIT @take
                ) AS shown
                JOIN "ActivityRunProfileExecutionItems" r ON r."Id" = shown.item_id
                {TimelineEntryJoinsSql}
                ORDER BY a."Executed" DESC, r."Id" DESC
            ) AS page ON TRUE
            """;

        AddParameter(command, "subject_id", subject.Id, NpgsqlDbType.Uuid);
        AddParameter(command, "skip", Math.Max(skip, 0), NpgsqlDbType.Integer);
        AddParameter(command, "take", Math.Max(take, 0), NpgsqlDbType.Integer);
        if (subject.Kind == CausalTimelineSubjectKind.ConnectedSystemObject)
            AddParameter(command, "deleted_change_type", (int)ObjectChangeType.Deleted, NpgsqlDbType.Integer);
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, CausalTimelineEntry>> GetCausalTimelineEntriesAsync(
        IReadOnlyCollection<Guid> runProfileExecutionItemIds)
    {
        ArgumentNullException.ThrowIfNull(runProfileExecutionItemIds);
        if (runProfileExecutionItemIds.Count == 0)
            return [];

        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TimelineEntryColumnsSql}
            FROM (SELECT DISTINCT unnest(@item_ids) AS id) AS ids
            JOIN "ActivityRunProfileExecutionItems" r ON r."Id" = ids.id
            {TimelineEntryJoinsSql}
            """;
        AddParameter(command, "item_ids", runProfileExecutionItemIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid);

        var entries = new Dictionary<Guid, CausalTimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var entry = ReadTimelineEntry(reader);
            entries[entry.RunProfileExecutionItemId] = entry;
        }

        return entries;
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, CausalSourceImportEvent>> GetLatestImportEventsForItemsAsync(
        IReadOnlyCollection<Guid> runProfileExecutionItemIds)
    {
        ArgumentNullException.ThrowIfNull(runProfileExecutionItemIds);
        if (runProfileExecutionItemIds.Count == 0)
            return [];

        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();
        PrepareLatestImportEventsCommand(command, runProfileExecutionItemIds.ToArray());

        var events = new Dictionary<Guid, CausalSourceImportEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events[reader.GetGuid(0)] = new CausalSourceImportEvent
            {
                RunProfileExecutionItemId = reader.GetGuid(1),
                ChangeType = (ObjectChangeType)reader.GetInt32(2),
                DisplayName = NullableString(reader, 3),
                Occurred = reader.GetDateTime(4).ToUniversalTime(),
                ConnectedSystemId = NullableInt(reader, 5),
                ConnectedSystemName = NullableString(reader, 6)
            };
        }

        return events;
    }

    /// <summary>
    /// Sets <paramref name="command"/> to find each item's source import: the walk's own rules
    /// (<see cref="GetLatestImportEventAsync"/>), per item, in one statement. By the record's id while the item still
    /// carries it, and by the item's external ID within its Activity's Connected System once a deletion has nulled it;
    /// an import-side change, never the item itself, no later than the item's own Activity; the latest, and between
    /// imports of one run the higher id. Shared with the tests that assert the statement is read through its indexes.
    /// </summary>
    internal static void PrepareLatestImportEventsCommand(DbCommand command, Guid[] runProfileExecutionItemIds)
    {
        command.CommandText = """
            SELECT f.item_id, imp.id, imp.change_type, imp.display_name, imp.executed, imp.cs_id, cs."Name"
            FROM (SELECT DISTINCT unnest(@item_ids) AS item_id) AS f
            JOIN "ActivityRunProfileExecutionItems" r ON r."Id" = f.item_id
            JOIN "Activities" a ON a."Id" = r."ActivityId"
            CROSS JOIN LATERAL (
                SELECT candidate.* FROM (
                    SELECT r2."Id" AS id, r2."ObjectChangeType" AS change_type, r2."DisplayNameSnapshot" AS display_name,
                           a2."Executed" AS executed, a2."ConnectedSystemId" AS cs_id
                    FROM "ActivityRunProfileExecutionItems" r2
                    JOIN "Activities" a2 ON a2."Id" = r2."ActivityId"
                    WHERE r."ConnectedSystemObjectId" IS NOT NULL
                      AND r2."ConnectedSystemObjectId" = r."ConnectedSystemObjectId"
                      AND r2."Id" <> r."Id" AND r2."ObjectChangeType" = ANY(@import_change_types)
                      AND a2."Executed" <= a."Executed"

                    UNION ALL

                    SELECT r2."Id", r2."ObjectChangeType", r2."DisplayNameSnapshot", a2."Executed", a2."ConnectedSystemId"
                    FROM "ActivityRunProfileExecutionItems" r2
                    JOIN "Activities" a2 ON a2."Id" = r2."ActivityId"
                    WHERE r."ConnectedSystemObjectId" IS NULL AND a."ConnectedSystemId" IS NOT NULL
                      AND r."ExternalIdSnapshot" IS NOT NULL AND r."ExternalIdSnapshot" <> ''
                      AND r2."ExternalIdSnapshot" = r."ExternalIdSnapshot" AND a2."ConnectedSystemId" = a."ConnectedSystemId"
                      AND r2."Id" <> r."Id" AND r2."ObjectChangeType" = ANY(@import_change_types)
                      AND a2."Executed" <= a."Executed"
                ) AS candidate
                ORDER BY candidate.executed DESC, candidate.id DESC
                LIMIT 1
            ) AS imp
            LEFT JOIN "ConnectedSystems" cs ON cs."Id" = imp.cs_id
            """;

        AddParameter(command, "item_ids", runProfileExecutionItemIds, NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        AddParameter(command, "import_change_types", SourceImportChangeTypes.Select(t => (int)t).ToArray(),
            NpgsqlDbType.Array | NpgsqlDbType.Integer);
    }

    /// <summary>
    /// Reads one row in the order of <see cref="TimelineEntryColumnsSql"/>.
    /// </summary>
    private static CausalTimelineEntry ReadTimelineEntry(DbDataReader reader) => new()
    {
        RunProfileExecutionItemId = reader.GetGuid(0),
        ActivityId = reader.GetGuid(1),
        Executed = reader.GetDateTime(2).ToUniversalTime(),
        ActivityTargetType = (ActivityTargetType)reader.GetInt32(3),
        ActivityTargetName = NullableString(reader, 4),
        ActivityTargetContext = NullableString(reader, 5),
        ConnectedSystemId = NullableInt(reader, 6),
        RunType = reader.IsDBNull(7) ? null : (ConnectedSystemRunType)reader.GetInt32(7),
        ObjectChangeType = (ObjectChangeType)reader.GetInt32(8),
        OutcomeSummary = NullableString(reader, 9),
        ErrorType = reader.IsDBNull(10) ? null : (ActivityRunProfileExecutionItemErrorType)reader.GetInt32(10),
        DisplayName = NullableString(reader, 11),
        ObjectType = NullableString(reader, 12),
        ConnectedSystemObjectId = NullableGuid(reader, 13),
        ExternalId = NullableString(reader, 14),
        MetaverseObjectId = NullableGuid(reader, 15)
    };
}
