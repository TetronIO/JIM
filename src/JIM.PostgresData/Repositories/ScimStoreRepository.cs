// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Scim;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

public class ScimStoreRepository : IScimStoreRepository
{
    private PostgresDataRepository Repository { get; }

    internal ScimStoreRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    /// <remarks>
    /// <para>
    /// The lock is the Connected System's <c>ScimStoreStates</c> row, taken by the upsert that also assigns the change
    /// sequence, and it is taken first, before the session reads or writes anything. A Connected System's writes
    /// therefore run one at a time, end to end, and commit in sequence order, which is what lets an import trust a
    /// horizon. Taking it first rather than last costs some concurrency within one Connected System (writes to
    /// different systems never wait for each other) and buys a session that needs no other row locks and cannot
    /// deadlock with another session.
    /// </para>
    /// <para>
    /// Before that, a shared lock on the Connected System row: a write waits for a deletion of the Connected System
    /// that is in progress, and then finds no row and writes nothing, rather than racing the cascade that removes the
    /// store.
    /// </para>
    /// </remarks>
    public async Task<IScimStoreWriteSession?> BeginWriteAsync(int connectedSystemId, CancellationToken cancellationToken = default)
    {
        if (Repository.Database.Database.CurrentTransaction != null)
            throw new InvalidOperationException("A SCIM store write needs a transaction of its own; this context already has one.");

        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        NpgsqlTransaction? transaction = null;
        ScimStoreWriteSession? session = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var lockSystem = new NpgsqlCommand(
                """SELECT 1 FROM "ConnectedSystems" WHERE "Id" = @system FOR SHARE""", connection, transaction))
            {
                lockSystem.Parameters.AddWithValue("system", connectedSystemId);
                if (await lockSystem.ExecuteScalarAsync(cancellationToken) == null)
                    return null;
            }

            long sequence;
            await using (var takeLock = new NpgsqlCommand($"""
                INSERT INTO "ScimStoreStates" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.States)}) VALUES (@system, 1, 0)
                ON CONFLICT ("ConnectedSystemId") DO UPDATE SET "LastChangeSequence" = "ScimStoreStates"."LastChangeSequence" + 1
                RETURNING "LastChangeSequence"
                """, connection, transaction))
            {
                takeLock.Parameters.AddWithValue("system", connectedSystemId);
                sequence = (long)(await takeLock.ExecuteScalarAsync(cancellationToken))!;
            }

