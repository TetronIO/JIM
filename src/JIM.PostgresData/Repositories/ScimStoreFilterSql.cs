// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Models.Scim;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Renders the SCIM store's predicate model (<see cref="ScimStoreFilter"/>) as a SQL condition over
/// <c>"ScimStoreResources" AS r</c>. Every value, JSON path and member name becomes a parameter on the command; the SQL
/// text holds only fixed fragments, so nothing a SCIM client sent can change the statement.
/// </summary>
/// <remarks>
/// The condition expects <c>@system</c> and <c>@type</c> (the Connected System and resource type listed) to be
/// parameters of the same command: a unique-value lookup is scoped by them so it uses the unique-value key. Case-
/// insensitive matching folds with <c>upper()</c> on both sides; equality on a unique attribute does not come here, but
/// through the unique-value rows, which hold values already folded as the write path folds them.
/// </remarks>
internal static class ScimStoreFilterSql
{
    internal static string Render(ScimStoreFilter filter, NpgsqlCommand command) => new Renderer(command).Render(filter);

    private sealed class Renderer(NpgsqlCommand command)
    {
        private int _next;

        public string Render(ScimStoreFilter filter) => filter switch
        {
            ScimStoreLogicalFilter logical =>
                $"({Render(logical.Left)} {(logical.Operator == ScimStoreLogicalOperator.And ? "AND" : "OR")} {Render(logical.Right)})",
            ScimStoreIdFilter id => $"r.\"Id\" = {Parameter(id.Id, NpgsqlDbType.Uuid)}",
            ScimStoreNothingFilter => "FALSE",
            ScimStoreLastModifiedFilter stamp =>
                $"r.\"LastModified\" {Operator(stamp.Comparison)} {Parameter(DateTime.SpecifyKind(stamp.Value, DateTimeKind.Utc), NpgsqlDbType.TimestampTz)}",
            ScimStoreUniqueValueFilter unique => $"""
                r."Id" IN (SELECT u."ResourceId" FROM "ScimStoreUniqueValues" AS u
                           WHERE u."ConnectedSystemId" = @system AND u."ResourceTypeId" = @type
                             AND u."Attribute" = {Parameter(unique.Attribute, NpgsqlDbType.Text)}
                             AND u."ValueHash" = {Parameter(SHA256.HashData(Encoding.UTF8.GetBytes(unique.Value)), NpgsqlDbType.Bytea)})
                """,
            ScimStoreTextFilter text => Text(text),
            ScimStoreBooleanFilter boolean =>
                $"(r.\"Payload\" #> {Path(boolean.Path)}) = to_jsonb({Parameter(boolean.Value, NpgsqlDbType.Boolean)})",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.GetType().Name, "No SQL for this SCIM store filter.")
        };

        private string Text(ScimStoreTextFilter filter)
        {
            var path = Path(filter.Path);
            if (!filter.MultiValued)
                return Compare($"(r.\"Payload\" #>> {path})", filter);

            // A value that is not the array the schema promises holds nothing to match, rather than failing the query.
            var array = $"(CASE WHEN jsonb_typeof(r.\"Payload\" #> {path}) = 'array' THEN r.\"Payload\" #> {path} ELSE '[]'::jsonb END)";
            return filter.EntryMember == null
                ? $"EXISTS (SELECT 1 FROM jsonb_array_elements_text({array}) AS entry(v) WHERE {Compare("entry.v", filter)})"
                : $"EXISTS (SELECT 1 FROM jsonb_array_elements({array}) AS entry(v) WHERE {Compare($"(entry.v ->> {Parameter(filter.EntryMember, NpgsqlDbType.Text)})", filter)})";
        }

        private string Compare(string stored, ScimStoreTextFilter filter)
        {
            var text = Parameter(filter.Text, NpgsqlDbType.Text);
            var (left, right) = filter.CaseExact ? (stored, text) : ($"upper({stored})", $"upper({text})");
            return filter.Match switch
            {
                ScimStoreTextMatch.Equal => $"{left} = {right}",
                ScimStoreTextMatch.Contains => $"strpos({left}, {right}) > 0",
                ScimStoreTextMatch.StartsWith => $"starts_with({left}, {right})",
                _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Match, "No SQL for this text match.")
            };
        }

        private string Path(IReadOnlyList<string> path) => Parameter(path.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);

        private static string Operator(ScimStoreComparison comparison) => comparison switch
        {
            ScimStoreComparison.Equal => "=",
            ScimStoreComparison.GreaterThan => ">",
            ScimStoreComparison.GreaterOrEqual => ">=",
            ScimStoreComparison.LessThan => "<",
            ScimStoreComparison.LessOrEqual => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "No SQL for this comparison.")
        };

        private string Parameter(object value, NpgsqlDbType type)
        {
            var name = $"filter{_next++}";
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            return "@" + name;
        }
    }
}
