// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Reads Metaverse Objects for the synchronisation hot path by raw SQL into untracked objects, each with every value,
/// its Type and each value's Attribute (#2598). A tracked EF load materialised and tracked every attribute value, a
/// cost that grows faster than a group's membership: 154 s for one page of groups at Scale750k40kGroups. Nothing on the
/// synchronisation path needs these objects tracked, because every write to them is raw SQL; and an untracked object
/// cannot be swapped for a second instance by EF's relationship fix-up, which a tracked load of the same row later in
/// the page would otherwise do. The column lists are the bulk writers' own, so a column the bulk update writes back is
/// always one this read.
/// </summary>
/// <remarks>
/// Callers on a tracking context must keep change detection off while the objects hang off tracked entities: a
/// detection pass would discover them through those navigations and attach them as Modified.
/// </remarks>
internal static class MetaverseObjectRawReader
{
    /// <summary>
    /// Reads the given Metaverse Objects, keyed by id; an id with no row is absent. The Types and Attributes come
    /// through the context, so on the Worker's tracking context they are the instances the run's Synchronisation
    /// Rules hold.
    /// </summary>
    internal static async Task<Dictionary<Guid, MetaverseObject>> ReadAsync(JimDbContext context, Guid[] mvoIds)
    {
        if (mvoIds.Length == 0)
            return [];

        var mvosById = await ReadMetaverseObjectRowsAsync(context, mvoIds);
        if (mvosById.Count == 0)
            return mvosById;

        var attributeValues = await ReadAttributeValueRowsAsync(context, mvoIds);

        var typeIds = mvosById.Values.Select(m => m.Type.Id).Distinct().ToList();
        var typesById = await context.MetaverseObjectTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var attributeIds = attributeValues.Select(v => v.AttributeId).Distinct().ToList();
        var attributesById = await context.MetaverseAttributes.Where(a => attributeIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

        foreach (var mvo in mvosById.Values)
            mvo.Type = typesById[mvo.Type.Id];

        foreach (var (value, ownerId) in attributeValues.Select(v => (Value: v, OwnerId: v.MetaverseObject.Id)))
        {
            var owner = mvosById[ownerId];
            value.MetaverseObject = owner;
            value.Attribute = attributesById[value.AttributeId];
            owner.AttributeValues.Add(value);
        }

        return mvosById;
    }

    /// <summary>
    /// Reads the given Metaverse Objects' own rows, every column the bulk writers write, into untracked objects. Each
    /// object's <see cref="MetaverseObject.Type"/> is a placeholder carrying only the Type's id, for the caller to
    /// replace.
    /// </summary>
    private static async Task<Dictionary<Guid, MetaverseObject>> ReadMetaverseObjectRowsAsync(JimDbContext context, Guid[] mvoIds)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(
            $@"SELECT {BulkSqlHelpers.ToQuotedList(MvoBulkInsertColumns.MetaverseObjects)}
               FROM ""MetaverseObjects"" WHERE ""Id"" = ANY(@ids)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = mvoIds });

