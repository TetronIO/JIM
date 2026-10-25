// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Utility;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The SQL behind the window reads over a change record's values (#2498), shared by Connected System Object and
/// Metaverse Object changes, whose tables differ only in name. A change to a large group carries one value change per
/// member, so its values are found, filtered, ordered and windowed here, and only the window's rows are read in full
/// by the caller.
/// </summary>
/// <remarks>
/// Rows are normalised exactly as the causality panel has always shown them: within one attribute change, a
/// single-valued attribute's first Add and first Remove become one Set row carrying the removed value as the previous
/// value, and nothing else of that attribute is shown; any other single-valued value change is a Set (Add) or a Remove;
/// a multi-valued value change is an Add or a Remove. Doing it in SQL means a window boundary can never split a
/// replacement in two, and the counts and the rows can never disagree.
/// </remarks>
internal static class ChangeValueRowsSql
{
    /// <summary>The tables and columns one side's change values live in.</summary>
    internal sealed record Side(
        string AttributeChangesTable,
        string ChangeForeignKey,
        string ValuesTable,
        string AttributeChangeForeignKey,
        string AttributesTable,
        Func<string, string> ReferenceName,
        bool ReferenceNameReadsNameParameters,
        string? PendingExportStubColumn,
        string SyncRuleIdColumn,
        string SyncRuleNameColumn);

    internal static readonly Side ConnectedSystemObject = new(
        AttributeChangesTable: "ConnectedSystemObjectChangeAttributes",
        ChangeForeignKey: "ConnectedSystemChangeId",
        ValuesTable: "ConnectedSystemObjectChangeAttributeValues",
        AttributeChangeForeignKey: "ConnectedSystemObjectChangeAttributeId",
        AttributesTable: "ConnectedSystemAttributes",
        ReferenceName: ConnectedSystemObjectNameSql,
        ReferenceNameReadsNameParameters: true,
        PendingExportStubColumn: "IsPendingExportStub",
        SyncRuleIdColumn: "SyncRuleId",
        SyncRuleNameColumn: "SyncRuleName");

    internal static readonly Side MetaverseObject = new(
        AttributeChangesTable: "MetaverseObjectChangeAttributes",
        ChangeForeignKey: "MetaverseObjectChangeId",
        ValuesTable: "MetaverseObjectChangeAttributeValues",
        AttributeChangeForeignKey: "MetaverseObjectChangeAttributeId",
        AttributesTable: "MetaverseAttributes",
        ReferenceName: id => $"""(SELECT ref_mvo."CachedDisplayName" FROM "MetaverseObjects" ref_mvo WHERE ref_mvo."Id" = {id})""",
        ReferenceNameReadsNameParameters: false,
        PendingExportStubColumn: null,
        SyncRuleIdColumn: "ContributedBySyncRuleId",
        SyncRuleNameColumn: "ContributedBySyncRuleName");

    /// <summary>
    /// A Connected System Object's best-effort name, mirroring <c>ConnectedSystemObject.NameOrId</c> and the
    /// change-history projection: the ranked name attributes (<c>@name_1</c> to <c>@name_3</c>, set by
    /// <see cref="AddNameParameters"/>), then the external id, then the secondary external id.
    /// </summary>
    /// <remarks>
    /// A search names every referenced object of a change, which for a large group is every member, so each tier is
    /// one exact probe on the object and the attribute: the tier's attribute ids are resolved once, ahead of the
    /// probes. Matching the attribute by name through a join instead made each probe read all of the object's values.
    /// A null value is ordered last rather than filtered out, because a <c>StringValue IS NOT NULL</c> condition drew
    /// the planner into intersecting every probe with the partial lower-case value index (#2498: a search over a
    /// 600,000-member change went from a 30-second timeout to under two seconds).
    /// </remarks>
    internal static string ConnectedSystemObjectNameSql(string id) => $"""
        COALESCE(
            (SELECT nav."StringValue" FROM "ConnectedSystemObjectAttributeValues" nav
             WHERE nav."ConnectedSystemObjectId" = {id}
               AND nav."AttributeId" = ANY(ARRAY(SELECT nat."Id" FROM "ConnectedSystemAttributes" nat WHERE lower(nat."Name") = @name_1))
             ORDER BY nav."StringValue" NULLS LAST LIMIT 1),
            (SELECT nav."StringValue" FROM "ConnectedSystemObjectAttributeValues" nav
             WHERE nav."ConnectedSystemObjectId" = {id}
               AND nav."AttributeId" = ANY(ARRAY(SELECT nat."Id" FROM "ConnectedSystemAttributes" nat WHERE lower(nat."Name") = @name_2))
             ORDER BY nav."StringValue" NULLS LAST LIMIT 1),
            (SELECT nav."StringValue" FROM "ConnectedSystemObjectAttributeValues" nav
             WHERE nav."ConnectedSystemObjectId" = {id}
               AND nav."AttributeId" = ANY(ARRAY(SELECT nat."Id" FROM "ConnectedSystemAttributes" nat WHERE lower(nat."Name") = @name_3))
             ORDER BY nav."StringValue" NULLS LAST LIMIT 1),
            (SELECT nav."StringValue" FROM "ConnectedSystemObjectAttributeValues" nav
             WHERE nav."ConnectedSystemObjectId" = {id}
               AND nav."AttributeId" = (SELECT no."ExternalIdAttributeId" FROM "ConnectedSystemObjects" no WHERE no."Id" = {id})
             ORDER BY nav."StringValue" NULLS LAST LIMIT 1),
            (SELECT nav."StringValue" FROM "ConnectedSystemObjectAttributeValues" nav
             WHERE nav."ConnectedSystemObjectId" = {id}
               AND nav."AttributeId" = (SELECT no."SecondaryExternalIdAttributeId" FROM "ConnectedSystemObjects" no WHERE no."Id" = {id})
             ORDER BY nav."StringValue" NULLS LAST LIMIT 1))
        """;

