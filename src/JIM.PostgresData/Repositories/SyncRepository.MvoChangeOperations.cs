// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    /// <inheritdoc />
    public async Task PersistPendingMvoChangesAsync(
        List<MetaverseObjectChange> newChanges,
        List<MetaverseObjectChange> attributeAppendsToExistingChanges)
    {
        if (newChanges.Count == 0 && attributeAppendsToExistingChanges.Count == 0)
            return;

        // Pre-generate IDs for new-change graph entities so FK relationships are known
        // before building the COPY binary import statements.
        foreach (var change in newChanges)
        {
            if (change.Id == Guid.Empty)
                change.Id = Guid.NewGuid();

            foreach (var attrChange in change.AttributeChanges)
            {
                if (attrChange.Id == Guid.Empty)
                    attrChange.Id = Guid.NewGuid();

                foreach (var valueChange in attrChange.ValueChanges)
                {
                    if (valueChange.Id == Guid.Empty)
                        valueChange.Id = Guid.NewGuid();
                }
            }
        }

        // For appends, the parent id must already be set (it's the existing MvoChange row).
        // Only the attribute/value children need id generation — writing a new parent here
        // would violate IX_MetaverseObjectChanges_ActivityRunProfileExecutionItemId.
        foreach (var change in attributeAppendsToExistingChanges)
        {
            if (change.Id == Guid.Empty)
                throw new InvalidOperationException(
                    "PersistPendingMvoChangesAsync (append): each MvoChange must already have its existing parent Id set.");

            foreach (var attrChange in change.AttributeChanges)
            {
                if (attrChange.Id == Guid.Empty)
                    attrChange.Id = Guid.NewGuid();

                foreach (var valueChange in attrChange.ValueChanges.Where(vc => vc.Id == Guid.Empty))
                    valueChange.Id = Guid.NewGuid();
            }
        }

        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(PostgresDataRepository.BulkOperationCommandTimeoutSeconds);

        // One outer transaction for both the new-parent inserts and the append-only
        // attribute/value writes. Halves the per-flush BEGIN/COMMIT overhead when the
        // cross-page phase produces both, and makes the pair atomic: either the new page
        // of changes AND its cross-page appends land together, or neither does.
        await using var transaction = await _context.Database.BeginTransactionAsync();

        if (newChanges.Count > 0)
            await BulkInsertMvoChangesRawAsync(newChanges);

        // Attribute rows from BOTH sets write to the same table in one COPY call.
        var allAttrChanges = newChanges
            .Concat(attributeAppendsToExistingChanges)
            .SelectMany(c => c.AttributeChanges.Select(ac =>
                (ChangeId: c.Id, AttributeId: ac.Attribute?.Id, AttrChange: ac)))
            .ToList();

        if (allAttrChanges.Count > 0)
            await BulkInsertMvoChangeAttributesRawAsync(allAttrChanges);

        var allValueChanges = newChanges
            .Concat(attributeAppendsToExistingChanges)
            .SelectMany(c => c.AttributeChanges
                .SelectMany(ac => ac.ValueChanges.Select(vc => (AttrChangeId: ac.Id, Value: vc))))
            .ToList();

        if (allValueChanges.Count > 0)
            await BulkInsertMvoChangeAttributeValuesRawAsync(allValueChanges);

        await transaction.CommitAsync();
        _context.Database.SetCommandTimeout(previousTimeout);

        Log.Debug("PersistPendingMvoChangesAsync: Persisted {NewCount} new MVO changes and appended children to {AppendCount} existing; {AttrCount} attribute changes, {ValueCount} value changes",
            newChanges.Count, attributeAppendsToExistingChanges.Count, allAttrChanges.Count, allValueChanges.Count);
    }

    /// <summary>
    /// Bulk inserts MetaverseObjectChange rows using COPY binary import.
    /// </summary>
    private async Task BulkInsertMvoChangesRawAsync(List<MetaverseObjectChange> changes)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "MetaverseObjectChanges" (
                "Id", "MetaverseObjectId", "ActivityRunProfileExecutionItemId",
                "ChangeTime", "ChangeType", "ChangeInitiatorType",
                "InitiatedByType", "InitiatedById", "InitiatedByName",
                "SyncRuleId", "SyncRuleName",
                "DeletedObjectTypeId", "DeletedMetaverseObjectId", "DeletedObjectDisplayName"
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var c in changes)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(c.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            // MetaverseObjectId is a shadow FK — use the navigation property to get the ID
            if (c.MetaverseObject?.Id is { } mvoId && mvoId != Guid.Empty)
                await writer.WriteAsync(mvoId, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (c.ActivityRunProfileExecutionItem?.Id is { } rpeiId && rpeiId != Guid.Empty)
                await writer.WriteAsync(rpeiId, NpgsqlTypes.NpgsqlDbType.Uuid);
            else if (c.ActivityRunProfileExecutionItemId.HasValue)
                await writer.WriteAsync(c.ActivityRunProfileExecutionItemId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(c.ChangeTime, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            await writer.WriteAsync((int)c.ChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)c.ChangeInitiatorType, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync((int)c.InitiatedByType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (c.InitiatedById.HasValue)
                await writer.WriteAsync(c.InitiatedById.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (c.InitiatedByName is not null)
                await writer.WriteAsync(c.InitiatedByName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (c.SyncRule?.Id is { } syncRuleId)
                await writer.WriteAsync(syncRuleId, NpgsqlTypes.NpgsqlDbType.Integer);
            else if (c.SyncRuleId.HasValue)
                await writer.WriteAsync(c.SyncRuleId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (c.SyncRuleName is not null)
                await writer.WriteAsync(c.SyncRuleName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (c.DeletedObjectType?.Id is { } deletedTypeId)
                await writer.WriteAsync(deletedTypeId, NpgsqlTypes.NpgsqlDbType.Integer);
            else if (c.DeletedObjectTypeId.HasValue)
                await writer.WriteAsync(c.DeletedObjectTypeId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (c.DeletedMetaverseObjectId.HasValue)
                await writer.WriteAsync(c.DeletedMetaverseObjectId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (c.DeletedObjectDisplayName is not null)
                await writer.WriteAsync(c.DeletedObjectDisplayName, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Bulk inserts MetaverseObjectChangeAttribute rows using COPY binary import.
    /// </summary>
    private async Task BulkInsertMvoChangeAttributesRawAsync(List<(Guid ChangeId, int? AttributeId, MetaverseObjectChangeAttribute AttrChange)> attrChanges)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "MetaverseObjectChangeAttributes" (
                "Id", "MetaverseObjectChangeId", "AttributeId", "AttributeName", "AttributeType"
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var (changeId, attributeId, attrChange) in attrChanges)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(attrChange.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(changeId, NpgsqlTypes.NpgsqlDbType.Uuid);
            if (attributeId.HasValue)
                await writer.WriteAsync(attributeId.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            await writer.WriteAsync(attrChange.AttributeName, NpgsqlTypes.NpgsqlDbType.Text);
            await writer.WriteAsync((int)attrChange.AttributeType, NpgsqlTypes.NpgsqlDbType.Integer);
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// Bulk inserts MetaverseObjectChangeAttributeValue rows using COPY binary import.
    /// </summary>
    private async Task BulkInsertMvoChangeAttributeValuesRawAsync(List<(Guid AttrChangeId, MetaverseObjectChangeAttributeValue Value)> valueChanges)
    {
        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);

        await using var writer = await npgsqlConn.BeginBinaryImportAsync(
            """
            COPY "MetaverseObjectChangeAttributeValues" (
                "Id", "MetaverseObjectChangeAttributeId", "ValueChangeType",
                "StringValue", "DateTimeValue", "IntValue", "LongValue", "DecimalValue",
                "ByteValueLength", "GuidValue", "BoolValue", "ReferenceValueId"
            ) FROM STDIN (FORMAT binary)
            """);

        foreach (var (attrChangeId, v) in valueChanges)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(v.Id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(attrChangeId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync((int)v.ValueChangeType, NpgsqlTypes.NpgsqlDbType.Integer);
            if (v.StringValue is not null)
                await writer.WriteAsync(v.StringValue, NpgsqlTypes.NpgsqlDbType.Text);
            else
                await writer.WriteNullAsync();
            if (v.DateTimeValue.HasValue)
                await writer.WriteAsync(v.DateTimeValue.Value, NpgsqlTypes.NpgsqlDbType.TimestampTz);
            else
                await writer.WriteNullAsync();
            if (v.IntValue.HasValue)
                await writer.WriteAsync(v.IntValue.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (v.LongValue.HasValue)
                await writer.WriteAsync(v.LongValue.Value, NpgsqlTypes.NpgsqlDbType.Bigint);
            else
                await writer.WriteNullAsync();
            if (v.DecimalValue.HasValue)
                await writer.WriteAsync(v.DecimalValue.Value, NpgsqlTypes.NpgsqlDbType.Numeric);
            else
                await writer.WriteNullAsync();
            if (v.ByteValueLength.HasValue)
                await writer.WriteAsync(v.ByteValueLength.Value, NpgsqlTypes.NpgsqlDbType.Integer);
            else
                await writer.WriteNullAsync();
            if (v.GuidValue.HasValue)
                await writer.WriteAsync(v.GuidValue.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
            if (v.BoolValue.HasValue)
                await writer.WriteAsync(v.BoolValue.Value, NpgsqlTypes.NpgsqlDbType.Boolean);
            else
                await writer.WriteNullAsync();
            // Write the ReferenceValueId FK. Prefer the scalar property (set directly when
            // only the target MVO id is known); fall back to the navigation's Id when the
            // full entity is attached.
            var refId = v.ReferenceValueId ?? v.ReferenceValue?.Id;
            if (refId.HasValue && refId.Value != Guid.Empty)
                await writer.WriteAsync(refId.Value, NpgsqlTypes.NpgsqlDbType.Uuid);
            else
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }
}
