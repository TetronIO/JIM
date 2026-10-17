// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The Metaverse-Derived Attribute Flow mark (#1750, "Position 2"): <c>ConnectedSystemObjects.DerivedInputChangePending</c>,
/// set in bulk on a hosting system's joined objects when another system changes a derived flow's input, and cleared
/// in bulk once the hosting system has re-evaluated them. Both are deliberately narrow, single-column statements
/// (exempt from the bulk column list rule, like the ScopeReviewPending flag's), and both fix up tracked instances:
/// the worker's context lives for the whole run, and a whole-entity save of a stale tracked instance would silently
/// undo the write.
/// <para>
/// The two statements cooperate through the row version (<c>xmin</c>) so that runs of different systems in parallel
/// cannot lose a mark. The mark writes every matched row, already-marked ones included, so each mark moves the row's
/// xmin; the clear applies only where the xmin still equals the version the hosting system's run read when it loaded
/// the object. A mark set after that read, which the run's evaluation therefore may not have seen, survives the clear
/// and the object is re-evaluated next run.
/// </para>
/// <para>
/// The clear is shared with the synchronisation-pending mark (#2092): <see cref="ClearConnectedSystemObjectMarksAsync"/>
/// clears either mark or both in one statement under the same guard, so clearing one never moves the row version the
/// other's clear depends on.
/// </para>
/// </summary>
public partial class SyncRepository
{
    public async Task<int> MarkConnectedSystemObjectsDerivedInputChangePendingAsync(IReadOnlyCollection<DerivedInputChangeMark> marks)
    {
        if (marks.Count == 0)
            return 0;

        var distinct = marks.Distinct().ToList();

        // One statement for the page, joined on (ConnectedSystemId, MetaverseObjectId), which the unique join index
        // IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique serves. Every matched row is written,
        // ALREADY-MARKED ROWS INCLUDED: the write is what moves the row's xmin, and the moved xmin is what stops a
        // hosting-system run that loaded the object before this mark from clearing it (see the clear below). Skipping
        // already-marked rows would let a concurrent clear wipe a change its run never evaluated. The count of rows
        // that were not already marked is taken from the pre-update snapshot in the same statement, for the summary.
        const string sql = """
            WITH marks AS (
                SELECT DISTINCT m."MetaverseObjectId", m."ConnectedSystemId"
                FROM unnest(@metaverseObjectIds, @connectedSystemIds) AS m("MetaverseObjectId", "ConnectedSystemId")
            ),
            targets AS (
                SELECT cso."Id", cso."DerivedInputChangePending" AS "WasMarked"
                FROM "ConnectedSystemObjects" AS cso
                JOIN marks ON cso."MetaverseObjectId" = marks."MetaverseObjectId"
                          AND cso."ConnectedSystemId" = marks."ConnectedSystemId"
            ),
            updated AS (
                UPDATE "ConnectedSystemObjects" AS cso
                SET "DerivedInputChangePending" = true
                FROM targets
                WHERE cso."Id" = targets."Id"
                RETURNING targets."WasMarked"
            )
            SELECT count(*) FILTER (WHERE NOT "WasMarked")::integer FROM updated
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("metaverseObjectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = distinct.Select(m => m.MetaverseObjectId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("connectedSystemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            { Value = distinct.Select(m => m.ConnectedSystemId).ToArray() });
        var newlyMarked = (int)(await command.ExecuteScalarAsync() ?? 0);

        var markSet = distinct.ToHashSet();
        ConnectedSystemObjectMarkFixUp.Apply(_context,
            cso => cso.MetaverseObjectId.HasValue && markSet.Contains(new DerivedInputChangeMark(cso.MetaverseObjectId.Value, cso.ConnectedSystemId)),
            cso => cso.DerivedInputChangePending, value: true);

        return newlyMarked;
    }

    public async Task<ConnectedSystemObjectMarkClearResult> ClearConnectedSystemObjectMarksAsync(IReadOnlyCollection<ConnectedSystemObjectMarkClear> clears)
    {
        if (clears.Count == 0)
            return new ConnectedSystemObjectMarkClearResult(0, 0);

        // One statement for the page, clearing each mark only where asked, and only on rows whose xmin still equals the
        // version the run read when it loaded the object. A row whose xmin has moved was written since: an import
        // committing a further change (#2092), or another system's run re-marking a derived input (#1750), neither of
        // which this run's processing saw. Leaving that row marked is fail-safe; the next run processes it. Because the
        // guard matched, the row is exactly as loaded, so the marks the request names are the marks the row carries and
        // the returned flags say precisely what was cleared.
        //
        // The clear never waits for a row lock (SKIP LOCKED). A row another transaction holds is being written, an
        // import mid-commit say, which will move its row version, so skipping it is the guard's own outcome: it stays
        // marked. Waiting instead deadlocked against a concurrent import's update transaction, each holding rows the
        // other wanted (found at runtime, #2092). Locking in Id order before updating also keeps the statement's own
        // lock order deterministic.
        const string sql = """
            WITH c AS (
                SELECT * FROM unnest(@ids, @seenRowVersions, @clearSynchronisationPending, @clearDerivedInputChangePending)
                    AS c("Id", "SeenRowVersion", "ClearSynchronisationPending", "ClearDerivedInputChangePending")
            ),
            locked AS (
                SELECT cso."Id"
                FROM "ConnectedSystemObjects" AS cso
                JOIN c ON cso."Id" = c."Id"
                WHERE cso.xmin = c."SeenRowVersion"
                  AND ((c."ClearSynchronisationPending" AND cso."SynchronisationPending")
                       OR (c."ClearDerivedInputChangePending" AND cso."DerivedInputChangePending"))
                ORDER BY cso."Id"
                FOR NO KEY UPDATE OF cso SKIP LOCKED
            )
            UPDATE "ConnectedSystemObjects" AS cso
            SET "SynchronisationPending" = CASE WHEN c."ClearSynchronisationPending" THEN false ELSE cso."SynchronisationPending" END,
                "DerivedInputChangePending" = CASE WHEN c."ClearDerivedInputChangePending" THEN false ELSE cso."DerivedInputChangePending" END
            FROM c
            JOIN locked ON locked."Id" = c."Id"
            WHERE cso."Id" = c."Id"
              AND cso.xmin = c."SeenRowVersion"
            RETURNING cso."Id", c."ClearSynchronisationPending", c."ClearDerivedInputChangePending"
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = clears.Select(c => c.ConnectedSystemObjectId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("seenRowVersions", NpgsqlDbType.Array | NpgsqlDbType.Xid)
            { Value = clears.Select(c => c.SeenRowVersion).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("clearSynchronisationPending", NpgsqlDbType.Array | NpgsqlDbType.Boolean)
            { Value = clears.Select(c => c.SynchronisationPending).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("clearDerivedInputChangePending", NpgsqlDbType.Array | NpgsqlDbType.Boolean)
            { Value = clears.Select(c => c.DerivedInputChangePending).ToArray() });

        var synchronisationPendingCleared = new HashSet<Guid>();
        var derivedInputCleared = new HashSet<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                if (reader.GetBoolean(1))
                    synchronisationPendingCleared.Add(id);
                if (reader.GetBoolean(2))
                    derivedInputCleared.Add(id);
            }
        }

        ConnectedSystemObjectMarkFixUp.Apply(_context, cso => synchronisationPendingCleared.Contains(cso.Id), cso => cso.SynchronisationPending, value: false);
        ConnectedSystemObjectMarkFixUp.Apply(_context, cso => derivedInputCleared.Contains(cso.Id), cso => cso.DerivedInputChangePending, value: false);

        return new ConnectedSystemObjectMarkClearResult(synchronisationPendingCleared.Count, derivedInputCleared.Count);
    }
}
