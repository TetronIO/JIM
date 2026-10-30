// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The window reads over a Metaverse Object change's values (#2498). See <see cref="ChangeValueRowsSql"/>.
/// </summary>
public partial class MetaverseRepository
{
    /// <inheritdoc />
    public async Task<RangeResultSet<MvoChangeValueRow>> GetMvoChangeValueRowsAsync(ChangeValueRowQuery query)
    {
        using var span = ActivitySource.StartActivity("Mvo.LoadChangeValueRows");
        var connection = Repository.Database.Database.GetDbConnection();
        List<MvoChangeValueRow> rows;
        int? total;
        await using (var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection))
        {
            var (window, windowTotal) = await ChangeValueRowsSql.ReadWindowAsync(connection, ChangeValueRowsSql.MetaverseObject, query, MaxHeaderWindowSize);
            total = windowTotal;
            span?.SetTag("returned", window.Count);
            if (window.Count == 0)
                return new RangeResultSet<MvoChangeValueRow> { Results = [], TotalResults = total };

            var details = await ReadMvoChangeValueDetailsAsync(connection, ChangeValueRowsSql.DetailIds(window));
            rows = window
                .Where(row => details.ContainsKey(row.ValueId))
                .Select(row =>
                {
                    var detail = details[row.ValueId];
                    return new MvoChangeValueRow
                    {
                        ChangeId = row.ChangeId,
                        MetaverseObjectId = detail.MetaverseObjectId,
                        ValueChangeId = row.ValueId,
                        AttributeName = detail.AttributeName,
                        AttributeId = detail.AttributeId,
                        AttributeType = detail.AttributeType,
                        AttributePlurality = detail.AttributePlurality,
                        Operation = row.Operation,
                        Value = detail.Value,
                        PreviousValue = row.PreviousValueId is { } previous && details.TryGetValue(previous, out var replaced) ? replaced.Value : null
                    };
                })
                .ToList();
        }

        // The connection lease is released first: this is an EF query of its own.
        await ResolveChangeValueContributingSystemsAsync(rows.SelectMany(row => row.PreviousValue is { } previous ? new[] { row.Value, previous } : [row.Value]));
        return new RangeResultSet<MvoChangeValueRow> { Results = rows, TotalResults = total };
    }

    /// <inheritdoc />
    public async Task<List<ChangeValueAttributeSummary>> GetMvoChangeAttributeValueSummariesAsync(Guid changeId)
    {
        using var span = ActivitySource.StartActivity("Mvo.LoadChangeAttributeValueSummaries");
        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        return await ChangeValueRowsSql.ReadAttributeSummariesAsync(connection, ChangeValueRowsSql.MetaverseObject, changeId);
    }

    /// <inheritdoc />
    public async Task<List<ChangeValueSummary>> GetMvoChangeValueSummariesAsync(IReadOnlyCollection<Guid> changeIds)
    {
        using var span = ActivitySource.StartActivity("Mvo.LoadChangeValueSummaries");
        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        return await ChangeValueRowsSql.ReadSummariesAsync(connection, ChangeValueRowsSql.MetaverseObject, changeIds);
    }

    private sealed record MvoChangeValueDetail(
        Guid? MetaverseObjectId, string AttributeName, int? AttributeId, AttributeDataType AttributeType, AttributePlurality AttributePlurality,
        MvoValueChangeDto Value);

    /// <summary>
    /// Reads the given value changes in full, naming each referenced Metaverse Object from its cached display name as the
    /// change-history projection does. Only ever called for one window's rows.
    /// </summary>
    private static async Task<Dictionary<Guid, MvoChangeValueDetail>> ReadMvoChangeValueDetailsAsync(DbConnection connection, Guid[] valueIds)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v."Id", c."MetaverseObjectId", ac."AttributeName", ac."AttributeId", ac."AttributeType",
                   COALESCE(a."AttributePlurality", @single_valued),
                   v."ValueChangeType", v."StringValue", v."DateTimeValue", v."IntValue", v."LongValue", v."DecimalValue",
                   v."ByteValueLength", v."GuidValue", v."BoolValue",
                   v."ContributedBySyncRuleId", v."ContributedBySyncRuleName", v."ContributedByKind", v."ContributedByClaimType",
                   r."Id", r."CachedDisplayName", rt."Name", rt."PluralName"
            FROM "MetaverseObjectChangeAttributeValues" v
            JOIN "MetaverseObjectChangeAttributes" ac ON ac."Id" = v."MetaverseObjectChangeAttributeId"
            JOIN "MetaverseObjectChanges" c ON c."Id" = ac."MetaverseObjectChangeId"
            LEFT JOIN "MetaverseAttributes" a ON a."Id" = ac."AttributeId"
            LEFT JOIN "MetaverseObjects" r ON r."Id" = v."ReferenceValueId"
            LEFT JOIN "MetaverseObjectTypes" rt ON rt."Id" = r."TypeId"
            WHERE v."Id" = ANY(@value_ids)
            """;
        ChangeValueRowsSql.AddParameter(command, "value_ids", valueIds, NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        ChangeValueRowsSql.AddParameter(command, "single_valued", (int)AttributePlurality.SingleValued, NpgsqlDbType.Integer);

        var details = new Dictionary<Guid, MvoChangeValueDetail>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = new MvoValueChangeDto
            {
                ValueChangeType = (ValueChangeType)reader.GetInt32(6),
                StringValue = reader.IsDBNull(7) ? null : reader.GetString(7),
                DateTimeValue = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTime>(8),
                IntValue = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                LongValue = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                DecimalValue = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                ByteValueLength = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                GuidValue = reader.IsDBNull(13) ? null : reader.GetGuid(13),
                BoolValue = reader.IsDBNull(14) ? null : reader.GetBoolean(14),
                ContributedBySyncRuleId = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                ContributedBySyncRuleName = reader.IsDBNull(16) ? null : reader.GetString(16),
                ContributedByKind = reader.IsDBNull(17) ? null : (ValueContributorKind)reader.GetInt32(17),
                ContributedByClaimType = reader.IsDBNull(18) ? null : reader.GetString(18),
                ReferenceValue = reader.IsDBNull(19)
                    ? null
                    : new MvoChangeReferenceDto
                    {
                        Id = reader.GetGuid(19),
                        DisplayName = reader.IsDBNull(20) ? null : reader.GetString(20),
                        TypeName = reader.IsDBNull(21) ? string.Empty : reader.GetString(21),
                        TypePluralName = reader.IsDBNull(22) ? string.Empty : reader.GetString(22)
                    }
            };
            details[reader.GetGuid(0)] = new MvoChangeValueDetail(
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                (AttributeDataType)reader.GetInt32(4),
                (AttributePlurality)reader.GetInt32(5),
                value);
        }

        return details;
    }
}
