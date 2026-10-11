// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The downward causal walk's reads (#1811): what a Run Profile Execution Item went on to cause.
/// </summary>
/// <remarks>
/// Raw SQL rather than EF projection because a level can be large (one deletion can remove a reference from thousands
/// of Groups) and the walk must count and group it in the database: a cohort arrives as one row carrying its count,
/// its span, its first page of members and the ids that carry the walk on, never as thousands of materialised items.
/// Every enum value is a parameter, so a renumbered or renamed member cannot silently change what the SQL matches.
/// </remarks>
public partial class ActivityRepository
{
    /// <summary>
    /// The shared front of both reads: the frontier, its items' own facts, and every hop out of them, one row per
    /// consequence member (deduplicated per cohort), as the CTEs <c>fi</c>, <c>hops</c> and <c>members</c>.
    /// </summary>
    /// <remarks>
    /// The hops, in order:
    /// <list type="number">
    /// <item><description>Recorded edges whose cause is a frontier item. These continue the walk.</description></item>
    /// <item><description>The confirming import of an export a frontier item executed, paired through the Pending
    /// Export the export's queueing edge and the confirmation's edge both name. A confirmation ends its branch.</description></item>
    /// <item><description>The first synchronisation of the same Connected System Object after a frontier import, by
    /// object id, or by external ID and system once a deletion has nulled the id. Continues the walk.</description></item>
    /// <item><description>Pending Exports a frontier item staged that still exist and no export run has executed:
    /// waiting for an export, a retry, or an administrator.</description></item>
    /// <item><description>Pending Exports a frontier item executed that still exist with no confirmation: waiting for
    /// the import that confirms them.</description></item>
    /// <item><description>A deletion a frontier item scheduled that the Metaverse Object still carries; a schedule
    /// cancelled or replaced since names a different item (or none), so it is not reported as pending.</description></item>
    /// </list>
    /// </remarks>
    private const string ConsequenceHopsSql = """
        WITH frontier AS (
            SELECT f.item_id, f.parent
            FROM unnest(@item_ids, @parents) AS f(item_id, parent)
        ),
        fi AS (
            SELECT f.item_id, f.parent, r."ObjectChangeType" AS change_type, r."ConnectedSystemObjectId" AS cso_id,
                   r."ExternalIdSnapshot" AS external_id, r."DisplayNameSnapshot" AS display_name,
                   r."DeletionPolicySnapshotJson" AS policy_json,
                   a."ConnectedSystemId" AS cs_id, a."Executed" AS executed,
                   (r."ObjectChangeType" = ANY(@import_change_types)
                       AND a."ConnectedSystemRunType" = ANY(@import_run_types)
                       -- An import that only confirmed an export found what JIM wrote: no Synchronisation acts on that.
                       AND NOT (EXISTS (SELECT 1 FROM "ActivityRunProfileExecutionItemSyncOutcomes" co
                                        WHERE co."ActivityRunProfileExecutionItemId" = r."Id" AND co."OutcomeType" = @export_confirmed)
                                AND NOT EXISTS (SELECT 1 FROM "ActivityRunProfileExecutionItemSyncOutcomes" oo
                                                WHERE oo."ActivityRunProfileExecutionItemId" = r."Id" AND oo."OutcomeType" <> @export_confirmed))) AS is_import
            FROM frontier f
            JOIN "ActivityRunProfileExecutionItems" r ON r."Id" = f.item_id
            JOIN "Activities" a ON a."Id" = r."ActivityId"
        ),
        hops AS (
            SELECT fi.item_id AS cause_item_id, fi.parent, @hop_edge AS hop_kind,
                   e."EdgeType" AS edge_type, e."ReasonCode" AS reason_code,
                   e."ConnectedSystemId" AS cs_id, e."ConnectedSystemName" AS cs_name,
                   e."SyncRuleId" AS rule_id, e."SyncRuleName" AS rule_name,
                   er."ObjectTypeSnapshot" AS object_type, e."EffectAttributeName" AS attribute_name,
                   er."ObjectChangeType" AS change_type, er."OutcomeSummary" AS outcome_summary, NULL::integer AS pe_status,
                   er."Id" AS member_item_id, NULL::uuid AS member_pe_id, NULL::uuid AS member_mvo_id,
                   er."DisplayNameSnapshot" AS display_name, ea."Executed" AS occurred, NULL::timestamptz AS due_at,
                   TRUE AS continues
            FROM fi
            JOIN "CausalEdges" e ON e."CauseRunProfileExecutionItemId" = fi.item_id
            JOIN "ActivityRunProfileExecutionItems" er ON er."Id" = e."EffectRunProfileExecutionItemId"
            JOIN "Activities" ea ON ea."Id" = er."ActivityId"

            UNION ALL

            SELECT fi.item_id, fi.parent, @hop_confirmation,
                   c."EdgeType", c."ReasonCode", c."ConnectedSystemId", c."ConnectedSystemName", c."SyncRuleId", c."SyncRuleName",
                   cr."ObjectTypeSnapshot", c."EffectAttributeName", cr."ObjectChangeType", cr."OutcomeSummary", NULL,
                   cr."Id", NULL, NULL, cr."DisplayNameSnapshot", ca."Executed", NULL, FALSE
            FROM fi
            JOIN "CausalEdges" q ON q."EffectRunProfileExecutionItemId" = fi.item_id
                AND q."EdgeType" = @queueing_edge AND q."CausePendingExportId" IS NOT NULL
            JOIN "CausalEdges" c ON c."CausePendingExportId" = q."CausePendingExportId" AND c."EdgeType" = @confirmation_edge
            JOIN "ActivityRunProfileExecutionItems" cr ON cr."Id" = c."EffectRunProfileExecutionItemId"
            JOIN "Activities" ca ON ca."Id" = cr."ActivityId"

            UNION ALL

            SELECT fi.item_id, fi.parent, @hop_next_sync,
                   NULL, @reason_not_set, ns.cs_id, ncs."Name", NULL, NULL,
                   ns.object_type, NULL, ns.change_type, ns.outcome_summary, NULL,
                   ns.id, NULL, NULL, ns.display_name, ns.executed, NULL, TRUE
            FROM fi
            CROSS JOIN LATERAL (
                SELECT candidate.* FROM (
                    SELECT r2."Id" AS id, r2."ObjectTypeSnapshot" AS object_type, r2."ObjectChangeType" AS change_type,
                           r2."OutcomeSummary" AS outcome_summary, r2."DisplayNameSnapshot" AS display_name,
                           a2."Executed" AS executed, a2."ConnectedSystemId" AS cs_id
                    FROM "ActivityRunProfileExecutionItems" r2
                    JOIN "Activities" a2 ON a2."Id" = r2."ActivityId"
                    WHERE fi.is_import AND fi.cso_id IS NOT NULL AND r2."ConnectedSystemObjectId" = fi.cso_id
                      AND a2."ConnectedSystemRunType" = ANY(@sync_run_types) AND a2."Executed" > fi.executed
                    UNION ALL
                    SELECT r2."Id", r2."ObjectTypeSnapshot", r2."ObjectChangeType", r2."OutcomeSummary", r2."DisplayNameSnapshot",
                           a2."Executed", a2."ConnectedSystemId"
                    FROM "ActivityRunProfileExecutionItems" r2
                    JOIN "Activities" a2 ON a2."Id" = r2."ActivityId"
                    WHERE fi.is_import AND fi.cso_id IS NULL AND fi.external_id IS NOT NULL AND r2."ExternalIdSnapshot" = fi.external_id
                      AND a2."ConnectedSystemId" = fi.cs_id
                      AND a2."ConnectedSystemRunType" = ANY(@sync_run_types) AND a2."Executed" > fi.executed
                ) AS candidate
                ORDER BY candidate.executed, candidate.id
                LIMIT 1
            ) AS ns
            LEFT JOIN "ConnectedSystems" ncs ON ncs."Id" = ns.cs_id
            WHERE fi.is_import

            UNION ALL

            SELECT fi.item_id, fi.parent, @hop_pending_export,
                   NULL, @reason_not_set, pe."ConnectedSystemId", pcs."Name", NULL, NULL,
                   NULL, NULL, NULL, NULL, pe."Status",
                   NULL, pe."Id", NULL, fi.display_name, pe."CreatedAt", pe."NextRetryAt", FALSE
            FROM fi
            JOIN "ActivityRunProfileExecutionItemSyncOutcomes" o ON o."ActivityRunProfileExecutionItemId" = fi.item_id
                AND o."OutcomeType" = ANY(@pending_export_outcome_types)
            JOIN "PendingExports" pe ON pe."Id" = o."TargetEntityId"
            LEFT JOIN "ConnectedSystems" pcs ON pcs."Id" = pe."ConnectedSystemId"
            WHERE NOT EXISTS (
                SELECT 1 FROM "CausalEdges" x
                WHERE x."CausePendingExportId" = pe."Id" AND x."EdgeType" = @queueing_edge)

            UNION ALL

            SELECT fi.item_id, fi.parent, @hop_pending_export,
                   NULL, @reason_not_set, pe."ConnectedSystemId", pcs."Name", NULL, NULL,
                   NULL, NULL, NULL, NULL, pe."Status",
                   NULL, pe."Id", NULL, fi.display_name, pe."CreatedAt", pe."NextRetryAt", FALSE
            FROM fi
            JOIN "CausalEdges" q ON q."EffectRunProfileExecutionItemId" = fi.item_id AND q."EdgeType" = @queueing_edge
            JOIN "PendingExports" pe ON pe."Id" = q."CausePendingExportId"
            LEFT JOIN "ConnectedSystems" pcs ON pcs."Id" = pe."ConnectedSystemId"
            WHERE NOT EXISTS (
                SELECT 1 FROM "CausalEdges" x
                WHERE x."CausePendingExportId" = pe."Id" AND x."EdgeType" = @confirmation_edge)

            UNION ALL

            SELECT fi.item_id, fi.parent, @hop_scheduled_deletion,
                   NULL, @reason_not_set, NULL, NULL, NULL, NULL,
                   NULL, NULL, NULL, NULL, NULL,
                   NULL, NULL, m."Id", COALESCE(o."TargetEntityDescription", fi.display_name), fi.executed,
                   CASE WHEN fi.policy_json IS JSON OBJECT
                        THEN (fi.policy_json::jsonb ->> 'deletionEligibleDate')::timestamptz END,
                   FALSE
            FROM fi
            JOIN "ActivityRunProfileExecutionItemSyncOutcomes" o ON o."ActivityRunProfileExecutionItemId" = fi.item_id
                AND o."OutcomeType" = @scheduled_outcome_type
            JOIN "MetaverseObjects" m ON m."Id" = o."TargetEntityId"
                AND m."DeletionScheduledByRunProfileExecutionItemId" = fi.item_id
        ),
        members AS (
            SELECT DISTINCT ON (parent, hop_kind, edge_type, reason_code, cs_id, rule_id, object_type, attribute_name,
                                change_type, outcome_summary, pe_status, member_key)
                   *
            FROM (SELECT hops.*, COALESCE(member_item_id, member_pe_id, member_mvo_id) AS member_key FROM hops) AS keyed
            ORDER BY parent, hop_kind, edge_type, reason_code, cs_id, rule_id, object_type, attribute_name,
                     change_type, outcome_summary, pe_status, member_key, occurred
        )
        """;

