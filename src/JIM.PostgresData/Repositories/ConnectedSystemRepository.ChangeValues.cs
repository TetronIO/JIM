// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The window reads over a Connected System Object change's values (#2498). See <see cref="ChangeValueRowsSql"/>.
/// </summary>
public partial class ConnectedSystemRepository
{
    /// <inheritdoc />
    public async Task<RangeResultSet<CsoChangeValueRow>> GetCsoChangeValueRowsAsync(ChangeValueRowQuery query)
    {
        using var span = ActivitySource.StartActivity("Cso.LoadChangeValueRows");
        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);

        var (window, total) = await ChangeValueRowsSql.ReadWindowAsync(connection, ChangeValueRowsSql.ConnectedSystemObject, query, MaxHeaderWindowSize);
        span?.SetTag("returned", window.Count);
        if (window.Count == 0)
            return new RangeResultSet<CsoChangeValueRow> { Results = [], TotalResults = total };

        var details = await ReadCsoChangeValueDetailsAsync(connection, ChangeValueRowsSql.DetailIds(window));
        var rows = window
            .Where(row => details.ContainsKey(row.ValueId))
            .Select(row =>
            {
                var detail = details[row.ValueId];
                return new CsoChangeValueRow
                {
                    ChangeId = row.ChangeId,
                    ValueChangeId = row.ValueId,
                    AttributeName = detail.AttributeName,
                    AttributeType = detail.AttributeType,
                    AttributePlurality = detail.AttributePlurality,
                    Operation = row.Operation,
                    Value = detail.Value,
                    PreviousValue = row.PreviousValueId is { } previous && details.TryGetValue(previous, out var replaced) ? replaced.Value : null
                };
            })
            .ToList();

        return new RangeResultSet<CsoChangeValueRow> { Results = rows, TotalResults = total };
    }

    /// <inheritdoc />
    public async Task<List<ChangeValueOperationCount>> GetCsoChangeValueOperationCountsAsync(IReadOnlyCollection<Guid> changeIds)
    {
        using var span = ActivitySource.StartActivity("Cso.LoadChangeValueOperationCounts");
        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        return await ChangeValueRowsSql.ReadOperationCountsAsync(connection, ChangeValueRowsSql.ConnectedSystemObject, changeIds);
    }

    private sealed record CsoChangeValueDetail(string AttributeName, AttributeDataType AttributeType, AttributePlurality AttributePlurality, CsoValueChangeDto Value);

    /// <summary>
    /// Reads the given value changes in full, naming each referenced object as the change-history projection names it.
    /// Only ever called for one window's rows.
    /// </summary>
    private static async Task<Dictionary<Guid, CsoChangeValueDetail>> ReadCsoChangeValueDetailsAsync(DbConnection connection, Guid[] valueIds)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v."Id", ac."AttributeName", ac."AttributeType", COALESCE(a."AttributePlurality", @single_valued),
                   v."ValueChangeType", v."StringValue", v."DateTimeValue", v."IntValue", v."LongValue", v."DecimalValue",
                   v."ByteValueLength", v."GuidValue", v."BoolValue", v."IsPendingExportStub", v."SyncRuleId", v."SyncRuleName",
                   r."Id", r."ConnectedSystemId", rt."Name", {ChangeValueRowsSql.ConnectedSystemObjectNameSql("r.\"Id\"")},
                   (SELECT sav."StringValue" FROM "ConnectedSystemObjectAttributeValues" sav
                    WHERE sav."ConnectedSystemObjectId" = r."Id" AND sav."AttributeId" = r."SecondaryExternalIdAttributeId"
                      AND sav."StringValue" IS NOT NULL LIMIT 1)
            FROM "ConnectedSystemObjectChangeAttributeValues" v
            JOIN "ConnectedSystemObjectChangeAttributes" ac ON ac."Id" = v."ConnectedSystemObjectChangeAttributeId"
            LEFT JOIN "ConnectedSystemAttributes" a ON a."Id" = ac."AttributeId"
            LEFT JOIN "ConnectedSystemObjects" r ON r."Id" = v."ReferenceValueId"
            LEFT JOIN "ConnectedSystemObjectTypes" rt ON rt."Id" = r."TypeId"
            WHERE v."Id" = ANY(@value_ids)
            """;
        ChangeValueRowsSql.AddParameter(command, "value_ids", valueIds, NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        ChangeValueRowsSql.AddParameter(command, "single_valued", (int)AttributePlurality.SingleValued, NpgsqlDbType.Integer);
        ChangeValueRowsSql.AddNameParameters(command);

        var details = new Dictionary<Guid, CsoChangeValueDetail>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = new CsoValueChangeDto
            {
                ValueChangeType = (ValueChangeType)reader.GetInt32(4),
                StringValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeValue = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6),
                IntValue = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                LongValue = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                DecimalValue = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                ByteValueLength = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                GuidValue = reader.IsDBNull(11) ? null : reader.GetGuid(11),
                BoolValue = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
                IsPendingExportStub = reader.GetBoolean(13),
                SyncRuleId = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                SyncRuleName = reader.IsDBNull(15) ? null : reader.GetString(15),
                ReferenceValue = reader.IsDBNull(16)
                    ? null
                    : new CsoChangeReferenceDto
                    {
                        Id = reader.GetGuid(16),
                        ConnectedSystemId = reader.GetInt32(17),
                        TypeName = reader.IsDBNull(18) ? string.Empty : reader.GetString(18),
                        DisplayName = reader.IsDBNull(19) ? null : reader.GetString(19),
                        SecondaryId = reader.IsDBNull(20) ? null : reader.GetString(20)
                    }
            };
            details[reader.GetGuid(0)] = new CsoChangeValueDetail(
                reader.GetString(1), (AttributeDataType)reader.GetInt32(2), (AttributePlurality)reader.GetInt32(3), value);
        }

        return details;
    }
}
