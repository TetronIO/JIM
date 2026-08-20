// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Schema-level guard for the factory reset's mixed-table deletes (issue #1477).
/// <para>
/// <see cref="JIM.PostgresData.Repositories.SystemRepository.ResetSystemAsync"/> removes custom configuration with
/// a set of <c>DELETE ... WHERE "BuiltIn" = false</c> statements. Those rely entirely on foreign-key cascades to
/// clear the child rows a custom object owns; a child table whose foreign key is NO ACTION instead fails the delete
/// with <c>23503</c>, and because the whole wipe runs in one transaction, the entire reset rolls back and nothing is
/// removed. That is how #1477 was found, and the same gap was present in four further places.
/// </para>
/// <para>
/// The behavioural cover is in <see cref="SystemResetDatabaseTests"/>, but it can only exercise the child rows it
/// knows to seed. This asserts the property directly against the live schema instead, so a child table added in a
/// future release cannot silently reintroduce the class of fault: every table reachable from a delete root by
/// cascade must itself be cascade-reachable, or be listed in <see cref="DeliberateNonCascadingForeignKeys"/> with
/// the reason and the compensating step in the wipe.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SystemResetForeignKeyCoverageTests
{
    /// <summary>
    /// The tables the wipe's mixed-table deletes target directly. Parity with the <c>WHERE "BuiltIn" = false</c>
    /// (and <c>"IsInfrastructureKey" = false</c>) statements in <c>ResetSystemAsync</c>.
    /// </summary>
    private static readonly string[] DeleteRootTables =
    {
        "PredefinedSearches",
        "ExampleDataTemplates",
        "ExampleDataSets",
        "Roles",
        "MetaverseObjectTypes",
        "MetaverseAttributes",
        "ConnectorDefinitions",
        "ApiKeys"
    };

    /// <summary>
    /// The tables the wipe empties with <c>TRUNCATE ... RESTART IDENTITY CASCADE</c> before the mixed-table deletes
    /// run. Parity with <c>TruncateAllCustomerDataSql</c>. A truncate cascade empties every referencing table
    /// whatever its delete rule, so anything downstream of these holds no rows by the time the deletes run and
    /// cannot block them.
    /// </summary>
    private static readonly string[] TruncatedTables =
    {
        "WorkerTasks",
        "DeferredReferences",
        "PendingExports",
        "ConnectedSystemObjectChanges",
        "MetaverseObjectChanges",
        "ScheduleExecutions",
        "Activities",
        "Schedules",
        "SyncRules",
        "ObjectMatchingRules",
        "ConnectedSystemObjects",
        "ConnectedSystems",
        "MetaverseObjects",
        "TrustedCertificates"
    };

    /// <summary>
    /// Foreign keys that are deliberately left non-cascading, keyed by constraint name with the reason. Each must
    /// have a compensating step in <c>ResetSystemAsync</c>, or the reset will roll back when the reference exists.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DeliberateNonCascadingForeignKeys =
        new Dictionary<string, string>
        {
            ["FK_ServiceSettings_MetaverseAttributes_SSOUniqueIdentifierMeta~"] =
                "Cascading would delete the preserved Service Settings singleton along with a custom Metaverse " +
                "Attribute chosen as the SSO unique identifier. The wipe clears the reference explicitly instead."
        };

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL foreign-key coverage tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var ctx = new JimDbContext(options);
        ctx.Database.Migrate();
    }

    [Test]
    public async Task ResetSystemAsync_EveryTableReachableFromAMixedDeleteRoot_CascadesAsync()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var ctx = new JimDbContext(options);

        // Walks the foreign-key graph from each delete root through cascading edges only, and reports any
        // non-cascading edge it meets on the way. Tables emptied by the preceding truncate are excluded from both
        // the walk and the report: they hold no rows to block anything. The explicit "C" collations are required
        // because a recursive CTE will not mix the default collation with the one pg_class carries.
        const string sql = @"
            WITH RECURSIVE fk AS (
                SELECT c.conname::text COLLATE ""C"" AS conname,
                       child.relname::text COLLATE ""C"" AS child,
                       parent.relname::text COLLATE ""C"" AS parent,
                       c.confdeltype
                FROM pg_constraint c
                INNER JOIN pg_class child ON child.oid = c.conrelid
                INNER JOIN pg_class parent ON parent.oid = c.confrelid
                WHERE c.contype = 'f'
            ),
            emptied(tbl) AS (
                SELECT unnest(@truncated)::text COLLATE ""C""
                UNION
                SELECT fk.child FROM emptied e INNER JOIN fk ON fk.parent = e.tbl
            ),
            reachable(tbl, depth, path) AS (
                SELECT unnest(@roots)::text COLLATE ""C"", 0, unnest(@roots)::text COLLATE ""C""
                UNION ALL
                SELECT fk.child, r.depth + 1, (r.path || ' > ' || fk.child)::text COLLATE ""C""
                FROM reachable r INNER JOIN fk ON fk.parent = r.tbl
                WHERE r.depth < 10
                  AND position(fk.child in r.path) = 0
                  AND fk.confdeltype = 'c'
                  AND fk.child NOT IN (SELECT tbl FROM emptied)
            )
            SELECT DISTINCT (r.path || ' -> ' || fk.child || '  [' || fk.conname || ']')::text COLLATE ""C"" AS ""Value""
            FROM reachable r INNER JOIN fk ON fk.parent = r.tbl
            WHERE fk.confdeltype <> 'c'
              AND fk.child NOT IN (SELECT tbl FROM emptied)
              AND fk.conname <> ALL(@allowed)
            ORDER BY 1;";

        var blocking = await ctx.Database.SqlQueryRaw<string>(
            sql,
            new Npgsql.NpgsqlParameter("truncated", TruncatedTables),
            new Npgsql.NpgsqlParameter("roots", DeleteRootTables),
            new Npgsql.NpgsqlParameter("allowed", DeliberateNonCascadingForeignKeys.Keys.ToArray()))
            .ToListAsync();

        Assert.That(blocking, Is.Empty,
            "each of these foreign keys will fail a factory reset with 23503 once the referencing row exists, " +
            "rolling the whole reset back. Make it cascade, or add it to DeliberateNonCascadingForeignKeys with a " +
            "compensating step in ResetSystemAsync:" + Environment.NewLine + string.Join(Environment.NewLine, blocking));
    }

    /// <summary>
    /// Guards the allow-list itself: an entry naming a constraint that no longer exists is stale, and would hide a
    /// genuinely blocking foreign key if the name were ever reused.
    /// </summary>
    [Test]
    public async Task DeliberateNonCascadingForeignKeys_AllStillExistAsync()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var ctx = new JimDbContext(options);

        var existing = await ctx.Database.SqlQueryRaw<string>(
            @"SELECT conname::text AS ""Value"" FROM pg_constraint WHERE contype = 'f';").ToListAsync();

        var stale = DeliberateNonCascadingForeignKeys.Keys.Where(name => !existing.Contains(name)).ToList();
        Assert.That(stale, Is.Empty, "stale allow-list entries: " + string.Join(", ", stale));
    }
}
