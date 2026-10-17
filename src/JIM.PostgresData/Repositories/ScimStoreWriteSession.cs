// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// The transaction runs at READ COMMITTED, so every statement here sees each write that committed before this session
/// took the lock: the lock, not the isolation level, is what orders a Connected System's writes. Raw SQL rather than EF
/// Core because the statements the contract needs (the upsert that is the lock, <c>RETURNING</c>, the unique-violation
/// mapping) have no EF form, and these run on the request path of every SCIM write.
/// </remarks>
internal sealed class ScimStoreWriteSession : IScimStoreWriteSession
{
    private const string UniqueViolation = "23505";

    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly RawSqlConnectionLease _lease;

    // The version each resource reached in this session. A resource's version rises once per session, so a second
    // touch (an update after a membership change, two membership operations) restamps it without raising it again.
    private readonly Dictionary<Guid, long> _versions = [];

    private bool _committed;
    private bool _failed;

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

    public async Task<ScimStoreResource?> GetLiveResourceAsync(ScimStoreResourceType resourceType, Guid id)
    {
        await using var command = Command($"""
            SELECT {BulkSqlHelpers.ToQuotedList(ScimStoreColumns.Resources)}
            FROM "ScimStoreResources"
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceType" = @type AND "DeletedAt" IS NULL
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("type", (int)resourceType);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadResource(reader) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, ScimStoreResourceType>> GetLiveResourceTypesAsync(IReadOnlyCollection<Guid> ids)
    {
        var types = new Dictionary<Guid, ScimStoreResourceType>();
        if (ids.Count == 0)
            return types;

        await using var command = Command("""
            SELECT "Id", "ResourceType" FROM "ScimStoreResources"
            WHERE "Id" = ANY(@ids) AND "ConnectedSystemId" = @system AND "DeletedAt" IS NULL
            """);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            types[reader.GetGuid(0)] = (ScimStoreResourceType)reader.GetInt32(1);
        return types;
    }

    public async Task<IReadOnlyList<Guid>> GetGroupMemberIdsAsync(Guid groupId)
    {
        await using var command = Command("""SELECT "MemberId" FROM "ScimStoreGroupMembers" WHERE "GroupId" = @group""");
        command.Parameters.AddWithValue("group", groupId);
        return await ReadGuidsAsync(command);
    }

    public async Task<ScimStoreWriteOutcome> InsertResourceAsync(ScimStoreResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureUsable();

        // Values in exactly ScimStoreColumns.Resources order.
        object?[] values =
        [
            resource.Id, ConnectedSystemId, (int)resource.ResourceType, resource.UserName, resource.ExternalId,
            resource.DisplayName, 1L, ChangeSequence, resource.Created, Timestamp, null, resource.Payload
        ];
        await using var command = Command($"""
            INSERT INTO "ScimStoreResources" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.Resources)})
            VALUES ({string.Join(", ", values.Select((_, i) => $"@p{i}"))})
            """);
        AddResourceParameters(command, ScimStoreColumns.Resources, values);

        var outcome = await ExecuteWriteAsync(command);
        if (outcome == ScimStoreWriteOutcome.Written)
            _versions[resource.Id] = 1;
        return outcome;
    }

    public async Task<(ScimStoreWriteOutcome Outcome, long Version)> UpdateResourceAsync(ScimStoreResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureUsable();

        // Values in exactly ScimStoreColumns.ResourcesUpdate order.
        object?[] values = [resource.UserName, resource.ExternalId, resource.DisplayName, ChangeSequence, Timestamp, resource.Payload];
        var assignments = string.Join(", ", ScimStoreColumns.ResourcesUpdate.Select((column, i) => $"\"{column}\" = @p{i}"));
        await using var command = Command($"""
            UPDATE "ScimStoreResources" SET {assignments}, "Version" = "Version" + @raise
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceType" = @type AND "DeletedAt" IS NULL
            RETURNING "Version"
            """);
        AddResourceParameters(command, ScimStoreColumns.ResourcesUpdate, values);
        command.Parameters.AddWithValue("raise", RaiseFor(resource.Id));
        command.Parameters.AddWithValue("id", resource.Id);
        command.Parameters.AddWithValue("type", (int)resource.ResourceType);

        try
        {
            var version = await command.ExecuteScalarAsync()
                          ?? throw new InvalidOperationException($"SCIM resource {resource.Id} is not live in Connected System {ConnectedSystemId}.");
            _versions[resource.Id] = (long)version;
            return (ScimStoreWriteOutcome.Written, (long)version);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            _failed = true;
            return (UniquenessOutcome(ex), 0);
        }
    }

    public async Task<IReadOnlyList<Guid>?> TombstoneResourceAsync(ScimStoreResourceType resourceType, Guid id)
    {
        EnsureUsable();

        await using (var tombstone = Command("""
            UPDATE "ScimStoreResources"
            SET "DeletedAt" = @timestamp, "LastModified" = @timestamp, "ChangeSequence" = @sequence,
                "Version" = "Version" + @raise, "UserName" = NULL, "ExternalId" = NULL, "DisplayName" = NULL, "Payload" = NULL
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceType" = @type AND "DeletedAt" IS NULL
            RETURNING "Version"
            """))
        {
            tombstone.Parameters.Add(new NpgsqlParameter("timestamp", NpgsqlDbType.TimestampTz) { Value = Timestamp });
            tombstone.Parameters.AddWithValue("sequence", ChangeSequence);
            tombstone.Parameters.AddWithValue("raise", RaiseFor(id));
            tombstone.Parameters.AddWithValue("id", id);
            tombstone.Parameters.AddWithValue("type", (int)resourceType);
            if (await tombstone.ExecuteScalarAsync() is not long version)
                return null;
            _versions[id] = version;
        }

        // Its own memberships as a Group, then its memberships of other Groups, each of which has now changed.
        await using (var asGroup = Command("""DELETE FROM "ScimStoreGroupMembers" WHERE "GroupId" = @id"""))
        {
            asGroup.Parameters.AddWithValue("id", id);
            await asGroup.ExecuteNonQueryAsync();
        }

        List<Guid> groups;
        await using (var asMember = Command("""DELETE FROM "ScimStoreGroupMembers" WHERE "MemberId" = @id RETURNING "GroupId" """))
        {
            asMember.Parameters.AddWithValue("id", id);
            groups = await ReadGuidsAsync(asMember);
        }

        foreach (var group in groups)
            await TouchGroupAsync(group);
        return groups;
    }

    public async Task<long> AddGroupMembersAsync(Guid groupId, IReadOnlyCollection<Guid> memberIds)
    {
        var version = await TouchGroupAsync(groupId);
        if (memberIds.Count == 0)
            return version;

        await using var command = Command($"""
            INSERT INTO "ScimStoreGroupMembers" ({BulkSqlHelpers.ToQuotedList(ScimStoreColumns.GroupMembers)})
            SELECT @group, member FROM unnest(@members) AS member
            ON CONFLICT DO NOTHING
            """);
        command.Parameters.AddWithValue("group", groupId);
        command.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = memberIds.Distinct().ToArray() });
        await command.ExecuteNonQueryAsync();
        return version;
    }

    public async Task<long> RemoveGroupMembersAsync(Guid groupId, IReadOnlyCollection<Guid> memberIds)
    {
        var version = await TouchGroupAsync(groupId);
        if (memberIds.Count == 0)
            return version;

        await using var command = Command("""
            DELETE FROM "ScimStoreGroupMembers" WHERE "GroupId" = @group AND "MemberId" = ANY(@members)
            """);
        command.Parameters.AddWithValue("group", groupId);
        command.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = memberIds.ToArray() });
        await command.ExecuteNonQueryAsync();
        return version;
    }

    public async Task<long> ReplaceGroupMembersAsync(Guid groupId, IReadOnlyCollection<Guid> memberIds)
    {
        await TouchGroupAsync(groupId);

        await using (var command = Command("""
            DELETE FROM "ScimStoreGroupMembers" WHERE "GroupId" = @group AND NOT ("MemberId" = ANY(@members))
            """))
        {
            command.Parameters.AddWithValue("group", groupId);
            command.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = memberIds.ToArray() });
            await command.ExecuteNonQueryAsync();
        }

        return await AddGroupMembersAsync(groupId, memberIds);
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
            await writer.WriteAsync((int)entry.ResourceType, NpgsqlDbType.Integer);
            await writer.WriteAsync((int)entry.Operation, NpgsqlDbType.Integer);
            await writer.WriteAsync(entry.ChangedAttributes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
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

    public async ValueTask DisposeAsync()
    {
        if (!_committed && _transaction.Connection != null)
            await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        await _lease.DisposeAsync();
    }

    /// <summary>
    /// Stamps a live Group as changed by this write and returns its version, raising the version only on the session's
    /// first touch of it.
    /// </summary>
    private async Task<long> TouchGroupAsync(Guid groupId)
    {
        EnsureUsable();
        await using var command = Command("""
            UPDATE "ScimStoreResources"
            SET "ChangeSequence" = @sequence, "LastModified" = @timestamp, "Version" = "Version" + @raise
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceType" = @type AND "DeletedAt" IS NULL
            RETURNING "Version"
            """);
        command.Parameters.AddWithValue("sequence", ChangeSequence);
        command.Parameters.Add(new NpgsqlParameter("timestamp", NpgsqlDbType.TimestampTz) { Value = Timestamp });
        command.Parameters.AddWithValue("raise", RaiseFor(groupId));
        command.Parameters.AddWithValue("id", groupId);
        command.Parameters.AddWithValue("type", (int)ScimStoreResourceType.Group);

        var version = await command.ExecuteScalarAsync() as long?
                      ?? throw new InvalidOperationException($"SCIM Group {groupId} is not live in Connected System {ConnectedSystemId}.");
        _versions[groupId] = version;
        return version;
    }

    private long RaiseFor(Guid id) => _versions.ContainsKey(id) ? 0 : 1;

    private async Task<ScimStoreWriteOutcome> ExecuteWriteAsync(NpgsqlCommand command)
    {
        try
        {
            await command.ExecuteNonQueryAsync();
            return ScimStoreWriteOutcome.Written;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            // The transaction is now aborted; the caller can only dispose the session.
            _failed = true;
            return UniquenessOutcome(ex);
        }
    }

    private static ScimStoreWriteOutcome UniquenessOutcome(PostgresException ex) => ex.ConstraintName switch
    {
        ScimStoreColumns.UserNameUniqueIndex => ScimStoreWriteOutcome.UserNameTaken,
        ScimStoreColumns.ExternalIdUniqueIndex => ScimStoreWriteOutcome.ExternalIdTaken,
        // A primary key clash is a server-assigned id colliding, which must never be reported as the client's fault.
        _ => throw new InvalidOperationException($"Unexpected uniqueness violation on {ex.ConstraintName}.", ex)
    };

    private void EnsureUsable()
    {
        if (_failed)
            throw new InvalidOperationException("This SCIM store write was refused by a uniqueness rule and can only be disposed.");
        if (_committed)
            throw new InvalidOperationException("This SCIM store write has already been committed.");
    }

    private NpgsqlCommand Command(string sql)
    {
        var command = new NpgsqlCommand(sql, _connection, _transaction);
        command.Parameters.AddWithValue("system", ConnectedSystemId);
        return command;
    }

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
        "ConnectedSystemId" or "ResourceType" => NpgsqlDbType.Integer,
        "UserName" or "ExternalId" or "DisplayName" => NpgsqlDbType.Varchar,
        "Version" or "ChangeSequence" => NpgsqlDbType.Bigint,
        "Created" or "LastModified" or "DeletedAt" => NpgsqlDbType.TimestampTz,
        "Payload" => NpgsqlDbType.Jsonb,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "No parameter type for this SCIM store column.")
    };

    private static ScimStoreResource ReadResource(NpgsqlDataReader reader) => new()
    {
        // Read in ScimStoreColumns.Resources order.
        Id = reader.GetGuid(0),
        ConnectedSystemId = reader.GetInt32(1),
        ResourceType = (ScimStoreResourceType)reader.GetInt32(2),
        UserName = reader.IsDBNull(3) ? null : reader.GetString(3),
        ExternalId = reader.IsDBNull(4) ? null : reader.GetString(4),
        DisplayName = reader.IsDBNull(5) ? null : reader.GetString(5),
        Version = reader.GetInt64(6),
        ChangeSequence = reader.GetInt64(7),
        Created = reader.GetFieldValue<DateTimeOffset>(8).UtcDateTime,
        LastModified = reader.GetFieldValue<DateTimeOffset>(9).UtcDateTime,
        DeletedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10).UtcDateTime,
        Payload = reader.IsDBNull(11) ? null : reader.GetString(11)
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
