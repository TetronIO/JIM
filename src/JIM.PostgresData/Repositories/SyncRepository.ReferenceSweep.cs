// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Serilog;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// What a Full Import that saves each unit as it goes reads and writes to resolve references (#2521): each unit's
/// references against what earlier units saved, by id alone, and, once the last unit is saved, the references a unit
/// saved before the object they name was read, together with the execution items that report what is still unresolved.
/// </summary>
public partial class SyncRepository
{
    /// <summary>
    /// Rows written per statement by the reference sweep's writers: each binds one array per column, so a statement's size
    /// follows this, not the number of resolutions.
    /// </summary>
    private const int ReferenceSweepWriteChunkSize = 10_000;

    /// <inheritdoc />
    public async Task<Dictionary<string, Guid>> GetConnectedSystemObjectIdsByAttributeValuesAsync(int connectedSystemId, int attributeId,
        IEnumerable<string> attributeValues)
    {
        var values = attributeValues.Distinct(StringComparer.Ordinal).ToList();
        var attribute = await _context.ConnectedSystemAttributes.AsNoTracking()
            .Where(a => a.Id == attributeId)
            .Select(a => new { a.Type, a.CaseExact })
            .FirstOrDefaultAsync();

        // Keyed and matched exactly as the whole-object lookup (ConnectedSystemRepository.GetConnectedSystemObjectsByAttributeValuesAsync)
        // is: by the value asked for, compared as the anchor compares its values (#2051), each value read through the
        // anchor's own data type and dropped where it cannot be one (#1285).
        var result = new Dictionary<string, Guid>(IdentifierKey.Comparer(attribute?.CaseExact ?? false));
        if (values.Count == 0 || attribute == null)
            return result;

        List<(string Value, Guid CsoId)> matches;
        switch (attribute.Type)
        {
            case AttributeDataType.Text:
            {
                var valuePredicate = attribute.CaseExact
                    ? "av.\"StringValue\" = v.input"
                    : "lower(av.\"StringValue\") = lower(v.input)";
                matches = await QueryReferenceIdMatchesAsync($"""
                    SELECT DISTINCT v.input, av."ConnectedSystemObjectId"
                    FROM unnest(@inputs) AS v(input)
                    JOIN "ConnectedSystemObjectAttributeValues" av
                        ON av."AttributeId" = @attributeId
                       AND {valuePredicate}
                    JOIN "ConnectedSystemObjects" cso ON cso."Id" = av."ConnectedSystemObjectId"
                    WHERE cso."ConnectedSystemId" = @connectedSystemId
                    """, connectedSystemId, attributeId, values, typedValues: null);
                break;
            }
            case AttributeDataType.Guid:
                matches = await QueryTypedReferenceIdMatchesAsync(connectedSystemId, attributeId, values, "GuidValue", NpgsqlDbType.Uuid,
                    value => (Guid.TryParse(value, out var parsed), parsed));
                break;
            case AttributeDataType.Number:
                matches = await QueryTypedReferenceIdMatchesAsync(connectedSystemId, attributeId, values, "IntValue", NpgsqlDbType.Integer,
                    value => (int.TryParse(value, out var parsed), parsed));
                break;
            case AttributeDataType.LongNumber:
                matches = await QueryTypedReferenceIdMatchesAsync(connectedSystemId, attributeId, values, "LongValue", NpgsqlDbType.Bigint,
                    value => (long.TryParse(value, out var parsed), parsed));
                break;
            case AttributeDataType.Decimal:
                // Invariant culture: reference values are written invariant by the import path (#1283).
                matches = await QueryTypedReferenceIdMatchesAsync(connectedSystemId, attributeId, values, "DecimalValue", NpgsqlDbType.Numeric,
                    value => (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed), parsed));
                break;
            case AttributeDataType.NotSet:
            case AttributeDataType.DateTime:
            case AttributeDataType.Binary:
            case AttributeDataType.Reference:
            case AttributeDataType.Boolean:
            default:
                Log.Warning("GetConnectedSystemObjectIdsByAttributeValuesAsync: Attribute {AttributeId} has type {AttributeType}, which cannot be used for external ids; no references can be resolved against it.",
                    attributeId, attribute.Type);
                return result;
        }

