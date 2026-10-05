// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The raw-SQL writes that retire generated values into the retired values register (Unique Value Generation,
/// #242, Phase 6; plan decision 4), shared by every repository that deletes something a generated value assignment
/// hangs off: <c>SyncRepository</c> and <c>MetaverseRepository</c> (Metaverse Object deletion), and
/// <c>ConnectedSystemRepository</c> and <c>SyncRepository</c> (Connected System Object deletion, including a connector
/// space clear). Each is one set-based statement: it reads the doomed objects' assignments, keeps only those whose
/// generated mapping never reuses values (<c>NeverReuse</c> on, or a Sequence token, which always never reuses),
/// inserts them into the register with <c>ON CONFLICT DO NOTHING</c> (a value already retired for the attribute is
/// left as it is), and returns what it actually inserted.
/// <para>
/// The caller runs it inside the same transaction as the deletion and BEFORE it: the assignments are removed by the
/// deletion's foreign-key cascade, so once the object is gone there is nothing left to read. The supersession path
/// (<see cref="RetireAndDeleteAssignmentsAsync"/>) deletes the assignments itself, in the same statement.
/// </para>
/// <para>
/// The fourth deletion path, a generated flow being removed, is retired by the database itself: a row trigger on
/// <c>SyncRuleMappingGenerations</c> (migration <c>AddRetiredGeneratedValues</c>), because a flow is removed through
/// many ORM graph saves and cascades that no single repository method sees.
/// </para>
/// </summary>
internal static class RetiredGeneratedValueSql
{
    /// <summary>
    /// The register columns, in <see cref="RetiredGeneratedValueBulkColumns.RetiredGeneratedValues"/> order. The
    /// <c>SELECT</c> in <see cref="RetireAsync"/> MUST project its values in exactly this order.
    /// </summary>
    private static readonly string InsertColumns = BulkSqlHelpers.ToQuotedList(RetiredGeneratedValueBulkColumns.RetiredGeneratedValues);

    /// <summary>
    /// A Connected System Object's display name, chosen exactly as <see cref="ObjectNaming.ConnectedSystemNameAttributes"/>
    /// orders its candidates (case-insensitive attribute names, first present value wins), so a deleted account is
    /// named in the register the way the portal named it while it existed.
    /// </summary>
    private static readonly string ConnectedSystemObjectNameSql = BuildConnectedSystemObjectNameSql();

