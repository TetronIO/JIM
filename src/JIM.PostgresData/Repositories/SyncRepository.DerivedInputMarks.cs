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
        FixUpTrackedDerivedInputChangePending(
            cso => cso.MetaverseObjectId.HasValue && markSet.Contains(new DerivedInputChangeMark(cso.MetaverseObjectId.Value, cso.ConnectedSystemId)),
            value: true);

        return newlyMarked;
    }

    public async Task<int> ClearConnectedSystemObjectDerivedInputChangePendingAsync(IReadOnlyCollection<DerivedInputChangeClear> clears)
    {
        if (clears.Count == 0)
            return 0;

        // Clear only rows whose xmin still equals the version the run read when it loaded the object. A row whose
        // xmin has moved was written since: most importantly re-marked by another system's run, whose input change
        // this run's evaluation may not have seen. Leaving that row marked is fail-safe; the next run re-evaluates it
        // (and a row rewritten for any other reason, such as this run's own join update, simply converges one run
        // later, since that write does not recur). Only rows actually cleared are returned and fixed up.
        const string sql = """
            UPDATE "ConnectedSystemObjects" AS cso
            SET "DerivedInputChangePending" = false
            FROM unnest(@ids, @seenRowVersions) AS c("Id", "SeenRowVersion")
            WHERE cso."Id" = c."Id"
              AND cso.xmin = c."SeenRowVersion"
              AND cso."DerivedInputChangePending"
            RETURNING cso."Id"
            """;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = clears.Select(c => c.ConnectedSystemObjectId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("seenRowVersions", NpgsqlDbType.Array | NpgsqlDbType.Xid)
            { Value = clears.Select(c => c.SeenRowVersion).ToArray() });

        var clearedIds = new HashSet<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                clearedIds.Add(reader.GetGuid(0));
        }

        FixUpTrackedDerivedInputChangePending(cso => clearedIds.Contains(cso.Id), value: false);

        return clearedIds.Count;
    }

    /// <summary>
    /// Brings tracked Connected System Objects matching <paramref name="predicate"/> into line with a raw write of
    /// <c>DerivedInputChangePending</c>: current and original value both set, so the property reads as persisted
    /// rather than as a pending change, and a later whole-entity save writes the value the database already holds.
    /// Change detection is suppressed while enumerating, for the reason <see cref="DetachTrackedEntities{T}"/> gives.
    /// </summary>
    private void FixUpTrackedDerivedInputChangePending(Func<ConnectedSystemObject, bool> predicate, bool value)
    {
        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<ConnectedSystemObject>()
                .Where(e => e.State != EntityState.Detached && e.State != EntityState.Added && predicate(e.Entity))
                .ToList();

            foreach (var property in entries.Select(entry => entry.Property(cso => cso.DerivedInputChangePending)))
            {
                property.CurrentValue = value;
                property.OriginalValue = value;
                property.IsModified = false;
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }
}