    /// <inheritdoc />
    public async Task<CausalConsequenceLevel> GetCausalConsequenceLevelAsync(
        IReadOnlyList<CausalConsequenceFrontierMember> frontier, int memberPageSize, int continuingItemLimit)
    {
        ArgumentNullException.ThrowIfNull(frontier);
        if (frontier.Count == 0)
            return new CausalConsequenceLevel();

        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();

        // Members are ordered earliest first, then by name: everything one run did shares its Activity's time, and a
        // name is a tie-break a reader can follow where an id is not.
        // One statement: the cohorts (row_kind 0) and the frontier items that led nowhere (row_kind 1), sharing the
        // hop CTEs, so a level is exactly one round trip. "hops" is referenced twice and so is materialised once.
        command.CommandText = ConsequenceHopsSql + """
            ,
            cohorts AS (
                SELECT parent, hop_kind, edge_type, reason_code, cs_id, rule_id, object_type, attribute_name,
                       change_type, outcome_summary, pe_status,
                       max(cs_name) AS cs_name, max(rule_name) AS rule_name,
                       count(*) AS member_count, min(occurred) AS first_occurred, max(occurred) AS last_occurred,
                       min(due_at) AS due_at,
                       (array_agg(member_item_id ORDER BY occurred, display_name, member_key))[1:@page] AS page_item_ids,
                       (array_agg(member_pe_id ORDER BY occurred, display_name, member_key))[1:@page] AS page_pe_ids,
                       (array_agg(member_mvo_id ORDER BY occurred, display_name, member_key))[1:@page] AS page_mvo_ids,
                       (array_agg(display_name ORDER BY occurred, display_name, member_key))[1:@page] AS page_names,
                       (array_agg(occurred ORDER BY occurred, display_name, member_key))[1:@page] AS page_occurred,
                       (array_agg(due_at ORDER BY occurred, display_name, member_key))[1:@page] AS page_due,
                       count(*) FILTER (WHERE continues) AS continuing_count,
                       (array_agg(member_item_id ORDER BY occurred, display_name, member_key) FILTER (WHERE continues))[1:@limit] AS continuing_ids
                FROM members
                GROUP BY parent, hop_kind, edge_type, reason_code, cs_id, rule_id, object_type, attribute_name,
                         change_type, outcome_summary, pe_status
            ),
            leaves AS (
                SELECT fi.parent,
                       CASE WHEN NOT fi.is_import THEN @ending_no_further
                            WHEN EXISTS (
                                SELECT 1 FROM "Activities" sa
                                WHERE sa."ConnectedSystemId" = fi.cs_id
                                  AND sa."ConnectedSystemRunType" = ANY(@sync_run_types)
                                  AND sa."Executed" > fi.executed) THEN @ending_nothing_followed
                            ELSE @ending_waiting_for_sync END AS ending,
                       CASE WHEN fi.is_import THEN fi.cs_id END AS cs_id,
                       count(*) AS member_count
                FROM fi
                WHERE NOT EXISTS (SELECT 1 FROM hops h WHERE h.cause_item_id = fi.item_id AND h.parent = fi.parent)
                GROUP BY 1, 2, 3
            )
            SELECT 0 AS row_kind, c.parent, c.hop_kind, c.edge_type, c.reason_code, c.cs_id, c.rule_id, c.object_type,
                   c.attribute_name, c.change_type, c.outcome_summary, c.pe_status, c.cs_name, c.rule_name,
                   (SELECT t."PluralName" FROM "MetaverseObjectTypes" t WHERE t."Name" = c.object_type LIMIT 1) AS plural_name,
                   c.member_count, c.first_occurred, c.last_occurred, c.due_at,
                   c.page_item_ids, c.page_pe_ids, c.page_mvo_ids, c.page_names, c.page_occurred, c.page_due,
                   c.continuing_count, c.continuing_ids, NULL::integer AS ending
            FROM cohorts c
            UNION ALL
            SELECT 1, l.parent, NULL, NULL, NULL, l.cs_id, NULL, NULL, NULL, NULL, NULL, NULL, lcs."Name", NULL, NULL,
                   l.member_count, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, l.ending
            FROM leaves l
            LEFT JOIN "ConnectedSystems" lcs ON lcs."Id" = l.cs_id
            ORDER BY 1, 2, 17, 3
            """;

        AddFrontierParameters(command, frontier.Select(m => m.RunProfileExecutionItemId).ToArray(),
            frontier.Select(m => m.ParentCohortOrdinal).ToArray());
        AddParameter(command, "page", Math.Max(memberPageSize, 0), NpgsqlDbType.Integer);
        AddParameter(command, "limit", Math.Max(continuingItemLimit, 0), NpgsqlDbType.Integer);
        AddParameter(command, "ending_no_further", (int)CausalConsequenceEnding.NoFurtherConsequences, NpgsqlDbType.Integer);
        AddParameter(command, "ending_nothing_followed", (int)CausalConsequenceEnding.NothingFollowed, NpgsqlDbType.Integer);
        AddParameter(command, "ending_waiting_for_sync", (int)CausalConsequenceEnding.WaitingForSynchronisation, NpgsqlDbType.Integer);

        var level = new CausalConsequenceLevel();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetInt32(0) == 1)
            {
                level.Leaves.Add(new CausalConsequenceLeafRow
                {
                    ParentCohortOrdinal = reader.GetInt32(1),
                    Ending = (CausalConsequenceEnding)reader.GetInt32(27),
                    ConnectedSystemId = NullableInt(reader, 5),
                    ConnectedSystemName = NullableString(reader, 12),
                    MemberCount = (int)reader.GetInt64(15)
                });
                continue;
            }