    /// <summary>
    /// The normalised rows of <paramref name="query"/>'s changes, as <c>normalised</c>: change_id, value_id,
    /// previous_value_id, operation and attribute_name, plus value_text when a search or a value sort needs it.
    /// </summary>
    private static string RowsSql(Side side, ChangeValueRowQuery query, bool needsValueText)
    {
        var attributeFilter = string.IsNullOrWhiteSpace(query.AttributeName)
            ? string.Empty
            : """AND lower(ac."AttributeName") = lower(@attribute_name)""";

        var stubCondition = side.PendingExportStubColumn == null
            ? string.Empty
            : $"""AND NOT (b.is_stub AND b.string_value <> '')""";
        var stubColumn = side.PendingExportStubColumn == null ? "false" : $"""v."{side.PendingExportStubColumn}" """;

        // Text the value reads as, for search and value ordering: a reference's name (a Pending Export stub's resolved
        // identifier where it has one), else the first value present. Only built when asked for, since naming every
        // referenced object of a large change is the expensive part of the read.
        var valueText = needsValueText
            ? $"""
                , COALESCE(
                    CASE WHEN b.reference_id IS NOT NULL {stubCondition}
                         THEN COALESCE({side.ReferenceName("b.reference_id")}, b.reference_id::text) END,
                    b.string_value, b.date_time_value::text, b.int_value::text, b.long_value::text, b.decimal_value::text,
                    b.guid_value::text, b.bool_value::text, '') AS value_text
                """
            : string.Empty;

        // A single-valued attribute change holding both an Add and a Remove reads as one Set row (see remarks).
        var paired = query.Normalise
            ? $"""
                , paired AS (
                    SELECT b.attribute_change_id,
                           (array_agg(b.value_id ORDER BY b.value_id) FILTER (WHERE b.value_change_type = @value_add))[1] AS add_id,
                           (array_agg(b.value_id ORDER BY b.value_id) FILTER (WHERE b.value_change_type = @value_remove))[1] AS remove_id
                    FROM base b
                    WHERE b.plurality = @single_valued
                    GROUP BY b.attribute_change_id
                    HAVING bool_or(b.value_change_type = @value_add) AND bool_or(b.value_change_type = @value_remove)
                )
                """
            : """
                , paired AS (SELECT NULL::uuid AS attribute_change_id, NULL::uuid AS add_id, NULL::uuid AS remove_id WHERE false)
                """;

        var singleValuedOperation = query.Normalise
            ? "WHEN b.plurality = @single_valued THEN CASE WHEN b.value_change_type = @value_add THEN @op_set ELSE @op_remove END"
            : string.Empty;

        return $"""
            WITH base AS NOT MATERIALIZED (
                SELECT v."Id" AS value_id, ac."{side.ChangeForeignKey}" AS change_id, ac."Id" AS attribute_change_id,
                       ac."AttributeName" AS attribute_name, COALESCE(a."AttributePlurality", @single_valued) AS plurality,
                       v."ValueChangeType" AS value_change_type, v."ReferenceValueId" AS reference_id, {stubColumn} AS is_stub,
                       v."StringValue" AS string_value, v."DateTimeValue" AS date_time_value, v."IntValue" AS int_value,
                       v."LongValue" AS long_value, v."DecimalValue" AS decimal_value, v."GuidValue" AS guid_value,
                       v."BoolValue" AS bool_value, v."{side.SyncRuleIdColumn}" AS rule_id, v."{side.SyncRuleNameColumn}" AS rule_name
                FROM "{side.AttributeChangesTable}" ac
                JOIN "{side.ValuesTable}" v ON v."{side.AttributeChangeForeignKey}" = ac."Id"
                LEFT JOIN "{side.AttributesTable}" a ON a."Id" = ac."AttributeId"
                WHERE ac."{side.ChangeForeignKey}" = ANY(@change_ids) {attributeFilter}
            ){paired},
            normalised AS (
                SELECT b.change_id, b.value_id, p.remove_id AS previous_value_id, b.attribute_name, b.rule_id, b.rule_name,
                       CASE WHEN p.attribute_change_id IS NOT NULL THEN @op_set
                            {singleValuedOperation}
                            WHEN b.value_change_type = @value_add THEN @op_add
                            ELSE @op_remove END AS operation
                       {valueText}
                FROM base b
                LEFT JOIN paired p ON p.attribute_change_id = b.attribute_change_id
                WHERE p.attribute_change_id IS NULL OR b.value_id = p.add_id
            )
            """;
    }

