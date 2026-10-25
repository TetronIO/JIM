// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the keyset cursor behind
/// <see cref="ConnectedSystemRepository.GetExecutableExportBatchAsync"/> (#2496). The export loop reads
/// one batch at a time strictly after the last row of the previous batch; the read stays cheap only
/// while PostgreSQL can seek IX_PendingExports_ConnectedSystemId_CreatedAt_Id to the cursor and stop
/// after one page. Written as <c>CreatedAt &gt; x OR (CreatedAt = x AND Id &gt; y)</c> alone, the
/// cursor could not bound the index scan: every batch either skipped every row already exported or
/// fetched and sorted every row still to export, so a 790k-object export spent about 137 ms per
/// 100-object batch reading.
/// </summary>
/// <remarks>
/// The plan is the assertion, not the timing: the EF Core in-memory provider has no planner, and a
/// timing budget would only catch the regression at a scale no test should seed. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ExecutableExportBatchCursorDatabaseTests
{
    private const int ExportedCount = 10_000;
    private const int PendingCount = 10_000;
    private static readonly DateTime T0 = new(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export batch cursor tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    /// <summary>
    /// Mid-way through an export, with as many Create exports already exported (behind the cursor) as
    /// still pending (ahead of it), the batch read must seek the index to the cursor and read one page
    /// in index order: the cursor's CreatedAt in the index condition and no sort. Checked under both
    /// plan modes, since the worker's unprepared statements get a custom plan and a prepared one would
    /// get a generic plan, which knows nothing of the cursor's value.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExecutableExportBatchQuery_MidRunCursor_SeeksTheIndexToTheCursorAsync(bool genericPlan)
    {
        var connectedSystemId = await SeedConnectedSystemAsync();
        await SeedCreateExportsAsync(connectedSystemId, ExportedCount, PendingCount);
        var (cursorCreatedAt, cursorId) = await LastExportedRowAsync(connectedSystemId);

        var plan = await ExplainBatchQueryAsync(connectedSystemId, cursorCreatedAt, cursorId, genericPlan);

        var indexConditions = plan.Split('\n').Where(line => line.Contains("Index Cond:")).ToList();
        Assert.That(indexConditions.Any(line => line.Contains("\"CreatedAt\"")), Is.True,
            $"the cursor's CreatedAt must bound the index scan, not filter its rows afterwards. Plan:\n{plan}");
        Assert.That(plan, Does.Not.Contain("Sort"),
            $"one page must be read in index order, not fetched whole and sorted. Plan:\n{plan}");
    }

    /// <summary>
    /// Rows sharing the cursor's CreatedAt must still page on Id: those after the cursor's Id are in the
    /// next batch, those at or before it are not. Guards the timestamp bound that lets the index seek
    /// against dropping or repeating a row at a timestamp tie.
    /// </summary>
    [Test]
    public async Task GetExecutableExportBatchAsync_RowsSharingTheCursorsCreatedAt_PagesThemOnIdAsync()
    {
        var connectedSystemId = await SeedConnectedSystemAsync();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToList();
        var tiedAt = T0.AddMinutes(1);
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO "PendingExports" ("Id", "ConnectedSystemId", "ChangeType", "Status", "ErrorCount", "HasUnresolvedReferences", "CreatedAt")
                SELECT id, @cs, @create, @pending, 0, false, @at FROM unnest(@ids) AS id
                UNION ALL
                SELECT gen_random_uuid(), @cs, @create, @pending, 0, false, @before
                UNION ALL
                SELECT gen_random_uuid(), @cs, @create, @pending, 0, false, @after
                """, connection);
            insert.Parameters.AddWithValue("cs", connectedSystemId);
            insert.Parameters.AddWithValue("create", (int)PendingExportChangeType.Create);
            insert.Parameters.AddWithValue("pending", (int)PendingExportStatus.Pending);
            insert.Parameters.AddWithValue("at", tiedAt);
            insert.Parameters.AddWithValue("before", tiedAt.AddMilliseconds(-1));
            insert.Parameters.AddWithValue("after", tiedAt.AddMilliseconds(1));
            insert.Parameters.AddWithValue("ids", ids.ToArray());
            await insert.ExecuteNonQueryAsync();
        }

        await using var ctx = NewContext();
        var batch = await new PostgresDataRepository(ctx).ConnectedSystems
            .GetExecutableExportBatchAsync(connectedSystemId, take: 100, afterCreatedAt: tiedAt, afterId: ids[2]);

        Assert.That(batch.Select(pe => pe.Id).Take(2), Is.EqualTo(ids.Skip(3)),
            "the tied rows after the cursor's Id come first, in Id order");
        Assert.That(batch, Has.Count.EqualTo(3), "the two tied rows after the cursor, then the later row; nothing at or before the cursor");
        Assert.That(batch[2].CreatedAt, Is.EqualTo(tiedAt.AddMilliseconds(1)));
    }

    // ─── Helpers ───

    /// <summary>
    /// Seeds a Connected System and returns its id only, so no detached parent graph leaks into a later context.
    /// </summary>
    private async Task<int> SeedConnectedSystemAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        return system.Id;
    }

    /// <summary>
    /// Seeds Create exports a millisecond apart, the first <paramref name="exportedCount"/> already Exported, as an
    /// export run part-way through leaves them; then refreshes the planner's statistics. One statement whatever the
    /// counts.
    /// </summary>
    private async Task SeedCreateExportsAsync(int connectedSystemId, int exportedCount, int pendingCount)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO "PendingExports" ("Id", "ConnectedSystemId", "ChangeType", "Status", "ErrorCount", "HasUnresolvedReferences", "CreatedAt")
            SELECT gen_random_uuid(), @cs, @create, CASE WHEN n <= @exported THEN @exportedStatus ELSE @pending END, 0, false,
                   @t0 + n * interval '1 millisecond'
            FROM generate_series(1, @total) AS n;
            ANALYZE "PendingExports";
            """, connection);
        insert.Parameters.AddWithValue("cs", connectedSystemId);
        insert.Parameters.AddWithValue("create", (int)PendingExportChangeType.Create);
        insert.Parameters.AddWithValue("exportedStatus", (int)PendingExportStatus.Exported);
        insert.Parameters.AddWithValue("pending", (int)PendingExportStatus.Pending);
        insert.Parameters.AddWithValue("exported", exportedCount);
        insert.Parameters.AddWithValue("total", exportedCount + pendingCount);
        insert.Parameters.AddWithValue("t0", T0);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<(DateTime CreatedAt, Guid Id)> LastExportedRowAsync(int connectedSystemId)
    {
        await using var ctx = NewContext();
        var last = await ctx.PendingExports
            .Where(pe => pe.ConnectedSystemId == connectedSystemId && pe.Status == PendingExportStatus.Exported)
            .OrderByDescending(pe => pe.CreatedAt).ThenByDescending(pe => pe.Id)
            .Select(pe => new { pe.CreatedAt, pe.Id })
            .FirstAsync();
        return (last.CreatedAt, last.Id);
    }

    /// <summary>
    /// The plan PostgreSQL chooses for the batch read's root query, exactly as the repository builds it. A custom plan
    /// is made for the cursor's values, as the worker's unprepared statements get. A generic plan is made without
    /// them, as a prepared statement may get: the query is prepared with positional placeholders and explained under
    /// <c>plan_cache_mode = force_generic_plan</c>.
    /// </summary>
    private async Task<string> ExplainBatchQueryAsync(int connectedSystemId, DateTime cursorCreatedAt, Guid cursorId, bool genericPlan)
    {
        await using var ctx = NewContext();
        await ctx.Database.OpenConnectionAsync();

        var repository = (ConnectedSystemRepository)new PostgresDataRepository(ctx).ConnectedSystems;
        await using var explain = repository
            .ExecutableExportBatchQuery(connectedSystemId, take: 100, cursorCreatedAt, cursorId)
            .CreateDbCommand();

        if (genericPlan)
        {
            var parameters = explain.Parameters.Cast<System.Data.Common.DbParameter>().ToList();
            var sql = explain.CommandText;
            // Longest name first, so a name that prefixes another (@p, @pending) is not replaced inside it.
            foreach (var parameter in parameters.OrderByDescending(p => p.ParameterName.Length))
                sql = sql.Replace("@" + parameter.ParameterName.TrimStart('@'), "$" + (parameters.IndexOf(parameter) + 1));

            await using (var prepare = explain.Connection!.CreateCommand())
            {
                prepare.CommandText = "SET plan_cache_mode = force_generic_plan";
                await prepare.ExecuteNonQueryAsync();
                prepare.CommandText = "PREPARE export_batch AS " + sql;
                await prepare.ExecuteNonQueryAsync();
            }

            // The values only satisfy EXECUTE; a forced generic plan is made without them.
            var arguments = parameters.Select(p => p.Value switch
            {
                DateTime at => $"'{at:O}'",
                Guid id => $"'{id}'",
                var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            });
            explain.Parameters.Clear();
            explain.CommandText = $"EXPLAIN EXECUTE export_batch({string.Join(", ", arguments)})";
        }
        else
        {
            explain.CommandText = "EXPLAIN " + explain.CommandText;
        }

        var plan = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(0));
        return string.Join("\n", plan);
    }
}
