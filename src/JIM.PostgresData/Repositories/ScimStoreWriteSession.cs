// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Data.Repositories;
using JIM.Models.Scim;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// One write to one Connected System's SCIM store, on the repository's own connection and in its own transaction,
/// holding the store's write lock (the <c>ScimStoreStates</c> row) until it commits or is disposed. See
/// <see cref="IScimStoreWriteSession"/> for the contract and <see cref="ScimStoreRepository.BeginWriteAsync"/> for how
/// the lock is taken.
/// </summary>
/// <remarks>
/// <para>
/// The transaction runs at READ COMMITTED, so every statement here sees each write that committed before this session
/// took the lock: the lock, not the isolation level, is what orders a Connected System's writes. It also makes a
/// uniqueness check followed by the write safe, because no other write to the same store can run in between; the
/// unique values' primary key is the backstop, not the mechanism, so a conflict is reported without aborting the
/// transaction.
/// </para>
/// <para>
/// Raw SQL rather than EF Core because the statements the contract needs (the upsert that is the lock,
/// <c>RETURNING</c>, array parameters) have no EF form, and these run on the request path of every SCIM write.
/// </para>
/// </remarks>
internal sealed class ScimStoreWriteSession : IScimStoreWriteSession
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly RawSqlConnectionLease _lease;

    // The version each resource reached in this session. A resource's version rises once per session, so a second
    // touch (an update after a reference change, two reference operations) restamps it without raising it again.
    private readonly Dictionary<Guid, long> _versions = [];
    private bool _disposed;

    private bool _committed;

    internal ScimStoreWriteSession(NpgsqlConnection connection, NpgsqlTransaction transaction, RawSqlConnectionLease lease,
        int connectedSystemId, long changeSequence, DateTime timestamp)
    {
        _connection = connection;
        _transaction = transaction;
        _lease = lease;
        ConnectedSystemId = connectedSystemId;
        ChangeSequence = changeSequence;
        Timestamp = timestamp;
    }

    public int ConnectedSystemId { get; }

    public long ChangeSequence { get; }

    public DateTime Timestamp { get; }

    public Task<ScimStoreResource?> GetLiveResourceAsync(int resourceTypeId, Guid id) =>
        ScimStoreReads.LiveResourceAsync(_connection, _transaction, ConnectedSystemId, resourceTypeId, id);

    public async Task<IReadOnlyDictionary<Guid, int>> GetLiveResourceTypeIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        var types = new Dictionary<Guid, int>();
        if (ids.Count == 0)
            return types;

        await using var command = Command("""
            SELECT "Id", "ResourceTypeId" FROM "ScimStoreResources"
            WHERE "Id" = ANY(@ids) AND "ConnectedSystemId" = @system AND "DeletedAt" IS NULL
            """);
        command.Parameters.Add(UuidArray("ids", ids));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            types[reader.GetGuid(0)] = reader.GetInt32(1);
        return types;
    }

    public Task<IReadOnlyList<ScimStoreReferenceTarget>> GetReferencesAsync(Guid sourceId, IReadOnlyCollection<Guid>? amongTargetIds = null) =>
        ScimStoreReads.ReferencesAsync(_connection, _transaction, sourceId, amongTargetIds);

    public async Task<ScimStoreWriteResult> InsertResourceAsync(ScimStoreResource resource, IReadOnlyCollection<ScimStoreUniqueValue> uniqueValues)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(uniqueValues);
        EnsureUsable();

        var unique = Hashed(uniqueValues);
        var conflict = await FindConflictAsync(resource.ResourceTypeId, unique, except: null);
        if (conflict != null)
            return ScimStoreWriteResult.Conflict(conflict);

        // Values in exactly ScimStoreColumns.Resources order.
        object?[] values =
        [
            resource.Id, ConnectedSystemId, resource.ResourceTypeId, 1L, ChangeSequence, resource.Created, Timestamp, null,
            resource.Payload
        ];
        await using (var command = Command($"""
            INSERT INTO "ScimStoreResources" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.Resources)})
            VALUES ({string.Join(", ", values.Select((_, i) => $"@p{i}"))})
            """))
        {
            AddResourceParameters(command, ScimStoreColumns.Resources, values);
            await command.ExecuteNonQueryAsync();
        }

        await InsertUniqueValuesAsync(resource.ResourceTypeId, resource.Id, unique);
        _versions[resource.Id] = 1;
        return ScimStoreWriteResult.Written(1);
    }

    public async Task<ScimStoreWriteResult> UpdateResourceAsync(ScimStoreResource resource, IReadOnlyCollection<ScimStoreUniqueValue> uniqueValues)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(uniqueValues);
        EnsureUsable();

        var unique = Hashed(uniqueValues);
        var conflict = await FindConflictAsync(resource.ResourceTypeId, unique, except: resource.Id);
        if (conflict != null)
            return ScimStoreWriteResult.Conflict(conflict);

        // Values in exactly ScimStoreColumns.ResourcesUpdate order.
        object?[] values = [ChangeSequence, Timestamp, resource.Payload];
        var assignments = string.Join(", ", ScimStoreColumns.ResourcesUpdate.Select((column, i) => $"\"{column}\" = @p{i}"));
        long version;
        await using (var command = Command($"""
            UPDATE "ScimStoreResources" SET {assignments}, "Version" = "Version" + @raise
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceTypeId" = @type AND "DeletedAt" IS NULL
            RETURNING "Version"
            """))
        {
            AddResourceParameters(command, ScimStoreColumns.ResourcesUpdate, values);
            command.Parameters.AddWithValue("raise", RaiseFor(resource.Id));
            command.Parameters.AddWithValue("id", resource.Id);
            command.Parameters.AddWithValue("type", resource.ResourceTypeId);
            version = await command.ExecuteScalarAsync() as long?
                      ?? throw new InvalidOperationException($"SCIM resource {resource.Id} is not live in Connected System {ConnectedSystemId}.");
        }

        await DeleteUniqueValuesAsync(resource.Id);
        await InsertUniqueValuesAsync(resource.ResourceTypeId, resource.Id, unique);
        _versions[resource.Id] = version;
        return ScimStoreWriteResult.Written(version);
    }

    public async Task<IReadOnlyList<ScimStoreReferrer>?> TombstoneResourceAsync(int resourceTypeId, Guid id)
    {
        EnsureUsable();

        await using (var tombstone = Command("""
            UPDATE "ScimStoreResources"
            SET "DeletedAt" = @timestamp, "LastModified" = @timestamp, "ChangeSequence" = @sequence,
                "Version" = "Version" + @raise, "Payload" = NULL
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceTypeId" = @type AND "DeletedAt" IS NULL
            RETURNING "Version"
            """))
        {
            tombstone.Parameters.Add(new NpgsqlParameter("timestamp", NpgsqlDbType.TimestampTz) { Value = Timestamp });
            tombstone.Parameters.AddWithValue("sequence", ChangeSequence);
            tombstone.Parameters.AddWithValue("raise", RaiseFor(id));
            tombstone.Parameters.AddWithValue("id", id);
            tombstone.Parameters.AddWithValue("type", resourceTypeId);
            if (await tombstone.ExecuteScalarAsync() is not long version)
                return null;
            _versions[id] = version;
        }

        // Its unique values are free for reuse, and its own references go with it.
        await DeleteUniqueValuesAsync(id);
        await using (var own = Command("""DELETE FROM "ScimStoreReferences" WHERE "SourceId" = @id"""))
        {
            own.Parameters.AddWithValue("id", id);
            await own.ExecuteNonQueryAsync();
        }

        // References to it from other resources go too, and each of those resources has now changed.
        var referrers = new List<ScimStoreReferrer>();
        await using (var toIt = Command("""
            WITH removed AS (DELETE FROM "ScimStoreReferences" WHERE "TargetId" = @id RETURNING "SourceId", "Attribute")
            SELECT DISTINCT removed."SourceId", source."ResourceTypeId", removed."Attribute"
            FROM removed INNER JOIN "ScimStoreResources" AS source ON source."Id" = removed."SourceId"
            """))
        {
            toIt.Parameters.AddWithValue("id", id);
            await using var reader = await toIt.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                referrers.Add(new ScimStoreReferrer(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2)));
        }

        foreach (var source in referrers.Select(r => r.SourceId).Distinct())
            await TouchResourceAsync(source);
        return referrers;
    }

    public async Task<long> AddReferencesAsync(Guid sourceId, string attribute, IReadOnlyCollection<Guid> targetIds)
    {
        var version = await TouchResourceAsync(sourceId);
        if (targetIds.Count == 0)
            return version;

        await using var command = Command($"""
            INSERT INTO "ScimStoreReferences" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.References)})
            SELECT @source, @attribute, target FROM unnest(@targets) AS target
            ON CONFLICT DO NOTHING
            """);
        command.Parameters.AddWithValue("source", sourceId);
        command.Parameters.AddWithValue("attribute", attribute);
        command.Parameters.Add(UuidArray("targets", targetIds.Distinct().ToArray()));
        await command.ExecuteNonQueryAsync();
        return version;
    }

    public async Task<long> RemoveReferencesAsync(Guid sourceId, string attribute, IReadOnlyCollection<Guid> targetIds)
    {
        var version = await TouchResourceAsync(sourceId);
        if (targetIds.Count == 0)
            return version;

        await using var command = Command("""
            DELETE FROM "ScimStoreReferences"
            WHERE "SourceId" = @source AND "Attribute" = @attribute AND "TargetId" = ANY(@targets)
            """);
        command.Parameters.AddWithValue("source", sourceId);
        command.Parameters.AddWithValue("attribute", attribute);
        command.Parameters.Add(UuidArray("targets", targetIds));
        await command.ExecuteNonQueryAsync();
        return version;
    }

    public async Task<long> ReplaceReferencesAsync(Guid sourceId, string attribute, IReadOnlyCollection<Guid> targetIds)
    {
        await TouchResourceAsync(sourceId);

        await using (var command = Command("""
            DELETE FROM "ScimStoreReferences"
            WHERE "SourceId" = @source AND "Attribute" = @attribute AND NOT ("TargetId" = ANY(@targets))
            """))
        {
            command.Parameters.AddWithValue("source", sourceId);
            command.Parameters.AddWithValue("attribute", attribute);
            command.Parameters.Add(UuidArray("targets", targetIds));
            await command.ExecuteNonQueryAsync();
        }

        return await AddReferencesAsync(sourceId, attribute, targetIds);
    }

    public async Task AppendJournalEntriesAsync(IReadOnlyCollection<ScimStoreJournalEntry> entries)
    {
        EnsureUsable();
        if (entries.Count == 0)
            return;

        await using var writer = await _connection.BeginBinaryImportAsync(
            $"COPY \"ScimStoreJournalEntries\" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.JournalEntries)}) FROM STDIN (FORMAT BINARY)");
        foreach (var entry in entries)
        {
            entry.ConnectedSystemId = ConnectedSystemId;
            entry.ChangeSequence = ChangeSequence;
            entry.Occurred = Timestamp;

            // Values in exactly ScimStoreColumns.JournalEntries order.
            await writer.StartRowAsync();
            await writer.WriteAsync(entry.ConnectedSystemId, NpgsqlDbType.Integer);
            await writer.WriteAsync(entry.ChangeSequence, NpgsqlDbType.Bigint);
            await writer.WriteAsync(entry.ResourceId, NpgsqlDbType.Uuid);
            await writer.WriteAsync(entry.ResourceTypeName, NpgsqlDbType.Varchar);
            await writer.WriteAsync((int)entry.Operation, NpgsqlDbType.Integer);
            await writer.WriteAsync(entry.ChangedAttributes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
            await writer.WriteAsync(entry.IgnoredAttributes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
            await WriteNullableAsync(writer, entry.ApiKeyId, NpgsqlDbType.Uuid);
            await WriteNullableAsync(writer, entry.ApiKeyName, NpgsqlDbType.Text);
            await WriteNullableAsync(writer, entry.ClientAddress, NpgsqlDbType.Varchar);
            await writer.WriteAsync(entry.Occurred, NpgsqlDbType.TimestampTz);
        }

        await writer.CompleteAsync();
    }

    public async Task CommitAsync()
    {
        EnsureUsable();
        await _transaction.CommitAsync();
        _committed = true;
    }

    /// <remarks>
    /// Idempotent: a caller may release the write lock early and dispose again on the way out, and the second dispose
    /// must not close a connection the context has opened since.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (!_committed && _transaction.Connection != null)
            await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _lease.DisposeAsync();
    }

    /// <summary>
    /// Stamps a live resource as changed by this write and returns its version, raising the version only on the
    /// session's first touch of it.
    /// </summary>
    private async Task<long> TouchResourceAsync(Guid id)
    {
        EnsureUsable();
        await using var command = Command("""
            UPDATE "ScimStoreResources"
            SET "ChangeSequence" = @sequence, "LastModified" = @timestamp, "Version" = "Version" + @raise
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "DeletedAt" IS NULL
            RETURNING "Version"
            """);
        command.Parameters.AddWithValue("sequence", ChangeSequence);
        command.Parameters.Add(new NpgsqlParameter("timestamp", NpgsqlDbType.TimestampTz) { Value = Timestamp });
        command.Parameters.AddWithValue("raise", RaiseFor(id));
        command.Parameters.AddWithValue("id", id);

        var version = await command.ExecuteScalarAsync() as long?
                      ?? throw new InvalidOperationException($"SCIM resource {id} is not live in Connected System {ConnectedSystemId}.");
        _versions[id] = version;
        return version;
    }

    /// <summary>
    /// The first attribute (by name) whose value another live resource of the type already holds, or null. Safe as a
    /// check followed by a write because the session holds the store's write lock.
    /// </summary>
    private async Task<string?> FindConflictAsync(int resourceTypeId, IReadOnlyList<(string Attribute, string Value, byte[] Hash)> unique, Guid? except)
    {
        if (unique.Count == 0)
            return null;

        await using var command = Command("""
            SELECT held."Attribute" FROM "ScimStoreUniqueValues" held
            INNER JOIN unnest(@attributes, @hashes) AS wanted("Attribute", "ValueHash")
                ON held."Attribute" = wanted."Attribute" AND held."ValueHash" = wanted."ValueHash"
            WHERE held."ConnectedSystemId" = @system AND held."ResourceTypeId" = @type
              AND (@except IS NULL OR held."ResourceId" <> @except)
            ORDER BY held."Attribute"
            LIMIT 1
            """);
        command.Parameters.AddWithValue("type", resourceTypeId);
        command.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = unique.Select(u => u.Attribute).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = unique.Select(u => u.Hash).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("except", NpgsqlDbType.Uuid) { Value = (object?)except ?? DBNull.Value });
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task InsertUniqueValuesAsync(int resourceTypeId, Guid resourceId, IReadOnlyList<(string Attribute, string Value, byte[] Hash)> unique)
    {
        if (unique.Count == 0)
            return;

        // Columns in exactly ScimStoreColumns.UniqueValues order.
        await using var command = Command($"""
            INSERT INTO "ScimStoreUniqueValues" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.UniqueValues)})
            SELECT @system, @type, u.attribute, u.value, u.hash, @resource
            FROM unnest(@attributes, @values, @hashes) AS u(attribute, value, hash)
            """);
        command.Parameters.AddWithValue("type", resourceTypeId);
        command.Parameters.AddWithValue("resource", resourceId);
        command.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { Value = unique.Select(u => u.Attribute).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = unique.Select(u => u.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = unique.Select(u => u.Hash).ToArray() });
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteUniqueValuesAsync(Guid resourceId)
    {
        await using var command = Command("""DELETE FROM "ScimStoreUniqueValues" WHERE "ResourceId" = @id""");
        command.Parameters.AddWithValue("id", resourceId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The unique values with their key hashes, one per attribute and value: a caller naming the same pair twice is
    /// holding it once.
    /// </summary>
    private static List<(string Attribute, string Value, byte[] Hash)> Hashed(IReadOnlyCollection<ScimStoreUniqueValue> uniqueValues) =>
        uniqueValues
            .Select(u => (u.Attribute, u.Value))
            .Distinct()
            .Select(u => (u.Attribute, u.Value, SHA256.HashData(Encoding.UTF8.GetBytes(u.Value))))
            .ToList();

    private long RaiseFor(Guid id) => _versions.ContainsKey(id) ? 0 : 1;

    private void EnsureUsable()
    {
        if (_committed)
            throw new InvalidOperationException("This SCIM store write has already been committed.");
    }

    private NpgsqlCommand Command(string sql)
    {
        var command = new NpgsqlCommand(sql, _connection, _transaction);
        command.Parameters.AddWithValue("system", ConnectedSystemId);
        return command;
    }

    private static NpgsqlParameter UuidArray(string name, IEnumerable<Guid> ids) => ScimStoreReads.UuidArray(name, ids);

    private static void AddResourceParameters(NpgsqlCommand command, string[] columns, object?[] values)
    {
        for (var i = 0; i < columns.Length; i++)
        {
            var parameter = BulkSqlHelpers.NullableParam(values[i], ResourceColumnType(columns[i]));
            parameter.ParameterName = $"p{i}";
            command.Parameters.Add(parameter);
        }
    }

    private static NpgsqlDbType ResourceColumnType(string column) => column switch
    {
        "Id" => NpgsqlDbType.Uuid,
        "ConnectedSystemId" or "ResourceTypeId" => NpgsqlDbType.Integer,
        "Version" or "ChangeSequence" => NpgsqlDbType.Bigint,
        "Created" or "LastModified" or "DeletedAt" => NpgsqlDbType.TimestampTz,
        "Payload" => NpgsqlDbType.Jsonb,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "No parameter type for this SCIM store column.")
    };

    private static async Task<List<Guid>> ReadGuidsAsync(NpgsqlCommand command)
    {
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type)
    {
        if (value is null)
            await writer.WriteNullAsync();
        else
            await writer.WriteAsync(value, type);
    }
}