    /// <summary>
    /// Retires the generated values held by the given Metaverse Objects, which the caller is about to delete
    /// (reason <see cref="RetiredGeneratedValueReason.ObjectDeleted"/>).
    /// </summary>
    internal static Task<IReadOnlyList<GeneratedValueRetirement>> RetireForMetaverseObjectsAsync(JimDbContext context, IReadOnlyCollection<Guid> metaverseObjectIds)
    {
        if (metaverseObjectIds.Count == 0)
            return Task.FromResult<IReadOnlyList<GeneratedValueRetirement>>([]);

        return RetireAsync(context, @"a.""MetaverseObjectId"" = ANY(@objectIds)",
            command => command.Parameters.Add(new NpgsqlParameter("objectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = metaverseObjectIds.ToArray() }),
            RetiredGeneratedValueReason.ObjectDeleted, activityId: null, deleteAssignments: false);
    }

    /// <summary>
    /// Retires the export-mode generated values held by the given Connected System Objects, which the caller is
    /// about to delete (reason <see cref="RetiredGeneratedValueReason.ObjectDeleted"/>).
    /// </summary>
    internal static Task<IReadOnlyList<GeneratedValueRetirement>> RetireForConnectedSystemObjectsAsync(JimDbContext context, IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return Task.FromResult<IReadOnlyList<GeneratedValueRetirement>>([]);

        return RetireAsync(context, @"a.""ConnectedSystemObjectId"" = ANY(@objectIds)",
            command => command.Parameters.Add(new NpgsqlParameter("objectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = connectedSystemObjectIds.ToArray() }),
            RetiredGeneratedValueReason.ObjectDeleted, activityId: null, deleteAssignments: false);
    }

    /// <summary>
    /// Retires the export-mode generated values held by every Connected System Object of one Connected System, which
    /// the caller is about to clear from its connector space (reason <see cref="RetiredGeneratedValueReason.ObjectDeleted"/>).
    /// </summary>
    internal static Task<IReadOnlyList<GeneratedValueRetirement>> RetireForConnectedSystemAsync(JimDbContext context, int connectedSystemId)
    {
        return RetireAsync(context,
            @"a.""ConnectedSystemObjectId"" IN (SELECT cso.""Id"" FROM ""ConnectedSystemObjects"" cso WHERE cso.""ConnectedSystemId"" = @connectedSystemId)",
            command => command.Parameters.Add(new NpgsqlParameter("connectedSystemId", NpgsqlDbType.Integer) { Value = connectedSystemId }),
            RetiredGeneratedValueReason.ObjectDeleted, activityId: null, deleteAssignments: false);
    }

    /// <summary>
    /// Deletes the given assignments and retires their values in the same statement: the supersession path, where
    /// the object lives on but its value stopped being generated.
    /// </summary>
    internal static Task<IReadOnlyList<GeneratedValueRetirement>> RetireAndDeleteAssignmentsAsync(
        JimDbContext context, IReadOnlyCollection<Guid> assignmentIds, RetiredGeneratedValueReason reason, Guid? activityId)
    {
        if (assignmentIds.Count == 0)
            return Task.FromResult<IReadOnlyList<GeneratedValueRetirement>>([]);

        return RetireAsync(context, @"a.""Id"" = ANY(@assignmentIds)",
            command => command.Parameters.Add(new NpgsqlParameter("assignmentIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = assignmentIds.ToArray() }),
            reason, activityId, deleteAssignments: true);
    }

    /// <summary>
    /// The one statement every writer above shares. <paramref name="assignmentPredicate"/> is fixed SQL text chosen
    /// by the callers above (never user input); everything variable is a parameter.
    /// </summary>
    private static async Task<IReadOnlyList<GeneratedValueRetirement>> RetireAsync(
        JimDbContext context,
        string assignmentPredicate,
        Action<NpgsqlCommand> addPredicateParameters,
        RetiredGeneratedValueReason reason,
        Guid? activityId,
        bool deleteAssignments)
    {
        // A data-modifying CTE when the assignments go in the same statement (supersession), a plain read otherwise
        // (the deletion's own cascade removes them). Either way every later CTE reads this one's rows.
        var source = deleteAssignments
            ? $@"DELETE FROM ""GeneratedValueAssignments"" a WHERE {assignmentPredicate} RETURNING a.*"
            : $@"SELECT a.* FROM ""GeneratedValueAssignments"" a WHERE {assignmentPredicate}";

        // The SELECT list below writes values in exactly RetiredGeneratedValueBulkColumns.RetiredGeneratedValues order:
        // MetaverseAttributeId, ConnectedSystemObjectTypeAttributeId, Value, NormalisedValue, RetiredAt, Reason,
        // FromObjectDisplayName, FromObjectId, ActivityId.
        var sql = $@"
            WITH doomed AS ({source}),
            retired AS (
                INSERT INTO ""RetiredGeneratedValues"" ({InsertColumns})
                SELECT d.""MetaverseAttributeId"",
                       d.""ConnectedSystemObjectTypeAttributeId"",
                       d.""Value"",
                       LOWER(d.""NormalisedValue""),
                       now(),
                       @reason,
                       CASE WHEN d.""MetaverseObjectId"" IS NOT NULL THEN mvo.""CachedDisplayName""
                            ELSE ({ConnectedSystemObjectNameSql}) END,
                       COALESCE(d.""MetaverseObjectId"", d.""ConnectedSystemObjectId""),
                       @activityId
                FROM doomed d
                INNER JOIN ""SyncRuleMappingGenerations"" g ON g.""Id"" = d.""SyncRuleMappingGenerationId""
                LEFT JOIN ""MetaverseObjects"" mvo ON mvo.""Id"" = d.""MetaverseObjectId""
                WHERE g.""NeverReuse"" OR g.""TokenKind"" = @sequenceTokenKind
                ON CONFLICT DO NOTHING
                RETURNING ""MetaverseAttributeId"", ""ConnectedSystemObjectTypeAttributeId"", ""Value"", ""Reason"", ""FromObjectId"")
            SELECT r.""MetaverseAttributeId"", r.""ConnectedSystemObjectTypeAttributeId"", COALESCE(ma.""Name"", ca.""Name"", '') AS ""AttributeName"",
                   r.""Value"", r.""Reason"", r.""FromObjectId""
            FROM retired r
            LEFT JOIN ""MetaverseAttributes"" ma ON ma.""Id"" = r.""MetaverseAttributeId""
            LEFT JOIN ""ConnectedSystemAttributes"" ca ON ca.""Id"" = r.""ConnectedSystemObjectTypeAttributeId""";

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        addPredicateParameters(command);
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Integer) { Value = (int)reason });
        command.Parameters.Add(new NpgsqlParameter("sequenceTokenKind", NpgsqlDbType.Integer) { Value = (int)GeneratedValueTokenKind.Sequence });
        var activityIdParameter = BulkSqlHelpers.NullableParam(activityId, NpgsqlDbType.Uuid);
        activityIdParameter.ParameterName = "activityId";
        command.Parameters.Add(activityIdParameter);

        var retirements = new List<GeneratedValueRetirement>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            retirements.Add(new GeneratedValueRetirement
            {
                MetaverseAttributeId = reader.IsDBNull(0) ? null : reader.GetInt32(0),
                ConnectedSystemObjectTypeAttributeId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                AttributeName = reader.GetString(2),
                Value = reader.GetString(3),
                Reason = (RetiredGeneratedValueReason)reader.GetInt32(4),
                FromObjectId = reader.IsDBNull(5) ? null : reader.GetGuid(5)
            });
        }

        return retirements;
    }

    private static string BuildConnectedSystemObjectNameSql()
    {
        // Names come from the shared catalogue, not user input; each is a fixed identifier-like literal, so quoting
        // them inline is safe and keeps the statement free of an extra array parameter.
        var names = ObjectNaming.ConnectedSystemNameAttributes.Select(n => n.ToLowerInvariant()).ToList();
        var inList = string.Join(", ", names.Select(n => $"'{n}'"));
        var rankCases = string.Join(" ", names.Select((n, i) => $"WHEN '{n}' THEN {i}"));

        return $@"SELECT v.""StringValue""
                  FROM ""ConnectedSystemObjectAttributeValues"" v
                  INNER JOIN ""ConnectedSystemAttributes"" ca2 ON ca2.""Id"" = v.""AttributeId""
                  WHERE v.""ConnectedSystemObjectId"" = d.""ConnectedSystemObjectId""
                    AND LOWER(ca2.""Name"") IN ({inList})
                    AND BTRIM(COALESCE(v.""StringValue"", '')) <> ''
                  ORDER BY CASE LOWER(ca2.""Name"") {rankCases} END
                  LIMIT 1";
    }
}