    /// <summary>
    /// Sets <paramref name="command"/> to read one window of <paramref name="query"/>'s rows: change_id, value_id,
    /// previous_value_id and operation, in the query's order.
    /// </summary>
    internal static void PrepareWindowCommand(DbCommand command, Side side, ChangeValueRowQuery query, int offset, int count)
    {
        var needsValueText = HasSearch(query) || query.Sort == ChangeValueRowSort.Value;
        var direction = query.SortDescending ? "DESC" : "ASC";
        var orderBy = query.Sort switch
        {
            ChangeValueRowSort.Attribute => $"lower(n.attribute_name) {direction}, n.value_id",
            ChangeValueRowSort.Operation => $"n.operation {direction}, lower(n.attribute_name), n.value_id",
            ChangeValueRowSort.Value => $"lower(n.value_text) {direction}, n.value_id",
            _ => $"array_position(@change_ids, n.change_id) {direction}, lower(n.attribute_name) {direction}, n.value_id {direction}"
        };

        command.CommandText = RowsSql(side, query, needsValueText) + $"""
            SELECT n.change_id, n.value_id, n.previous_value_id, n.operation
            FROM normalised n
            {FilterSql(query)}
            ORDER BY {orderBy}
            OFFSET @offset LIMIT @count
            """;
        AddQueryParameters(command, side, query);
        AddParameter(command, "offset", offset, NpgsqlDbType.Integer);
        AddParameter(command, "count", count, NpgsqlDbType.Integer);
    }

    /// <summary>Sets <paramref name="command"/> to count every row <paramref name="query"/> matches.</summary>
    internal static void PrepareCountCommand(DbCommand command, Side side, ChangeValueRowQuery query)
    {
        command.CommandText = RowsSql(side, query, HasSearch(query)) + $"""
            SELECT count(*) FROM normalised n {FilterSql(query)}
            """;
        AddQueryParameters(command, side, query);
    }

    /// <summary>
    /// Sets <paramref name="command"/> to summarise each change's normalised rows: change_id, the Set, Add and Remove
    /// counts, how many distinct Synchronisation Rules the rows name, and (meaningful only when that is one) the rule.
    /// A Set row carries its new value's rule, as the causality panel has always attributed it.
    /// </summary>
    internal static void PrepareSummariesCommand(DbCommand command, Side side, IReadOnlyCollection<Guid> changeIds)
    {
        var query = new ChangeValueRowQuery { ChangeIds = changeIds.ToList(), Count = 1 };
        command.CommandText = RowsSql(side, query, needsValueText: false) + """
            SELECT n.change_id,
                   count(*) FILTER (WHERE n.operation = @op_set),
                   count(*) FILTER (WHERE n.operation = @op_add),
                   count(*) FILTER (WHERE n.operation = @op_remove),
                   count(DISTINCT (n.rule_id, n.rule_name)) FILTER (WHERE n.rule_id IS NOT NULL AND btrim(n.rule_name) <> ''),
                   min(n.rule_id) FILTER (WHERE n.rule_id IS NOT NULL AND btrim(n.rule_name) <> ''),
                   min(n.rule_name) FILTER (WHERE n.rule_id IS NOT NULL AND btrim(n.rule_name) <> '')
            FROM normalised n
            GROUP BY n.change_id
            """;
        AddQueryParameters(command, side, query);
    }

    /// <summary>One row of a window, before its value is read in full.</summary>
    internal sealed record WindowRow(Guid ChangeId, Guid ValueId, Guid? PreviousValueId, ChangeValueOperation Operation);

