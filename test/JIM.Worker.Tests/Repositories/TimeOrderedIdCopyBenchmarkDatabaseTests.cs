// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Measures what random (version 4) ids cost a COPY into a large <c>ConnectedSystemObjectAttributeValues</c> against
/// time-ordered (version 7) ones (#2929), on one connection and across several at once.
/// </summary>
/// <remarks>
/// <para>
/// A copy of the table, with every index the migrations give it but no foreign keys, is pre-filled with
/// <see cref="PrefillRows"/> value rows (ten per object) whose ids and parent ids are of the kind under test, so its
/// indexes together are several times <c>shared_buffers</c>. Then, after a checkpoint, <see cref="Batches"/> batches of
/// <see cref="ObjectsPerBatch"/> new objects' values are copied in as an import writes them: one after another on one
/// connection, then each batch split into contiguous slices (<c>ParallelBatchWriter.Partition</c>) copied at once on
/// <see cref="ParallelConnections"/> connections. Each COPY connection reports what it did itself (PostgreSQL 18's
/// per-backend statistics), so other work on the server does not count: write-ahead log bytes and full page images,
/// and buffers read, hit and written.
/// </para>
/// <para>
/// Only the indexes keyed on an id gain: the primary key and the two leading on the object's id. The value indexes
/// (attribute and value, lower-cased value, and so on) take a random page per row whatever the ids are. Explicit,
/// because it writes millions of rows and exists to be read, not to guard; the numbers go to the test output.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark: pre-fills a large value table twice and copies batches into it; run on demand")]
public class TimeOrderedIdCopyBenchmarkDatabaseTests
{
    private const string BenchTable = "Bench2929ConnectedSystemObjectAttributeValues";
    private const int ValuesPerObject = 10;
    private const int ObjectsPerBatch = 500;
    private const int Batches = 20;
    private const int ParallelConnections = 4;
    private const int Rounds = 2;

    /// <summary>
    /// Pre-filled rows, from <c>JIM_BENCH_PREFILL_ROWS</c> where set: enough by default for the table's indexes to be
    /// several times the test container's 128 MB of <c>shared_buffers</c>.
    /// </summary>
    private static readonly int PrefillRows =
        int.TryParse(Environment.GetEnvironmentVariable("JIM_BENCH_PREFILL_ROWS"), out var rows) && rows > 0 ? rows : 1_500_000;

    private string _connectionString = null!;