            // Taken once the lock is held, so it is never earlier than the previous write's, and truncated to the
            // microsecond PostgreSQL stores, so what the response reports is exactly what was written.
            var now = DateTime.UtcNow;
            var timestamp = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);

            session = new ScimStoreWriteSession(connection, transaction, lease, connectedSystemId, sequence, timestamp);
            return session;
        }
        finally
        {
            // Anything short of handing a session over (no such Connected System, a failure, a cancellation) releases
            // the transaction, and with it the locks, and the connection here; once handed over, the session owns them.
            if (session == null)
            {
                if (transaction != null)
                    await transaction.DisposeAsync();
                await lease.DisposeAsync();
            }
        }
    }

    public async Task<ScimStoreResource?> GetLiveResourceAsync(int connectedSystemId, int resourceTypeId, Guid id, CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        return await ScimStoreReads.LiveResourceAsync(connection, null, connectedSystemId, resourceTypeId, id, cancellationToken);
    }

    public async Task<IReadOnlyList<ScimStoreReferenceTarget>> GetReferencesAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        return await ScimStoreReads.ReferencesAsync(connection, null, sourceId, null, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>>> GetReferencesAsync(IReadOnlyCollection<Guid> sourceIds,
        CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        return await ScimStoreReads.ReferencesBySourceAsync(connection, null, sourceIds, cancellationToken);
    }

    /// <remarks>
    /// The count and the page are two statements of one command; under concurrent writes they can see different
    /// snapshots, which a client walking a store that is changing has to tolerate anyway. Rows come back in uuid order,
    /// which is PostgreSQL's byte order rather than .NET's: a cursor only ever compares in SQL, so the two never meet.
    /// </remarks>
    public async Task<ScimStorePage> ListLiveResourcesAsync(int connectedSystemId, ScimStoreQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Count);

        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand { Connection = connection };
        command.Parameters.AddWithValue("system", connectedSystemId);
        command.Parameters.AddWithValue("type", query.ResourceTypeId);

        var where = """r."ConnectedSystemId" = @system AND r."ResourceTypeId" = @type AND r."DeletedAt" IS NULL""";
        if (query.Filter != null)
            where += $" AND {ScimStoreFilterSql.Render(query.Filter, command)}";

        var sql = $"""SELECT count(*) FROM "ScimStoreResources" AS r WHERE {where};""";
        if (query.Count > 0)
        {
            var position = "";
            if (query.After is { } after)
            {
                command.Parameters.AddWithValue("after", after);
                position = """ AND r."Id" > @after""";
            }
            else
            {
                command.Parameters.AddWithValue("offset", (long)Math.Max(query.StartIndex, 1) - 1);
            }

            // One more than the page holds, to know whether another page follows.
            command.Parameters.AddWithValue("limit", query.Count + 1);
            var columns = string.Join(", ", ScimStoreColumns.Resources.Select(column => $"r.\"{column}\""));
            sql += $"""
                SELECT {columns}
                FROM "ScimStoreResources" AS r WHERE {where}{position}
                ORDER BY r."Id"{(query.After == null ? " OFFSET @offset" : "")} LIMIT @limit;
                """;
        }

        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var total = reader.GetInt64(0);

        var resources = new List<ScimStoreResource>();
        if (query.Count > 0 && await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                resources.Add(ScimStoreReads.ReadResource(reader));
        }

        var hasMore = resources.Count > query.Count;
        if (hasMore)
            resources.RemoveAt(resources.Count - 1);
        return new ScimStorePage { TotalResults = total, Resources = resources, HasMore = hasMore };
    }

    public async Task<ScimStoreImportPage> ReadForImportAsync(int connectedSystemId, ScimStoreImportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Count);
        if (query.ChangedAfter.HasValue != query.ChangedUpTo.HasValue)
            throw new ArgumentException("A Delta Import's read needs both ends of its change sequence range.", nameof(query));

        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand { Connection = connection };
        command.Parameters.AddWithValue("system", connectedSystemId);
        command.Parameters.AddWithValue("limit", query.Count + 1);

        // A Full Import reads every live resource, whatever its sequence: stopping at the horizon would make a resource
        // changed during the walk look absent. A Delta Import reads the range, tombstones included.
        var where = """r."ConnectedSystemId" = @system""";
        if (query.ChangedAfter is { } changedAfter)
        {
            command.Parameters.AddWithValue("changedAfter", changedAfter);
            command.Parameters.AddWithValue("changedUpTo", query.ChangedUpTo!.Value);
            where += """ AND r."ChangeSequence" > @changedAfter AND r."ChangeSequence" <= @changedUpTo""";
        }
        else
        {
            where += """ AND r."DeletedAt" IS NULL""";
        }

        if (query.After is { } after)
        {
            command.Parameters.AddWithValue("after", after);
            where += """ AND r."Id" > @after""";
        }

        var columns = string.Join(", ", ScimStoreColumns.Resources.Select(column => $"r.\"{column}\""));
        command.CommandText = $"""SELECT {columns} FROM "ScimStoreResources" AS r WHERE {where} ORDER BY r."Id" LIMIT @limit""";

        var resources = new List<ScimStoreResource>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                resources.Add(ScimStoreReads.ReadResource(reader));
        }

        var hasMore = resources.Count > query.Count;
        if (hasMore)
            resources.RemoveAt(resources.Count - 1);

        var live = resources.Where(r => r.DeletedAt == null).Select(r => r.Id).ToList();
        var references = await ScimStoreReads.ReferencesBySourceAsync(connection, null, live, cancellationToken);
        return new ScimStoreImportPage { Resources = resources, References = references, HasMore = hasMore };
    }

    public async Task<IReadOnlyList<ScimStoreResourceType>> GetResourceTypesAsync(int connectedSystemId, CancellationToken cancellationToken = default)
    {
        return await Repository.Database.ScimStoreResourceTypes
            .AsNoTracking()
            .Where(t => t.ConnectedSystemId == connectedSystemId)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScimStoreSchema>> GetSchemasAsync(int connectedSystemId, CancellationToken cancellationToken = default)
    {
        return await Repository.Database.ScimStoreSchemas
            .AsNoTracking()
            .Where(s => s.ConnectedSystemId == connectedSystemId)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<ScimStoreSummary> GetSummaryAsync(int connectedSystemId, long? importedUpTo, CancellationToken cancellationToken = default)
    {
        var context = Repository.Database;
        var resources = context.ScimStoreResources.AsNoTracking().Where(r => r.ConnectedSystemId == connectedSystemId);
        var after = importedUpTo ?? 0;

        var counts = await context.ScimStoreResourceTypes
            .AsNoTracking()
            .Where(t => t.ConnectedSystemId == connectedSystemId)
            .OrderBy(t => t.Id)
            .Select(t => new ScimResourceTypeCount(t.Name, resources.Count(r => r.ResourceTypeId == t.Id && r.DeletedAt == null)))
            .ToListAsync(cancellationToken);

        return new ScimStoreSummary
        {
            ResourceCounts = counts,
            Tombstones = await resources.CountAsync(r => r.DeletedAt != null, cancellationToken),
            LastChangeSequence = (await GetStateAsync(connectedSystemId, cancellationToken))?.LastChangeSequence ?? 0,
            ImportedUpTo = importedUpTo,
            ChangesAwaitingImport = await resources.CountAsync(r => r.ChangeSequence > after, cancellationToken),
            LastWrite = await context.ScimStoreJournalEntries
                .AsNoTracking()
                .Where(e => e.ConnectedSystemId == connectedSystemId)
                .MaxAsync(e => (DateTime?)e.Occurred, cancellationToken)
        };
    }

    public async Task<List<ScimStoreJournalEntry>> GetJournalEntriesAsync(int connectedSystemId, Guid resourceId, long? afterSequence,
        long? upToSequence, int maxEntries, CancellationToken cancellationToken = default)
    {
        var query = Repository.Database.ScimStoreJournalEntries
            .AsNoTracking()
            .Where(e => e.ConnectedSystemId == connectedSystemId && e.ResourceId == resourceId);
        if (afterSequence is { } after)
            query = query.Where(e => e.ChangeSequence > after);
        if (upToSequence is { } upTo)
            query = query.Where(e => e.ChangeSequence <= upTo);

        // The (ConnectedSystemId, ResourceId, ChangeSequence) index serves this read in order.
        return await query.OrderBy(e => e.ChangeSequence).Take(maxEntries).ToListAsync(cancellationToken);
    }

    public async Task<ScimChangeImportContext?> GetChangeImportContextAsync(Guid connectedSystemObjectChangeId, CancellationToken cancellationToken = default)
    {
        return await Repository.Database.ConnectedSystemObjectChanges
            .AsNoTracking()
            .Where(c => c.Id == connectedSystemObjectChangeId)
            .Select(c => new ScimChangeImportContext(
                c.ConnectedSystemId,
                c.ActivityRunProfileExecutionItem != null ? c.ActivityRunProfileExecutionItem.ExternalIdSnapshot : null,
                c.ActivityRunProfileExecutionItem != null ? c.ActivityRunProfileExecutionItem.Activity.ImportWatermarkFrom : null,
                c.ActivityRunProfileExecutionItem != null ? c.ActivityRunProfileExecutionItem.Activity.ImportWatermarkTo : null))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task AddDefinitionsAsync(int connectedSystemId, IReadOnlyCollection<ScimStoreSchema> schemas,
        IReadOnlyCollection<ScimStoreResourceType> resourceTypes, CancellationToken cancellationToken = default)
    {
        foreach (var schema in schemas)
            schema.ConnectedSystemId = connectedSystemId;
        foreach (var resourceType in resourceTypes)
            resourceType.ConnectedSystemId = connectedSystemId;

        // One SaveChanges is one transaction: the definitions arrive together or not at all. Detached afterwards
        // either way, so a refused set is not left in the tracker for the context's next save to try again.
        var context = Repository.Database;
        context.ScimStoreSchemas.AddRange(schemas);
        context.ScimStoreResourceTypes.AddRange(resourceTypes);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity is ScimStoreSchema or ScimStoreResourceType).ToList())
                entry.State = EntityState.Detached;
        }
    }

    public async Task<ScimStoreState?> GetStateAsync(int connectedSystemId, CancellationToken cancellationToken = default)
    {
        return await Repository.Database.ScimStoreStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.ConnectedSystemId == connectedSystemId, cancellationToken);
    }

    /// <remarks>
    /// Needs no write lock: a write never touches a tombstone, and the floor only rises (<c>GREATEST</c>), so a purge
    /// racing a write or another purge can neither lose a deletion an import has not read nor lower the floor.
    /// </remarks>
    public async Task<int> PurgeTombstonesAsync(int connectedSystemId, long importedUpToChangeSequence, DateTime deletedBefore,
        CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand("""
            WITH purged AS (
                DELETE FROM "ScimStoreResources"
                WHERE "ConnectedSystemId" = @system AND "DeletedAt" IS NOT NULL
                  AND "ChangeSequence" <= @importedUpTo AND "DeletedAt" < @deletedBefore
                RETURNING "ChangeSequence"
            ), floor AS (
                UPDATE "ScimStoreStates"
                SET "TombstonePurgeFloor" = GREATEST("TombstonePurgeFloor", (SELECT max("ChangeSequence") FROM purged))
                WHERE "ConnectedSystemId" = @system AND EXISTS (SELECT 1 FROM purged)
                RETURNING 1
            )
            SELECT count(*) FROM purged
            """, connection);
        command.Parameters.AddWithValue("system", connectedSystemId);
        command.Parameters.AddWithValue("importedUpTo", importedUpToChangeSequence);
        command.Parameters.Add(new NpgsqlParameter("deletedBefore", NpgsqlDbType.TimestampTz) { Value = deletedBefore });

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<int> DeleteJournalEntriesOlderThanAsync(DateTime cutoff, int maxRecords, CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var lease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = new NpgsqlCommand("""
            DELETE FROM "ScimStoreJournalEntries"
            WHERE "Id" IN (
                SELECT "Id" FROM "ScimStoreJournalEntries" WHERE "Occurred" < @cutoff ORDER BY "Occurred" LIMIT @max)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff });
        command.Parameters.AddWithValue("max", maxRecords);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