            level.Cohorts.Add(ReadCohort(reader, level.Cohorts.Count));
        }

        return level;
    }

    /// <inheritdoc />
    /// <remarks>
    /// EF rather than raw SQL: one item per cohort, so the set is small, and the portal renders the entity as it would
    /// any item of its own.
    /// </remarks>
    public async Task<Dictionary<Guid, ActivityRunProfileExecutionItem>> GetCausalConsequenceRepresentativesAsync(
        IReadOnlyCollection<Guid> runProfileExecutionItemIds)
    {
        ArgumentNullException.ThrowIfNull(runProfileExecutionItemIds);
        if (runProfileExecutionItemIds.Count == 0)
            return [];

        var ids = runProfileExecutionItemIds.Distinct().ToList();
        var items = await Repository.Database.ActivityRunProfileExecutionItems
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Activity)
            .Include(i => i.SyncOutcomes)
            .Where(i => ids.Contains(i.Id))
            .ToListAsync();
        return items.ToDictionary(i => i.Id);
    }

    /// <inheritdoc />
    public async Task<(List<CausalConsequenceMember> Members, int TotalCount)> GetCausalConsequenceCohortMembersAsync(
        IReadOnlyList<Guid> frontier, CausalConsequenceCohortTuple tuple, int skip, int take, string? search)
    {
        ArgumentNullException.ThrowIfNull(frontier);
        ArgumentNullException.ThrowIfNull(tuple);
        if (frontier.Count == 0)
            return ([], 0);

        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();

        // The frontier is one parent cohort's members, so every row shares parent 0 and the cohort is picked out by
        // its values alone. strpos rather than LIKE, so the search text needs no escaping.
        command.CommandText = ConsequenceHopsSql + """
            SELECT member_item_id, member_pe_id, member_mvo_id, display_name, occurred, due_at, count(*) OVER () AS total
            FROM members
            WHERE hop_kind = @t_hop_kind
              AND edge_type IS NOT DISTINCT FROM @t_edge_type
              AND reason_code IS NOT DISTINCT FROM @t_reason_code
              AND cs_id IS NOT DISTINCT FROM @t_cs_id
              AND rule_id IS NOT DISTINCT FROM @t_rule_id
              AND object_type IS NOT DISTINCT FROM @t_object_type
              AND attribute_name IS NOT DISTINCT FROM @t_attribute_name
              AND change_type IS NOT DISTINCT FROM @t_change_type
              AND outcome_summary IS NOT DISTINCT FROM @t_outcome_summary
              AND pe_status IS NOT DISTINCT FROM @t_pe_status
              AND (@search::text IS NULL OR strpos(lower(display_name), lower(@search::text)) > 0)
            ORDER BY occurred, display_name, member_key
            OFFSET @skip LIMIT @take
            """;

        AddFrontierParameters(command, frontier.ToArray(), new int[frontier.Count]);
        AddParameter(command, "t_hop_kind", (int)tuple.HopKind, NpgsqlDbType.Integer);
        AddParameter(command, "t_edge_type", tuple.EdgeType.HasValue ? (int)tuple.EdgeType.Value : null, NpgsqlDbType.Integer);
        AddParameter(command, "t_reason_code", (int)tuple.ReasonCode, NpgsqlDbType.Integer);
        AddParameter(command, "t_cs_id", tuple.ConnectedSystemId, NpgsqlDbType.Integer);
        AddParameter(command, "t_rule_id", tuple.SyncRuleId, NpgsqlDbType.Integer);
        AddParameter(command, "t_object_type", tuple.ObjectTypeName, NpgsqlDbType.Text);
        AddParameter(command, "t_attribute_name", tuple.AttributeName, NpgsqlDbType.Text);
        AddParameter(command, "t_change_type", tuple.ChangeType.HasValue ? (int)tuple.ChangeType.Value : null, NpgsqlDbType.Integer);
        AddParameter(command, "t_outcome_summary", tuple.OutcomeSummary, NpgsqlDbType.Text);
        AddParameter(command, "t_pe_status", tuple.PendingExportStatus.HasValue ? (int)tuple.PendingExportStatus.Value : null, NpgsqlDbType.Integer);
        AddParameter(command, "search", string.IsNullOrWhiteSpace(search) ? null : search.Trim(), NpgsqlDbType.Text);
        AddParameter(command, "skip", Math.Max(skip, 0), NpgsqlDbType.Integer);
        AddParameter(command, "take", Math.Max(take, 0), NpgsqlDbType.Integer);

        var members = new List<CausalConsequenceMember>();
        var total = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            members.Add(new CausalConsequenceMember
            {
                RunProfileExecutionItemId = NullableGuid(reader, 0),
                PendingExportId = NullableGuid(reader, 1),
                MetaverseObjectId = NullableGuid(reader, 2),
                DisplayName = NullableString(reader, 3),
                Occurred = reader.GetDateTime(4).ToUniversalTime(),
                DueAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToUniversalTime()
            });
            total = (int)reader.GetInt64(6);
        }

        // An empty page past the end still has a total; ask for it rather than reporting none.
        if (members.Count == 0 && skip > 0)
            total = (await GetCausalConsequenceCohortMembersAsync(frontier, tuple, 0, 1, search)).TotalCount;

        return (members, total);
    }

    /// <summary>
    /// Reads one cohort row of <see cref="GetCausalConsequenceLevelAsync"/>; the column positions are that
    /// statement's select list.
    /// </summary>
    private static CausalConsequenceCohortRow ReadCohort(DbDataReader reader, int ordinal)
    {
        var tuple = new CausalConsequenceCohortTuple(
            HopKind: (CausalConsequenceHopKind)reader.GetInt32(2),
            EdgeType: reader.IsDBNull(3) ? null : (CausalEdgeType)reader.GetInt32(3),
            ReasonCode: reader.IsDBNull(4) ? CausalReasonCode.NotSet : (CausalReasonCode)reader.GetInt32(4),
            ConnectedSystemId: NullableInt(reader, 5),
            SyncRuleId: NullableInt(reader, 6),
            ObjectTypeName: NullableString(reader, 7),
            AttributeName: NullableString(reader, 8),
            ChangeType: reader.IsDBNull(9) ? null : (ObjectChangeType)reader.GetInt32(9),
            OutcomeSummary: NullableString(reader, 10),
            PendingExportStatus: reader.IsDBNull(11) ? null : (PendingExportStatus)reader.GetInt32(11));

        var itemIds = reader.GetFieldValue<Guid?[]>(19);
        var pendingExportIds = reader.GetFieldValue<Guid?[]>(20);
        var metaverseObjectIds = reader.GetFieldValue<Guid?[]>(21);
        var names = reader.GetFieldValue<string?[]>(22);
        var occurred = reader.GetFieldValue<DateTime?[]>(23);
        var due = reader.GetFieldValue<DateTime?[]>(24);
        var members = new List<CausalConsequenceMember>(itemIds.Length);
        for (var i = 0; i < itemIds.Length; i++)
        {
            members.Add(new CausalConsequenceMember
            {
                RunProfileExecutionItemId = itemIds[i],
                PendingExportId = pendingExportIds[i],
                MetaverseObjectId = metaverseObjectIds[i],
                DisplayName = names[i],
                Occurred = occurred[i]?.ToUniversalTime() ?? default,
                DueAt = due[i]?.ToUniversalTime()
            });
        }

        return new CausalConsequenceCohortRow
        {
            Ordinal = ordinal,
            ParentCohortOrdinal = reader.GetInt32(1),
            Tuple = tuple,
            ConnectedSystemName = NullableString(reader, 12),
            SyncRuleName = NullableString(reader, 13),
            ObjectTypePluralName = NullableString(reader, 14),
            MemberCount = (int)reader.GetInt64(15),
            FirstOccurred = reader.GetDateTime(16).ToUniversalTime(),
            LastOccurred = reader.GetDateTime(17).ToUniversalTime(),
            DueAt = reader.IsDBNull(18) ? null : reader.GetDateTime(18).ToUniversalTime(),
            Members = members,
            ContinuingMemberCount = (int)reader.GetInt64(25),
            ContinuingItemIds = reader.IsDBNull(26) ? [] : reader.GetFieldValue<Guid[]>(26).ToList()
        };
    }

    /// <summary>
    /// Adds the frontier arrays and the enum values the hop CTEs match on.
    /// </summary>
    private static void AddFrontierParameters(DbCommand command, Guid[] itemIds, int[] parents)
    {
        AddParameter(command, "item_ids", itemIds, NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        AddParameter(command, "parents", parents, NpgsqlDbType.Array | NpgsqlDbType.Integer);
        AddParameter(command, "import_change_types",
            new[] { (int)ObjectChangeType.Added, (int)ObjectChangeType.Updated, (int)ObjectChangeType.Deleted },
            NpgsqlDbType.Array | NpgsqlDbType.Integer);
        AddParameter(command, "import_run_types",
            new[] { (int)ConnectedSystemRunType.FullImport, (int)ConnectedSystemRunType.DeltaImport },
            NpgsqlDbType.Array | NpgsqlDbType.Integer);
        AddParameter(command, "export_confirmed", (int)ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, NpgsqlDbType.Integer);
        AddParameter(command, "sync_run_types",
            new[] { (int)ConnectedSystemRunType.FullSynchronisation, (int)ConnectedSystemRunType.DeltaSynchronisation },
            NpgsqlDbType.Array | NpgsqlDbType.Integer);
        AddParameter(command, "pending_export_outcome_types",
            new[]
            {
                (int)ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                (int)ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
            },
            NpgsqlDbType.Array | NpgsqlDbType.Integer);
        AddParameter(command, "scheduled_outcome_type", (int)ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, NpgsqlDbType.Integer);
        AddParameter(command, "queueing_edge", (int)CausalEdgeType.PendingExportQueueingCausedExportExecution, NpgsqlDbType.Integer);
        AddParameter(command, "confirmation_edge", (int)CausalEdgeType.ExportCausedImportConfirmation, NpgsqlDbType.Integer);
        AddParameter(command, "reason_not_set", (int)CausalReasonCode.NotSet, NpgsqlDbType.Integer);
        AddParameter(command, "hop_edge", (int)CausalConsequenceHopKind.Edge, NpgsqlDbType.Integer);
        AddParameter(command, "hop_confirmation", (int)CausalConsequenceHopKind.Confirmation, NpgsqlDbType.Integer);
        AddParameter(command, "hop_next_sync", (int)CausalConsequenceHopKind.NextSynchronisation, NpgsqlDbType.Integer);
        AddParameter(command, "hop_pending_export", (int)CausalConsequenceHopKind.OutstandingPendingExport, NpgsqlDbType.Integer);
        AddParameter(command, "hop_scheduled_deletion", (int)CausalConsequenceHopKind.ScheduledDeletion, NpgsqlDbType.Integer);
    }

    private static void AddParameter(DbCommand command, string name, object? value, NpgsqlDbType type)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    }

    private static int? NullableInt(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid? NullableGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
}