        var result = new Dictionary<Guid, MetaverseObject>(mvoIds.Length);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var mvo = new MetaverseObject();
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                SetMetaverseObjectColumn(mvo, MvoBulkInsertColumns.MetaverseObjects[ordinal], reader, ordinal);
            result[mvo.Id] = mvo;
        }

        return result;
    }

    /// <summary>
    /// Reads every attribute value row of the given Metaverse Objects, every column the bulk writers write, into
    /// untracked objects. Each value's <see cref="MetaverseObjectAttributeValue.MetaverseObject"/> is a placeholder
    /// carrying only its owner's id, for the caller to replace.
    /// </summary>
    private static async Task<List<MetaverseObjectAttributeValue>> ReadAttributeValueRowsAsync(JimDbContext context, Guid[] mvoIds)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(
            $@"SELECT {BulkSqlHelpers.ToQuotedList(MvoBulkInsertColumns.MetaverseObjectAttributeValues)}
               FROM ""MetaverseObjectAttributeValues"" WHERE ""MetaverseObjectId"" = ANY(@ids)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = mvoIds });

        var result = new List<MetaverseObjectAttributeValue>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = new MetaverseObjectAttributeValue();
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                SetAttributeValueColumn(value, MvoBulkInsertColumns.MetaverseObjectAttributeValues[ordinal], reader, ordinal);
            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// Sets the property a <c>MetaverseObjects</c> column maps to. A column with no case here fails the load rather
    /// than being dropped: <c>PageMetaverseObjectLoadDatabaseTests</c> compares every mapped column.
    /// </summary>
    private static void SetMetaverseObjectColumn(MetaverseObject mvo, string column, NpgsqlDataReader reader, int ordinal)
    {
        var isNull = reader.IsDBNull(ordinal);
        switch (column)
        {
            case "Id": mvo.Id = reader.GetGuid(ordinal); break;
            case "Created": mvo.Created = reader.GetDateTime(ordinal); break;
            case "LastUpdated": mvo.LastUpdated = isNull ? null : reader.GetDateTime(ordinal); break;
            case "TypeId": mvo.Type = new MetaverseObjectType { Id = reader.GetInt32(ordinal) }; break;
            case "Status": mvo.Status = (MetaverseObjectStatus)reader.GetInt32(ordinal); break;
            case "Origin": mvo.Origin = (MetaverseObjectOrigin)reader.GetInt32(ordinal); break;
            case "LastConnectorDisconnectedDate": mvo.LastConnectorDisconnectedDate = isNull ? null : reader.GetDateTime(ordinal); break;
            case "DeletionInitiatedByType": mvo.DeletionInitiatedByType = (ActivityInitiatorType)reader.GetInt32(ordinal); break;
            case "DeletionInitiatedById": mvo.DeletionInitiatedById = isNull ? null : reader.GetGuid(ordinal); break;
            case "DeletionInitiatedByName": mvo.DeletionInitiatedByName = isNull ? null : reader.GetString(ordinal); break;
            case "DeletionTriggeredBySystemId": mvo.DeletionTriggeredBySystemId = isNull ? null : reader.GetInt32(ordinal); break;
            case "DeletionTriggeredBySystemName": mvo.DeletionTriggeredBySystemName = isNull ? null : reader.GetString(ordinal); break;
            case "DeletionPolicySnapshotJson": mvo.DeletionPolicySnapshotJson = isNull ? null : reader.GetString(ordinal); break;
            case "DeletionScheduledByRunProfileExecutionItemId": mvo.DeletionScheduledByRunProfileExecutionItemId = isNull ? null : reader.GetGuid(ordinal); break;
            case "CachedDisplayName": mvo.CachedDisplayName = isNull ? null : reader.GetString(ordinal); break;
            case "ScopeReviewPending": mvo.ScopeReviewPending = reader.GetBoolean(ordinal); break;
            case "LastScopeEvaluatedAt": mvo.LastScopeEvaluatedAt = isNull ? null : reader.GetDateTime(ordinal); break;
            case "UnsynchronisedChangesJson": mvo.UnsynchronisedChangesJson = isNull ? null : reader.GetString(ordinal); break;
            default: throw new InvalidOperationException($"MetaverseObjectRawReader: MetaverseObjects column '{column}' is not read; map it in {nameof(SetMetaverseObjectColumn)}.");
        }
    }

    /// <summary>
    /// Sets the property a <c>MetaverseObjectAttributeValues</c> column maps to; see <see cref="SetMetaverseObjectColumn"/>.
    /// </summary>
    private static void SetAttributeValueColumn(MetaverseObjectAttributeValue value, string column, NpgsqlDataReader reader, int ordinal)
    {
        var isNull = reader.IsDBNull(ordinal);
        switch (column)
        {
            case "Id": value.Id = reader.GetGuid(ordinal); break;
            case "MetaverseObjectId": value.MetaverseObject = new MetaverseObject { Id = reader.GetGuid(ordinal) }; break;
            case "AttributeId": value.AttributeId = reader.GetInt32(ordinal); break;
            case "StringValue": value.StringValue = isNull ? null : reader.GetString(ordinal); break;
            case "DateTimeValue": value.DateTimeValue = isNull ? null : DateTimeAttributeValue.ToCanonical(reader.GetDateTime(ordinal)); break;
            case "IntValue": value.IntValue = isNull ? null : reader.GetInt32(ordinal); break;
            case "LongValue": value.LongValue = isNull ? null : reader.GetInt64(ordinal); break;
            case "DecimalValue": value.DecimalValue = isNull ? null : reader.GetDecimal(ordinal); break;
            case "ByteValue": value.ByteValue = isNull ? null : reader.GetFieldValue<byte[]>(ordinal); break;
            case "GuidValue": value.GuidValue = isNull ? null : reader.GetGuid(ordinal); break;
            case "BoolValue": value.BoolValue = isNull ? null : reader.GetBoolean(ordinal); break;
            case "ReferenceValueId": value.ReferenceValueId = isNull ? null : reader.GetGuid(ordinal); break;
            case "UnresolvedReferenceValueId": value.UnresolvedReferenceValueId = isNull ? null : reader.GetGuid(ordinal); break;
            case "ContributedBySystemId": value.ContributedBySystemId = isNull ? null : reader.GetInt32(ordinal); break;
            case "ContributedBySyncRuleId": value.ContributedBySyncRuleId = isNull ? null : reader.GetInt32(ordinal); break;
            case "NullValue": value.NullValue = reader.GetBoolean(ordinal); break;
            case "ContributedByKind": value.ContributedByKind = isNull ? null : (ValueContributorKind)reader.GetInt32(ordinal); break;
            case "ContributedByClaimType": value.ContributedByClaimType = isNull ? null : reader.GetString(ordinal); break;
            default: throw new InvalidOperationException($"MetaverseObjectRawReader: MetaverseObjectAttributeValues column '{column}' is not read; map it in {nameof(SetAttributeValueColumn)}.");
        }
    }
}