    /// <summary>
    /// Reads one window of <paramref name="query"/>'s rows and, when asked, their total; the window is clamped to
    /// <paramref name="maxWindowSize"/>. An empty set of changes reads nothing.
    /// </summary>
    internal static async Task<(List<WindowRow> Window, int? Total)> ReadWindowAsync(
        DbConnection connection, Side side, ChangeValueRowQuery query, int maxWindowSize)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Count < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.Count, "A window must ask for at least one row.");
        if (query.ChangeIds.Count == 0)
            return ([], query.IncludeTotalCount ? 0 : null);

        var window = new List<WindowRow>();
        await using (var command = connection.CreateCommand())
        {
            PrepareWindowCommand(command, side, query, Math.Max(query.Offset, 0), Math.Min(query.Count, maxWindowSize));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                window.Add(new WindowRow(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    (ChangeValueOperation)reader.GetInt32(3)));
        }

        if (!query.IncludeTotalCount)
            return (window, null);

        await using var countCommand = connection.CreateCommand();
        PrepareCountCommand(countCommand, side, query);
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        return (window, total);
    }

    /// <summary>Reads a summary of each change's normalised rows; a change with no values has none.</summary>
    internal static async Task<List<ChangeValueSummary>> ReadSummariesAsync(
        DbConnection connection, Side side, IReadOnlyCollection<Guid> changeIds)
    {
        if (changeIds.Count == 0)
            return [];

        await using var command = connection.CreateCommand();
        PrepareSummariesCommand(command, side, changeIds);
        var summaries = new List<ChangeValueSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var agreed = reader.GetInt64(4) == 1;
            summaries.Add(new ChangeValueSummary(
                reader.GetGuid(0),
                (int)reader.GetInt64(1),
                (int)reader.GetInt64(2),
                (int)reader.GetInt64(3),
                agreed ? reader.GetInt32(5) : null,
                agreed ? reader.GetString(6) : null));
        }
        return summaries;
    }

    /// <summary>The ids a window's rows are read in full by: each row's value, and a Set row's previous value.</summary>
    internal static Guid[] DetailIds(IEnumerable<WindowRow> window) =>
        window.SelectMany(row => row.PreviousValueId is { } previous ? new[] { row.ValueId, previous } : [row.ValueId]).ToArray();

    private static bool HasSearch(ChangeValueRowQuery query) => !string.IsNullOrWhiteSpace(query.SearchText);

    private static string FilterSql(ChangeValueRowQuery query)
    {
        var conditions = new List<string>();
        if (query.Operation.HasValue)
            conditions.Add("n.operation = @operation");
        if (HasSearch(query))
            conditions.Add("(n.attribute_name ILIKE @search OR n.value_text ILIKE @search)");
        return conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
    }

    private static void AddQueryParameters(DbCommand command, Side side, ChangeValueRowQuery query)
    {
        AddParameter(command, "change_ids", query.ChangeIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        AddParameter(command, "single_valued", (int)AttributePlurality.SingleValued, NpgsqlDbType.Integer);
        AddParameter(command, "value_add", (int)ValueChangeType.Add, NpgsqlDbType.Integer);
        AddParameter(command, "value_remove", (int)ValueChangeType.Remove, NpgsqlDbType.Integer);
        AddParameter(command, "op_set", (int)ChangeValueOperation.Set, NpgsqlDbType.Integer);
        AddParameter(command, "op_add", (int)ChangeValueOperation.Add, NpgsqlDbType.Integer);
        AddParameter(command, "op_remove", (int)ChangeValueOperation.Remove, NpgsqlDbType.Integer);
        if (!string.IsNullOrWhiteSpace(query.AttributeName))
            AddParameter(command, "attribute_name", query.AttributeName.Trim(), NpgsqlDbType.Text);
        if (query.Operation is { } operation)
            AddParameter(command, "operation", (int)operation, NpgsqlDbType.Integer);
        if (HasSearch(query))
            AddParameter(command, "search", LikePattern.Contains(query.SearchText!.Trim()), NpgsqlDbType.Text);
        if (side.ReferenceNameReadsNameParameters)
            AddNameParameters(command);
    }

    /// <summary>The ranked name attributes <see cref="ConnectedSystemObjectNameSql"/> reads, lowered.</summary>
    internal static void AddNameParameters(DbCommand command)
    {
        for (var tier = 0; tier < 3; tier++)
            AddParameter(command, $"name_{tier + 1}", ObjectNaming.ConnectedSystemNameAttributes[tier].ToLowerInvariant(), NpgsqlDbType.Text);
    }

    internal static void AddParameter(DbCommand command, string name, object value, NpgsqlDbType type) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
}
