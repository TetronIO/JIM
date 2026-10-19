// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scim;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The SCIM store's reads, shared by the write session (inside its transaction, under the store's write lock) and the
/// repository (outside any session, for GET).
/// </summary>
internal static class ScimStoreReads
{
    internal static async Task<ScimStoreResource?> LiveResourceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        int connectedSystemId, int resourceTypeId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {BulkSqlHelpers.ToQuotedList(ScimStoreColumns.Resources)}
            FROM "ScimStoreResources"
            WHERE "Id" = @id AND "ConnectedSystemId" = @system AND "ResourceTypeId" = @type AND "DeletedAt" IS NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("system", connectedSystemId);
        command.Parameters.AddWithValue("type", resourceTypeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResource(reader) : null;
    }

    /// <summary>
    /// A resource's references, all of them or only those to <paramref name="amongTargetIds"/>. A reference's target is
    /// always live (deleting a resource removes the references to it), so the join to its row finds its type.
    /// </summary>
    internal static async Task<IReadOnlyList<ScimStoreReferenceTarget>> ReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid sourceId, IReadOnlyCollection<Guid>? amongTargetIds, CancellationToken cancellationToken = default)
    {
        var references = new List<ScimStoreReferenceTarget>();
        if (amongTargetIds is { Count: 0 })
            return references;

        await using var command = new NpgsqlCommand($"""
            SELECT reference."Attribute", reference."TargetId", target."ResourceTypeId"
            FROM "ScimStoreReferences" AS reference
            INNER JOIN "ScimStoreResources" AS target ON target."Id" = reference."TargetId"
            WHERE reference."SourceId" = @source{(amongTargetIds == null ? "" : """ AND reference."TargetId" = ANY(@targets)""")}
            """, connection, transaction);
        command.Parameters.AddWithValue("source", sourceId);
        if (amongTargetIds != null)
            command.Parameters.Add(UuidArray("targets", amongTargetIds));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            references.Add(new ScimStoreReferenceTarget(reader.GetString(0), reader.GetGuid(1), reader.GetInt32(2)));
        return references;
    }

    /// <summary>
    /// The references several resources hold, keyed by the resource holding them; one holding none is absent.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>>> ReferencesBySourceAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken = default)
    {
        var bySource = new Dictionary<Guid, List<ScimStoreReferenceTarget>>();
        if (sourceIds.Count > 0)
        {
            await using var command = new NpgsqlCommand("""
                SELECT reference."SourceId", reference."Attribute", reference."TargetId", target."ResourceTypeId"
                FROM "ScimStoreReferences" AS reference
                INNER JOIN "ScimStoreResources" AS target ON target."Id" = reference."TargetId"
                WHERE reference."SourceId" = ANY(@sources)
                """, connection, transaction);
            command.Parameters.Add(UuidArray("sources", sourceIds));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = reader.GetGuid(0);
                if (!bySource.TryGetValue(source, out var held))
                    bySource[source] = held = [];
                held.Add(new ScimStoreReferenceTarget(reader.GetString(1), reader.GetGuid(2), reader.GetInt32(3)));
            }
        }

        return bySource.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ScimStoreReferenceTarget>)pair.Value);
    }

    internal static NpgsqlParameter UuidArray(string name, IEnumerable<Guid> ids) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() };

    internal static ScimStoreResource ReadResource(NpgsqlDataReader reader) => new()
    {
        // Read in ScimStoreColumns.Resources order.
        Id = reader.GetGuid(0),
        ConnectedSystemId = reader.GetInt32(1),
        ResourceTypeId = reader.GetInt32(2),
        Version = reader.GetInt64(3),
        ChangeSequence = reader.GetInt64(4),
        Created = reader.GetFieldValue<DateTimeOffset>(5).UtcDateTime,
        LastModified = reader.GetFieldValue<DateTimeOffset>(6).UtcDateTime,
        DeletedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7).UtcDateTime,
        Payload = reader.IsDBNull(8) ? null : reader.GetString(8)
    };
}
