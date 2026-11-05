// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Enums;
using JIM.Models.History;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Resolves the objects history records name by id (#2669): the live object, else its own deletion record, else
/// nothing. Shared by the batch reads behind <see cref="ResolvedObjectReference"/> and by the windowed change value
/// reads (<see cref="ChangeValueRowsSql"/>), so the two name a reference the same way.
/// </summary>
/// <remarks>
/// <para>
/// "Is it live?" is an existence test on the object table, never a null id: an id with no object behind it is what a
/// history record holds once the object is gone.
/// </para>
/// <para>
/// A deletion record is a <c>Deleted</c> change record naming the object, matched on the record's subject id or on its
/// <c>Deleted*ObjectId</c> column, because both carry it until the second column is retired; the latest wins. It is
/// read only for an object that is not live: a Connected System Object deprovisioned by an export records a
/// <c>Deleted</c> change while the object still exists, awaiting its confirming import.
/// </para>
/// </remarks>
internal static class ObjectReferenceSql
{
    /// <summary>The <c>Deleted</c> change type, as stored. Taken from the enum, so a renumbered member cannot drift.</summary>
    private static readonly string DeletedChangeType = ((int)ObjectChangeType.Deleted).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// What a reference to a live Metaverse Object reads as: its cached display name. Null where no object has the id.
    /// </summary>
    internal static string MetaverseObjectLiveLabelSql(string id) =>
        $"""(SELECT ref_mvo."CachedDisplayName" FROM "MetaverseObjects" ref_mvo WHERE ref_mvo."Id" = {id})""";

    /// <summary>
    /// What a reference to a deleted Connected System Object reads as: the name its latest deletion record kept, else
    /// the external ID it kept. Null where the object is live or no deletion record names it.
    /// </summary>
    internal static string ConnectedSystemObjectDeletionLabelSql(string id) => $"""
        (SELECT COALESCE(NULLIF(dr."DeletedObjectDisplayName", ''), NULLIF(dr."DeletedObjectExternalId", ''))
         FROM "ConnectedSystemObjectChanges" dr
         WHERE dr."ChangeType" = {DeletedChangeType}
           AND (dr."ConnectedSystemObjectId" = {id} OR dr."DeletedConnectedSystemObjectId" = {id})
           AND NOT EXISTS (SELECT 1 FROM "ConnectedSystemObjects" dr_live WHERE dr_live."Id" = {id})
         ORDER BY dr."ChangeTime" DESC, dr."Id" DESC LIMIT 1)
        """;

    /// <summary>
    /// What a reference to a deleted Metaverse Object reads as: the name its latest deletion record kept. Null where
    /// the object is live or no deletion record names it.
    /// </summary>
    internal static string MetaverseObjectDeletionLabelSql(string id) => $"""
        (SELECT NULLIF(dr."DeletedObjectDisplayName", '')
         FROM "MetaverseObjectChanges" dr
         WHERE dr."ChangeType" = {DeletedChangeType}
           AND (dr."MetaverseObjectId" = {id} OR dr."DeletedMetaverseObjectId" = {id})
           AND NOT EXISTS (SELECT 1 FROM "MetaverseObjects" dr_live WHERE dr_live."Id" = {id})
         ORDER BY dr."ChangeTime" DESC, dr."Id" DESC LIMIT 1)
        """;

    /// <summary>
    /// A live Connected System Object's name alone, as <c>ConnectedSystemObject.Name</c> resolves it: the first naming
    /// tier holding a value that is not blank.
    /// </summary>
    private static string ConnectedSystemObjectRankedNameSql(string id) => $"""
        (SELECT nav."StringValue" FROM "ConnectedSystemObjectAttributeValues" nav
         JOIN "ConnectedSystemAttributes" nat ON nat."Id" = nav."AttributeId"
         WHERE nav."ConnectedSystemObjectId" = {id} AND btrim(nav."StringValue") <> ''
           AND lower(nat."Name") IN (@name_1, @name_2, @name_3)
         ORDER BY CASE lower(nat."Name") WHEN @name_1 THEN 1 WHEN @name_2 THEN 2 ELSE 3 END, nav."StringValue"
         LIMIT 1)
        """;

    /// <summary>
    /// A live Connected System Object's external ID as text, by the same column priority as the list pages
    /// (<see cref="ExternalIdValueText"/>).
    /// </summary>
    private static string ConnectedSystemObjectExternalIdSql(string id) => $"""
        (SELECT COALESCE(NULLIF(ev."StringValue", ''), ev."DateTimeValue"::text, ev."IntValue"::text, ev."LongValue"::text,
                         ev."DecimalValue"::text, ev."GuidValue"::text,
                         CASE WHEN ev."BoolValue" THEN 'True' WHEN NOT ev."BoolValue" THEN 'False' END)
         FROM "ConnectedSystemObjectAttributeValues" ev
         JOIN "ConnectedSystemObjects" eo ON eo."Id" = ev."ConnectedSystemObjectId"
         WHERE ev."ConnectedSystemObjectId" = {id} AND ev."AttributeId" = eo."ExternalIdAttributeId"
         ORDER BY ev."Id" LIMIT 1)
        """;

    /// <summary>
    /// Resolves each Connected System Object id to what is held of it, in one statement. Every id given is in the
    /// result, as <see cref="ObjectReferenceState.NotHeld"/> where neither the object nor a deletion record is found.
    /// </summary>
    /// <remarks>
    /// The label is the windowed value reads' own fragment (<see cref="ChangeValueRowsSql.ConnectedSystemObjectNameSql"/>
    /// and <see cref="ConnectedSystemObjectDeletionLabelSql"/>), so a reference reads the same in a list as in a search.
    /// </remarks>
    internal static async Task<Dictionary<Guid, ResolvedObjectReference>> ReadConnectedSystemObjectReferencesAsync(
        DbConnection connection, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var references = new Dictionary<Guid, ResolvedObjectReference>();
        if (ids.Count == 0)
            return references;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ids.id,
                   o."Id" IS NOT NULL AS live,
                   COALESCE(NULLIF({ChangeValueRowsSql.ConnectedSystemObjectNameSql("o.\"Id\"")}, ''),
                            {ConnectedSystemObjectDeletionLabelSql("ids.id")}) AS label,
                   {ConnectedSystemObjectRankedNameSql("o.\"Id\"")} AS live_name,
                   {ConnectedSystemObjectExternalIdSql("o.\"Id\"")} AS live_external_id,
                   o."TypeId", ot."Name", o."ConnectedSystemId",
                   d."ChangeTime", NULLIF(d."DeletedObjectDisplayName", ''), NULLIF(d."DeletedObjectExternalId", ''),
                   d."DeletedObjectTypeId", dt."Name", d."ConnectedSystemId", cs."Name"
            FROM (SELECT DISTINCT unnest(@ids) AS id) AS ids
            LEFT JOIN "ConnectedSystemObjects" o ON o."Id" = ids.id
            LEFT JOIN "ConnectedSystemObjectTypes" ot ON ot."Id" = o."TypeId"
            LEFT JOIN LATERAL (
                SELECT dr."ChangeTime", dr."DeletedObjectDisplayName", dr."DeletedObjectExternalId", dr."DeletedObjectTypeId",
                       dr."ConnectedSystemId"
                FROM "ConnectedSystemObjectChanges" dr
                WHERE o."Id" IS NULL AND dr."ChangeType" = {DeletedChangeType}
                  AND (dr."ConnectedSystemObjectId" = ids.id OR dr."DeletedConnectedSystemObjectId" = ids.id)
                ORDER BY dr."ChangeTime" DESC, dr."Id" DESC
                LIMIT 1
            ) AS d ON TRUE
            LEFT JOIN "ConnectedSystemObjectTypes" dt ON dt."Id" = d."DeletedObjectTypeId"
            LEFT JOIN "ConnectedSystems" cs ON cs."Id" = COALESCE(o."ConnectedSystemId", d."ConnectedSystemId")
            """;
        ChangeValueRowsSql.AddParameter(command, "ids", ids.ToArray(), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid);
        ChangeValueRowsSql.AddNameParameters(command);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            var label = NullableString(reader, 2);
            if (reader.GetBoolean(1))
            {
                references[id] = new ResolvedObjectReference
                {
                    Id = id,
                    State = ObjectReferenceState.Live,
                    Label = label,
                    DisplayName = NullableString(reader, 3),
                    ExternalId = NullableString(reader, 4),
                    ObjectTypeId = reader.GetInt32(5),
                    ObjectTypeName = NullableString(reader, 6),
                    ConnectedSystemId = reader.GetInt32(7),
                    ConnectedSystemName = NullableString(reader, 14)
                };
            }
            else if (!reader.IsDBNull(8))
            {
                references[id] = new ResolvedObjectReference
                {
                    Id = id,
                    State = ObjectReferenceState.Deleted,
                    Label = label,
                    DeletedAt = reader.GetDateTime(8).ToUniversalTime(),
                    DisplayName = NullableString(reader, 9),
                    ExternalId = NullableString(reader, 10),
                    ObjectTypeId = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    ObjectTypeName = NullableString(reader, 12),
                    ConnectedSystemId = reader.GetInt32(13),
                    ConnectedSystemName = NullableString(reader, 14)
                };
            }
            else
            {
                references[id] = ResolvedObjectReference.NotHeld(id);
            }
        }

        return references;
    }

    /// <summary>
    /// Resolves each Metaverse Object id to what is held of it, in one statement. Every id given is in the result, as
    /// <see cref="ObjectReferenceState.NotHeld"/> where neither the object nor a deletion record is found.
    /// </summary>
    internal static async Task<Dictionary<Guid, ResolvedObjectReference>> ReadMetaverseObjectReferencesAsync(
        DbConnection connection, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var references = new Dictionary<Guid, ResolvedObjectReference>();
        if (ids.Count == 0)
            return references;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ids.id,
                   m."Id" IS NOT NULL AS live,
                   COALESCE(NULLIF({MetaverseObjectLiveLabelSql("ids.id")}, ''), {MetaverseObjectDeletionLabelSql("ids.id")}) AS label,
                   m."CachedDisplayName", m."TypeId", mt."Name", mt."PluralName",
                   d."ChangeTime", NULLIF(d."DeletedObjectDisplayName", ''), d."DeletedObjectTypeId", dt."Name", dt."PluralName"
            FROM (SELECT DISTINCT unnest(@ids) AS id) AS ids
            LEFT JOIN "MetaverseObjects" m ON m."Id" = ids.id
            LEFT JOIN "MetaverseObjectTypes" mt ON mt."Id" = m."TypeId"
            LEFT JOIN LATERAL (
                SELECT dr."ChangeTime", dr."DeletedObjectDisplayName", dr."DeletedObjectTypeId"
                FROM "MetaverseObjectChanges" dr
                WHERE m."Id" IS NULL AND dr."ChangeType" = {DeletedChangeType}
                  AND (dr."MetaverseObjectId" = ids.id OR dr."DeletedMetaverseObjectId" = ids.id)
                ORDER BY dr."ChangeTime" DESC, dr."Id" DESC
                LIMIT 1
            ) AS d ON TRUE
            LEFT JOIN "MetaverseObjectTypes" dt ON dt."Id" = d."DeletedObjectTypeId"
            """;
        ChangeValueRowsSql.AddParameter(command, "ids", ids.ToArray(), NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            var label = NullableString(reader, 2);
            if (reader.GetBoolean(1))
            {
                references[id] = new ResolvedObjectReference
                {
                    Id = id,
                    State = ObjectReferenceState.Live,
                    Label = label,
                    DisplayName = NullableString(reader, 3),
                    ObjectTypeId = reader.GetInt32(4),
                    ObjectTypeName = NullableString(reader, 5),
                    ObjectTypePluralName = NullableString(reader, 6)
                };
            }
            else if (!reader.IsDBNull(7))
            {
                references[id] = new ResolvedObjectReference
                {
                    Id = id,
                    State = ObjectReferenceState.Deleted,
                    Label = label,
                    DeletedAt = reader.GetDateTime(7).ToUniversalTime(),
                    DisplayName = NullableString(reader, 8),
                    ObjectTypeId = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    ObjectTypeName = NullableString(reader, 10),
                    ObjectTypePluralName = NullableString(reader, 11)
                };
            }
            else
            {
                references[id] = ResolvedObjectReference.NotHeld(id);
            }
        }

        return references;
    }

    private static string? NullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
