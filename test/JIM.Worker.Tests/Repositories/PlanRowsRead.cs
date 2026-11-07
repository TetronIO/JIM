// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// How many rows a set of statements read from one table, by running each again under <c>EXPLAIN ANALYZE</c>: what each
/// scan of the table returned, plus what it read and filtered out, over all its loops. Whatever plan PostgreSQL picks, a
/// statement that reads a group's whole membership to keep a few members counts the whole membership.
/// </summary>
internal static class PlanRowsRead
{
    public const string AttributeValues = "ConnectedSystemObjectAttributeValues";

    public static async Task<long> FromAsync(string connectionString, IEnumerable<(string Text, List<NpgsqlParameter> Parameters)> commands,
        string relation)
    {
        var rowsRead = 0L;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var (text, parameters) in commands)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + text, connection);
            foreach (var parameter in parameters)
                explain.Parameters.Add(parameter.Clone());
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            rowsRead += RowsReadFrom(JsonDocument.Parse(plan).RootElement[0].GetProperty("Plan"), relation);
        }

        return rowsRead;
    }

    private static long RowsReadFrom(JsonElement node, string relation)
    {
        var rows = 0L;
        if (node.TryGetProperty("Relation Name", out var name) && name.GetString() == relation)
        {
            var loops = node.GetProperty("Actual Loops").GetDouble();
            double Per(string property) => node.TryGetProperty(property, out var value) ? value.GetDouble() : 0;
            rows += (long)Math.Round(loops * (Per("Actual Rows") + Per("Rows Removed by Filter") + Per("Rows Removed by Index Recheck")));
        }

        if (node.TryGetProperty("Plans", out var children))
            rows += children.EnumerateArray().Sum(child => RowsReadFrom(child, relation));
        return rows;
    }
}