    private sealed record Measure(double Seconds, long WalBytes, long FullPageImages, long Reads, long Hits, long Writes);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the time-ordered id COPY benchmark.");
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 1800 }.ConnectionString;

        using var ctx = new JimDbContext(new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
        ctx.Database.Migrate();
    }

    [Test]
    public async Task ValueCopy_RandomAgainstTimeOrderedIds_ReportsTimeWalAndBuffersAsync()
    {
        var results = new List<(string Kind, string Mode, int Round, Measure Measure)>();
        var sizes = new Dictionary<string, (long Table, long Indexes, string SharedBuffers)>();
        try
        {
            foreach (var (kind, newId) in new (string, Func<Guid>)[] { ("version 4", Guid.NewGuid), ("version 7", Guid.CreateVersion7) })
            {
                await PrefillAsync(kind == "version 7");
                sizes[kind] = await SizesAsync();

                var random = new Random(2929);
                for (var round = 1; round <= Rounds; round++)
                {
                    await ExecuteAsync("CHECKPOINT");
                    results.Add((kind, "sequential", round, await CopyBatchesAsync(newId, random, parallel: false)));
                    await ExecuteAsync("CHECKPOINT");
                    results.Add((kind, $"{ParallelConnections} connections", round, await CopyBatchesAsync(newId, random, parallel: true)));
                }
            }
        }
        finally
        {
            await ExecuteAsync($"""DROP TABLE IF EXISTS "{BenchTable}" """);
        }

        TestContext.Out.WriteLine($"COPY into a copy of ConnectedSystemObjectAttributeValues pre-filled with {PrefillRows:N0} rows: " +
                                  $"{Batches} batches of {ObjectsPerBatch * ValuesPerObject:N0} rows ({ObjectsPerBatch} objects x {ValuesPerObject} values)");
        foreach (var (kind, size) in sizes)
            TestContext.Out.WriteLine($"  {kind} pre-fill: table {size.Table / 1048576.0:F0} MB, indexes {size.Indexes / 1048576.0:F0} MB, shared_buffers {size.SharedBuffers}");
        foreach (var (kind, mode, round, m) in results)
            TestContext.Out.WriteLine($"  {kind}  {mode,-14} round {round}  {m.Seconds,6:F2} s  {m.Seconds * 1000 / Batches,6:F1} ms/batch  " +
                                      $"WAL {m.WalBytes / 1048576.0,6:F1} MB  full page images {m.FullPageImages,7:N0}  " +
                                      $"buffers read {m.Reads,7:N0}  hit {m.Hits,9:N0}  written {m.Writes,6:N0}");

        Assert.That(results, Has.Count.EqualTo(2 * 2 * Rounds));
    }

    /// <summary>
    /// Recreates the bench table as a copy of the value table, with its indexes but no foreign keys, and fills it with
    /// <see cref="PrefillRows"/> rows: ten values per object, text values random so the value indexes are spread as an
    /// import's are, and ids of the kind under test. Version 7 rows are made oldest first, spread over the past days, as
    /// a table that has grown over time holds them.
    /// </summary>
    private async Task PrefillAsync(bool timeOrdered)
    {
        var objects = PrefillRows / ValuesPerObject;
        var objectId = timeOrdered ? $"uuidv7(-(interval '1 second' * ({objects} - g) * 2))" : "gen_random_uuid()";
        var valueId = timeOrdered ? $"uuidv7(-(interval '1 second' * ({objects} - o.g) * 2))" : "gen_random_uuid()";
        await ExecuteAsync($"""
            DROP TABLE IF EXISTS "{BenchTable}";
            CREATE TABLE "{BenchTable}" (LIKE "ConnectedSystemObjectAttributeValues" INCLUDING ALL);
            SET synchronous_commit = off;
            INSERT INTO "{BenchTable}" ({BulkSqlHelpers.ToQuotedList(CsoBulkColumns.ConnectedSystemObjectAttributeValues)})
            SELECT {valueId}, o.id, a, md5(o.g::text || ':' || a), NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
            FROM (SELECT g, {objectId} AS id FROM generate_series(1, {objects}) AS g) AS o
            CROSS JOIN generate_series(1, {ValuesPerObject}) AS a
            ORDER BY o.g, a;
            ANALYZE "{BenchTable}";
            """);
    }

    private async Task<Measure> CopyBatchesAsync(Func<Guid> newId, Random random, bool parallel)
    {
        var batches = Enumerable.Range(0, Batches).Select(_ => BuildBatch(newId, random)).ToList();
        var connections = new List<NpgsqlConnection>();
        try
        {
            for (var i = 0; i < (parallel ? ParallelConnections : 1); i++)
            {
                var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                connections.Add(connection);
            }

            var before = await Task.WhenAll(connections.Select(BackendStatsAsync));
            var stopwatch = Stopwatch.StartNew();
            foreach (var batch in batches)
            {
                if (parallel)
                {
                    var slices = JIM.PostgresData.ParallelBatchWriter.Partition(batch, ParallelConnections);
                    await Task.WhenAll(slices.Select((slice, i) => CopyAsync(connections[i], slice)));
                }
                else
                {
                    await CopyAsync(connections[0], batch);
                }
            }
            var seconds = stopwatch.Elapsed.TotalSeconds;

            var after = await Task.WhenAll(connections.Select(BackendStatsAsync));
            return new Measure(seconds,
                after.Sum(a => a.WalBytes) - before.Sum(b => b.WalBytes),
                after.Sum(a => a.FullPageImages) - before.Sum(b => b.FullPageImages),
                after.Sum(a => a.Reads) - before.Sum(b => b.Reads),
                after.Sum(a => a.Hits) - before.Sum(b => b.Hits),
                after.Sum(a => a.Writes) - before.Sum(b => b.Writes));
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// One batch as an import builds it: each new object's values in turn, the object's id and each value's id made as
    /// it goes.
    /// </summary>
    private static List<(Guid Id, Guid ObjectId, int AttributeId, string Value)> BuildBatch(Func<Guid> newId, Random random)
    {
        var rows = new List<(Guid, Guid, int, string)>(ObjectsPerBatch * ValuesPerObject);
        Span<byte> bytes = stackalloc byte[16];
        for (var o = 0; o < ObjectsPerBatch; o++)
        {
            var objectId = newId();
            for (var a = 1; a <= ValuesPerObject; a++)
            {
                random.NextBytes(bytes);
                rows.Add((newId(), objectId, a, Convert.ToHexStringLower(bytes)));
            }
        }

        return rows;
    }

    private static async Task CopyAsync(NpgsqlConnection connection, IReadOnlyList<(Guid Id, Guid ObjectId, int AttributeId, string Value)> rows)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "{BenchTable}" ({BulkSqlHelpers.ToQuotedList(CsoBulkColumns.ConnectedSystemObjectAttributeValues)}) FROM STDIN (FORMAT binary)""");
        foreach (var (id, objectId, attributeId, value) in rows)
        {
            // Order MUST match CsoBulkColumns.ConnectedSystemObjectAttributeValues.
            await writer.StartRowAsync();
            await writer.WriteAsync(id, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(objectId, NpgsqlTypes.NpgsqlDbType.Uuid);
            await writer.WriteAsync(attributeId, NpgsqlTypes.NpgsqlDbType.Integer);
            await writer.WriteAsync(value, NpgsqlTypes.NpgsqlDbType.Text);
            for (var i = 0; i < 9; i++)
                await writer.WriteNullAsync();
        }

        await writer.CompleteAsync();
    }

    /// <summary>
    /// What this connection's own backend has done so far: its write-ahead log and its I/O on relations. The connection
    /// first asks for its pending statistics to be flushed, which happens as the request's transaction ends.
    /// </summary>
    private static async Task<(long WalBytes, long FullPageImages, long Reads, long Hits, long Writes)> BackendStatsAsync(NpgsqlConnection connection)
    {
        await using (var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush()", connection))
            await flush.ExecuteNonQueryAsync();

        await using var command = new NpgsqlCommand("""
            SELECT w.wal_bytes::bigint, w.wal_fpi,
                   coalesce(sum(io.reads), 0)::bigint, coalesce(sum(io.hits), 0)::bigint, coalesce(sum(io.writes), 0)::bigint
            FROM pg_stat_get_backend_wal(pg_backend_pid()) AS w
            LEFT JOIN pg_stat_get_backend_io(pg_backend_pid()) AS io ON io.object = 'relation'
            GROUP BY w.wal_bytes, w.wal_fpi
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private async Task<(long Table, long Indexes, string SharedBuffers)> SizesAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT pg_table_size('"{BenchTable}"'), pg_indexes_size('"{BenchTable}"'), current_setting('shared_buffers')""", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