        foreach (var (requestedValue, csoId) in matches.Where(match => !result.TryAdd(match.Value, match.CsoId)))
        {
            Log.Warning("GetConnectedSystemObjectIdsByAttributeValuesAsync: Found duplicate Connected System Objects for external ID '{ExternalId}' in Connected System {ConnectedSystemId}. Returning first match; {CsoId} is not returned.",
                LogSanitiser.Sanitise(requestedValue), connectedSystemId, csoId);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, Guid>> GetConnectedSystemObjectIdsBySecondaryExternalIdAnyTypeValuesAsync(int connectedSystemId,
        IEnumerable<string> secondaryExternalIdValues)
    {
        // As the whole-object lookup: keyed by the value asked for, each object's value compared as its own Secondary
        // External ID attribute compares, ignoring case unless that attribute is case-exact (#2051).
        var values = secondaryExternalIdValues.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (values.Count == 0)
            return result;

        var matches = await QueryReferenceIdMatchesAsync("""
            SELECT DISTINCT v.input, cso."Id"
            FROM unnest(@inputs) AS v(input)
            JOIN "ConnectedSystemObjectAttributeValues" av ON lower(av."StringValue") = lower(v.input)
            JOIN "ConnectedSystemObjects" cso
                ON cso."Id" = av."ConnectedSystemObjectId"
               AND av."AttributeId" = cso."SecondaryExternalIdAttributeId"
            JOIN "ConnectedSystemAttributes" attr ON attr."Id" = av."AttributeId"
            WHERE cso."ConnectedSystemId" = @connectedSystemId
              AND (NOT attr."CaseExact" OR av."StringValue" = v.input)
            """, connectedSystemId, attributeId: null, values, typedValues: null);

        foreach (var (requestedValue, csoId) in matches.Where(match => !result.TryAdd(match.Value, match.CsoId)))
        {
            Log.Warning("GetConnectedSystemObjectIdsBySecondaryExternalIdAnyTypeValuesAsync: Found duplicate Connected System Objects for secondary external ID '{SecondaryExternalId}' in Connected System {ConnectedSystemId}. Returning first match; {CsoId} is not returned.",
                LogSanitiser.Sanitise(requestedValue), connectedSystemId, csoId);
        }

        return result;
    }

    /// <summary>
    /// The typed branches of <see cref="GetConnectedSystemObjectIdsByAttributeValuesAsync"/>: each value asked for is read
    /// through the anchor's type, the first value asked for standing for every one that reads as the same typed value, as
    /// the whole-object lookup keys them.
    /// </summary>
    private async Task<List<(string Value, Guid CsoId)>> QueryTypedReferenceIdMatchesAsync<T>(int connectedSystemId, int attributeId,
        IReadOnlyCollection<string> values, string column, NpgsqlDbType elementType, Func<string, (bool Ok, T Parsed)> parse) where T : notnull
    {
        var requestedByParsedValue = values
            .Select(value => (Result: parse(value), Value: value))
            .Where(pair => pair.Result.Ok)
            .GroupBy(pair => pair.Result.Parsed)
            .ToDictionary(group => group.Key, group => group.First().Value);
        if (requestedByParsedValue.Count == 0)
            return [];

        // The column is one of the four typed value columns named by the caller, never a value from outside.
        return await QueryReferenceIdMatchesAsync($"""
            SELECT DISTINCT v.input, av."ConnectedSystemObjectId"
            FROM unnest(@typedValues, @inputs) AS v(typed_value, input)
            JOIN "ConnectedSystemObjectAttributeValues" av
                ON av."AttributeId" = @attributeId
               AND av."{column}" = v.typed_value
            JOIN "ConnectedSystemObjects" cso ON cso."Id" = av."ConnectedSystemObjectId"
            WHERE cso."ConnectedSystemId" = @connectedSystemId
            """, connectedSystemId, attributeId, [.. requestedByParsedValue.Values],
            new NpgsqlParameter("typedValues", NpgsqlDbType.Array | elementType) { Value = requestedByParsedValue.Keys.ToArray() });
    }

    /// <summary>
    /// Runs a reference id lookup: rows of (value asked for, object id). The statement reads <c>@inputs</c> and
    /// <c>@connectedSystemId</c>, and <c>@attributeId</c> and <c>@typedValues</c> where given.
    /// </summary>
    private async Task<List<(string Value, Guid CsoId)>> QueryReferenceIdMatchesAsync(string sql, int connectedSystemId, int? attributeId,
        IReadOnlyCollection<string> inputs, NpgsqlParameter? typedValues)
    {
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("inputs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = inputs.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("connectedSystemId", NpgsqlDbType.Integer) { Value = connectedSystemId });
        if (attributeId.HasValue)
            command.Parameters.Add(new NpgsqlParameter("attributeId", NpgsqlDbType.Integer) { Value = attributeId.Value });
        if (typedValues != null)
            command.Parameters.Add(typedValues);

        var matches = new List<(string Value, Guid CsoId)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            matches.Add((reader.GetString(0), reader.GetGuid(1)));
        return matches;
    }

    /// <inheritdoc />
    public async Task<List<OutstandingReferenceValue>> GetOutstandingReferenceValuesAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        var values = new List<OutstandingReferenceValue>();
        if (connectedSystemObjectIds.Count == 0)
            return values;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        // Driven by the values' index on their object: the caller asks about a bounded batch of objects at a time.
        await using var command = new NpgsqlCommand("""
            SELECT av."Id", av."ConnectedSystemObjectId", cso."TypeId", av."AttributeId", av."UnresolvedReferenceValue"
            FROM "ConnectedSystemObjectAttributeValues" av
            JOIN "ConnectedSystemObjects" cso ON cso."Id" = av."ConnectedSystemObjectId"
            WHERE av."ConnectedSystemObjectId" = ANY(@csoIds)
              AND av."UnresolvedReferenceValue" IS NOT NULL
              AND av."ReferenceValueId" IS NULL
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = connectedSystemObjectIds.Distinct().ToArray() });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(new OutstandingReferenceValue(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
        return values;
    }

    /// <inheritdoc />
    public async Task<int> ApplyOutstandingReferenceResolutionsAsync(IReadOnlyList<OutstandingReferenceResolution> resolutions)
    {
        if (resolutions.Count == 0)
            return 0;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        var resolvedValueIds = new Dictionary<Guid, Guid>();
        var resolvedChangeValueIds = new Dictionary<Guid, Guid>();
        foreach (var chunk in resolutions.Chunk(ReferenceSweepWriteChunkSize))
        {
            // The id test keeps a value that has gained one since it was read from being re-pointed.
            await using (var valueCommand = new NpgsqlCommand("""
                UPDATE "ConnectedSystemObjectAttributeValues" av
                SET "ReferenceValueId" = r.target_id
                FROM unnest(@valueIds, @targetIds) AS r(av_id, target_id)
                WHERE av."Id" = r.av_id
                  AND av."ReferenceValueId" IS NULL
                RETURNING av."Id", av."ReferenceValueId"
                """, connection, transaction) { CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds })
            {
                valueCommand.Parameters.Add(new NpgsqlParameter("valueIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = chunk.Select(r => r.AttributeValueId).ToArray() });
                valueCommand.Parameters.Add(new NpgsqlParameter("targetIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = chunk.Select(r => r.ReferencedConnectedSystemObjectId).ToArray() });
                await using var reader = await valueCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    resolvedValueIds[reader.GetGuid(0)] = reader.GetGuid(1);
            }

            // The change record the run wrote for the holding object recorded the value by its identifier alone, marked as a
            // reference still to resolve (#2690): it is given the id and loses the mark, as the change record fix-up would.
            var withItems = chunk.Where(r => r.ExecutionItemId.HasValue).ToList();
            if (withItems.Count == 0)
                continue;

            await using var changeCommand = new NpgsqlCommand("""
                UPDATE "ConnectedSystemObjectChangeAttributeValues" cav
                SET "ReferenceValueId" = r.target_id, "IsUnresolvedReference" = false
                FROM unnest(@itemIds, @attributeIds, @referenceValues, @targetIds) AS r(item_id, attribute_id, reference_value, target_id)
                JOIN "ConnectedSystemObjectChanges" cc ON cc."ActivityRunProfileExecutionItemId" = r.item_id
                JOIN "ConnectedSystemObjectChangeAttributes" ca ON ca."ConnectedSystemChangeId" = cc."Id" AND ca."AttributeId" = r.attribute_id
                WHERE cav."ConnectedSystemObjectChangeAttributeId" = ca."Id"
                  AND cav."IsUnresolvedReference"
                  AND cav."ReferenceValueId" IS NULL
                  AND cav."StringValue" = r.reference_value
                RETURNING cav."Id", cav."ReferenceValueId"
                """, connection, transaction) { CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds };
            changeCommand.Parameters.Add(new NpgsqlParameter("itemIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = withItems.Select(r => r.ExecutionItemId!.Value).ToArray() });
            changeCommand.Parameters.Add(new NpgsqlParameter("attributeIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = withItems.Select(r => r.AttributeId).ToArray() });
            changeCommand.Parameters.Add(new NpgsqlParameter("referenceValues", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = withItems.Select(r => r.UnresolvedReferenceValue).ToArray() });
            changeCommand.Parameters.Add(new NpgsqlParameter("targetIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = withItems.Select(r => r.ReferencedConnectedSystemObjectId).ToArray() });
            await using var changeReader = await changeCommand.ExecuteReaderAsync();
            while (await changeReader.ReadAsync())
                resolvedChangeValueIds[changeReader.GetGuid(0)] = changeReader.GetGuid(1);
        }

        FixUpTrackedReferenceResolutions(resolvedValueIds, resolvedChangeValueIds);
        return resolvedValueIds.Count;
    }

    /// <inheritdoc />
    public async Task<int> ReopenReferencesToRetiredObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds, IReadOnlyCollection<Guid> retiredConnectedSystemObjectIds,
        IReadOnlyCollection<Guid> executionItemIds)
    {
        if (connectedSystemObjectIds.Count == 0 || retiredConnectedSystemObjectIds.Count == 0)
            return 0;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();
        var retired = retiredConnectedSystemObjectIds.Distinct().ToArray();

        // Only a value that still holds the identifier it was read by: the sweep resolves it again by that identifier.
        var reopenedValueIds = new HashSet<Guid>();
        foreach (var chunk in connectedSystemObjectIds.Distinct().Chunk(ReferenceSweepWriteChunkSize))
        {
            await using var valueCommand = new NpgsqlCommand("""
                UPDATE "ConnectedSystemObjectAttributeValues" av
                SET "ReferenceValueId" = NULL
                WHERE av."ConnectedSystemObjectId" = ANY(@csoIds)
                  AND av."ReferenceValueId" = ANY(@retiredIds)
                  AND av."UnresolvedReferenceValue" IS NOT NULL
                RETURNING av."Id"
                """, connection, transaction) { CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds };
            valueCommand.Parameters.Add(new NpgsqlParameter("csoIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = chunk });
            valueCommand.Parameters.Add(new NpgsqlParameter("retiredIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = retired });
            await using var reader = await valueCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                reopenedValueIds.Add(reader.GetGuid(0));
        }

        // The change record the run wrote for each object added the value with the retired object's id: it loses the id and
        // is marked as a reference still to resolve (#2690), which the sweep's writer then resolves with the value.
        var reopenedChangeValueIds = new HashSet<Guid>();
        foreach (var chunk in executionItemIds.Distinct().Chunk(ReferenceSweepWriteChunkSize))
        {
            await using var changeCommand = new NpgsqlCommand("""
                UPDATE "ConnectedSystemObjectChangeAttributeValues" cav
                SET "ReferenceValueId" = NULL, "IsUnresolvedReference" = true
                FROM "ConnectedSystemObjectChanges" cc
                JOIN "ConnectedSystemObjectChangeAttributes" ca ON ca."ConnectedSystemChangeId" = cc."Id"
                WHERE cc."ActivityRunProfileExecutionItemId" = ANY(@itemIds)
                  AND cav."ConnectedSystemObjectChangeAttributeId" = ca."Id"
                  AND cav."ValueChangeType" = @add
                  AND cav."ReferenceValueId" = ANY(@retiredIds)
                  AND cav."StringValue" IS NOT NULL
                RETURNING cav."Id"
                """, connection, transaction) { CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds };
            changeCommand.Parameters.Add(new NpgsqlParameter("itemIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = chunk });
            changeCommand.Parameters.Add(new NpgsqlParameter("retiredIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = retired });
            changeCommand.Parameters.Add(new NpgsqlParameter("add", NpgsqlDbType.Integer) { Value = (int)ValueChangeType.Add });
            await using var changeReader = await changeCommand.ExecuteReaderAsync();
            while (await changeReader.ReadAsync())
                reopenedChangeValueIds.Add(changeReader.GetGuid(0));
        }

        FixUpTrackedReopenedReferences(reopenedValueIds, reopenedChangeValueIds);
        return reopenedValueIds.Count;
    }

    /// <summary>
    /// Brings tracked instances of the values and change record values <see cref="ReopenReferencesToRetiredObjectsAsync"/>
    /// re-opened into line with what it wrote, as <see cref="FixUpTrackedReferenceResolutions"/> does for the sweep's own
    /// writes. Change detection is suppressed while enumerating.
    /// </summary>
    private void FixUpTrackedReopenedReferences(IReadOnlySet<Guid> reopenedValueIds, IReadOnlySet<Guid> reopenedChangeValueIds)
    {
        if (reopenedValueIds.Count == 0 && reopenedChangeValueIds.Count == 0)
            return;

        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in _context.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>()
                         .Where(e => e.State is not (EntityState.Detached or EntityState.Added) && reopenedValueIds.Contains(e.Entity.Id))
                         .ToList())
            {
                var navigation = entry.Navigation(nameof(ConnectedSystemObjectAttributeValue.ReferenceValue));
                navigation.CurrentValue = null;
                var property = entry.Property(value => value.ReferenceValueId);
                property.CurrentValue = null;
                property.OriginalValue = null;
                property.IsModified = false;
            }

            foreach (var entry in _context.ChangeTracker.Entries<ConnectedSystemObjectChangeAttributeValue>()
                         .Where(e => e.State is not (EntityState.Detached or EntityState.Added) && reopenedChangeValueIds.Contains(e.Entity.Id))
                         .ToList())
            {
                var reference = entry.Property(value => value.ReferenceValueId);
                reference.CurrentValue = null;
                reference.OriginalValue = null;
                reference.IsModified = false;

                var mark = entry.Property(value => value.IsUnresolvedReference);
                mark.CurrentValue = true;
                mark.OriginalValue = true;
                mark.IsModified = false;
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    /// <summary>
    /// Brings tracked instances of the values and change record values the sweep resolved into line with what it wrote, so
    /// a later save on the run's long-lived context neither writes the old state back nor reads it (src/CLAUDE.md, "Raw SQL
    /// Writes Must Fix Up or Detach Tracked Instances"). Change detection is suppressed while enumerating.
    /// </summary>
    private void FixUpTrackedReferenceResolutions(IReadOnlyDictionary<Guid, Guid> resolvedValueIds, IReadOnlyDictionary<Guid, Guid> resolvedChangeValueIds)
    {
        if (resolvedValueIds.Count == 0 && resolvedChangeValueIds.Count == 0)
            return;

        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in _context.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>()
                         .Where(e => e.State is not (EntityState.Detached or EntityState.Added) && resolvedValueIds.ContainsKey(e.Entity.Id))
                         .ToList())
            {
                var property = entry.Property(value => value.ReferenceValueId);
                property.CurrentValue = resolvedValueIds[entry.Entity.Id];
                property.OriginalValue = resolvedValueIds[entry.Entity.Id];
                property.IsModified = false;
            }

            foreach (var entry in _context.ChangeTracker.Entries<ConnectedSystemObjectChangeAttributeValue>()
                         .Where(e => e.State is not (EntityState.Detached or EntityState.Added) && resolvedChangeValueIds.ContainsKey(e.Entity.Id))
                         .ToList())
            {
                var reference = entry.Property(value => value.ReferenceValueId);
                reference.CurrentValue = resolvedChangeValueIds[entry.Entity.Id];
                reference.OriginalValue = resolvedChangeValueIds[entry.Entity.Id];
                reference.IsModified = false;

                var mark = entry.Property(value => value.IsUnresolvedReference);
                mark.CurrentValue = false;
                mark.OriginalValue = false;
                mark.IsModified = false;
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    /// <inheritdoc />
    public async Task UpdateUnresolvedReferenceReportsAsync(IReadOnlyList<UnresolvedReferenceReport> reports)
    {
        if (reports.Count == 0)
            return;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        // A narrow statement of its own rather than the bulk field update (RpeiBulkColumns): it writes the one column the
        // bulk update leaves out on purpose, and the three error columns only where the report carries an error, which
        // then holds the item's error slot (the three are always written together).
        foreach (var chunk in reports.Chunk(ReferenceSweepWriteChunkSize))
        {
            await using var command = new NpgsqlCommand("""
                UPDATE "ActivityRunProfileExecutionItems" item
                SET "UnresolvedReferenceCount" = r.unresolved_count,
                    "ErrorType" = CASE WHEN r.error_type IS NULL THEN item."ErrorType" ELSE r.error_type END,
                    "ErrorMessage" = CASE WHEN r.error_type IS NULL THEN item."ErrorMessage" ELSE r.error_message END,
                    "ErrorStackTrace" = CASE WHEN r.error_type IS NULL THEN item."ErrorStackTrace" ELSE NULL END
                FROM unnest(@itemIds, @counts, @errorTypes, @errorMessages) AS r(item_id, unresolved_count, error_type, error_message)
                WHERE item."Id" = r.item_id
                """, connection, transaction) { CommandTimeout = PostgresDataRepository.BulkOperationCommandTimeoutSeconds };
            command.Parameters.Add(new NpgsqlParameter("itemIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = chunk.Select(r => r.ExecutionItemId).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => r.UnresolvedReferenceCount).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("errorTypes", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chunk.Select(r => (int?)r.ErrorType).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("errorMessages", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(r => r.ErrorMessage).ToArray() });
            await command.ExecuteNonQueryAsync();
        }

        // Written items are detached as they are released, so this normally finds none; any still tracked reads as written.
        var reportsById = reports.GroupBy(r => r.ExecutionItemId).ToDictionary(g => g.Key, g => g.Last());
        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var entry in _context.ChangeTracker.Entries<ActivityRunProfileExecutionItem>()
                         .Where(e => e.State is not (EntityState.Detached or EntityState.Added) && reportsById.ContainsKey(e.Entity.Id))
                         .ToList())
            {
                var report = reportsById[entry.Entity.Id];
                SetUnchanged(entry.Property(item => item.UnresolvedReferenceCount), report.UnresolvedReferenceCount);
                if (report.ErrorType == null)
                    continue;

                SetUnchanged(entry.Property(item => item.ErrorType), report.ErrorType);
                SetUnchanged(entry.Property(item => item.ErrorMessage), report.ErrorMessage);
                SetUnchanged(entry.Property(item => item.ErrorStackTrace), null);
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    private static void SetUnchanged<TValue>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<ActivityRunProfileExecutionItem, TValue> property, TValue value)
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }
}
