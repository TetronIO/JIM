// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// How the statements a context ran read <c>PendingExportAttributeValueChanges</c>: each recorded statement is run again
/// under <c>EXPLAIN (ANALYZE, FORMAT JSON)</c> with its own parameters, and every scan of the changes table in its plan is
/// collected (#2884, #2915).
/// </summary>
internal static class PendingExportChangeScans
{
    public const string ChangesTable = "PendingExportAttributeValueChanges";

    /// <summary>
    /// One scan of the changes table: its node and index, its index condition (a bitmap heap scan's is taken from the
    /// bitmap index scans beneath it; a sequential scan has none), and the rows it read across all its loops, including
    /// those a filter or recheck then discarded.
    /// </summary>
    public sealed record ChangeScan(string NodeType, string? IndexName, string? IndexCondition, long Loops, long RowsRead)
    {
        public override string ToString() => $"{NodeType} {IndexName} [{IndexCondition}] loops {Loops}, rows read {RowsRead}";
    }

    /// <summary>
    /// Runs each recorded statement again under <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c> and returns the scans of the
    /// changes table, with every plan as text for an assertion message.
    /// </summary>
    public static async Task<(List<ChangeScan> Scans, string Plans)> ExplainAsync(string connectionString, CommandRecordingInterceptor recorder)
    {
        var (scans, _, plans) = await ExplainWithBuffersAsync(connectionString, recorder);
        return (scans, plans);
    }

    /// <summary>
    /// As <see cref="ExplainAsync"/>, with the shared buffers each statement's execution touched (hit or read), so a test
    /// can bound a statement's whole cost and not only its reads of the changes.
    /// </summary>
    public static async Task<(List<ChangeScan> Scans, List<long> StatementBuffers, string Plans)> ExplainWithBuffersAsync(
        string connectionString, CommandRecordingInterceptor recorder)
    {
        var scans = new List<ChangeScan>();
        var buffers = new List<long>();
        var plans = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var (text, parameters) in recorder.Commands)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + text, connection);
            foreach (var parameter in parameters)
                explain.Parameters.Add(parameter.Clone());
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            plans.Add($"{text}\n{plan}");
            var root = JsonDocument.Parse(plan).RootElement[0].GetProperty("Plan");
            // A node's buffer counts include its children's, so the root's are the statement's.
            buffers.Add(root.GetProperty("Shared Hit Blocks").GetInt64() + root.GetProperty("Shared Read Blocks").GetInt64());
            Collect(root, scans);
        }

        return (scans, buffers, string.Join("\n\n", plans));
    }

    private static void Collect(JsonElement node, List<ChangeScan> scans)
    {
        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == ChangesTable)
        {
            var nodeType = node.GetProperty("Node Type").GetString()!;
            string? indexName = node.TryGetProperty("Index Name", out var name) ? name.GetString() : null;
            string? condition = node.TryGetProperty("Index Cond", out var cond) ? cond.GetString() : null;
            if (nodeType == "Bitmap Heap Scan" && node.TryGetProperty("Plans", out var bitmaps))
            {
                var bitmapIndexScans = bitmaps.EnumerateArray().Where(b => b.TryGetProperty("Index Cond", out _)).ToList();
                indexName = string.Join(",", bitmapIndexScans.Select(b => b.GetProperty("Index Name").GetString()));
                condition = bitmapIndexScans.Count == 0 ? null : string.Join(" AND ", bitmapIndexScans.Select(b => b.GetProperty("Index Cond").GetString()));
            }

            var loops = node.GetProperty("Actual Loops").GetDouble();
            double Per(string property) => node.TryGetProperty(property, out var value) ? value.GetDouble() : 0;
            var rowsRead = (long)Math.Round(loops * (Per("Actual Rows") + Per("Rows Removed by Filter") + Per("Rows Removed by Index Recheck")));
            scans.Add(new ChangeScan(nodeType, indexName, condition, (long)loops, rowsRead));
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
                Collect(child, scans);
        }
    }
}
