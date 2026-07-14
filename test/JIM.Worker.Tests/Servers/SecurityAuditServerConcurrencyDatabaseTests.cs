// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that concurrent failed-authentication recording preserves every attempt in the
/// aggregated count. The write-shape under a genuinely concurrent spray (per-request DI scopes racing on the same
/// window bucket's first row) cannot be exercised by the in-memory provider or single-threaded tests: the losers of
/// the insert race hit the partial unique index and fall back to an atomic increment, and any second full-row write
/// of the winner's stale in-memory Activity (for example a create-then-complete lifecycle) silently erases those
/// concurrent increments; a lost update, not an exception. Runs one caller per fresh context, mirroring the
/// per-request scopes in production.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent. The fixture truncates every table per test, so the target
/// database must be a scratch database, never a live one.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SecurityAuditServerConcurrencyDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL security audit concurrency tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_ConcurrentSprayInOneWindow_PreservesEveryAttemptInOneRowAsync()
    {
        // Enough parallel callers that several lose the window's insert race and take the increment fallback while
        // the winner is still completing its own write path; the lost-update window is all but guaranteed to be hit.
        const int concurrentAttempts = 24;

        var tasks = Enumerable.Range(0, concurrentAttempts).Select(_ => Task.Run(async () =>
        {
            // One fresh context and facade per caller, mirroring the fresh per-request DI scopes production uses
            // for fire-and-forget audit capture.
            await using var ctx = NewContext();
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.SecurityAudit.RecordFailedAuthenticationAsync(
                "API key authentication failed", "API key not found", "jim_ak_race1", "203.0.113.9");
        }));
        await Task.WhenAll(tasks);

        await using var verify = NewContext();
        var rows = await verify.Activities
            .Where(a => a.TargetType == ActivityTargetType.Authentication)
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1),
            "a concurrent spray on one (prefix, IP, reason, window) bucket must aggregate onto a single row");
        Assert.That(rows[0].AttemptCount, Is.EqualTo(concurrentAttempts),
            "every attempt must be preserved in the aggregated count: concurrent increments must not be lost to a " +
            "second full-row write of the window-creating caller's stale in-memory Activity");
        Assert.That(rows[0].Status, Is.EqualTo(ActivityStatus.Complete),
            "aggregated audit rows are point-in-time records and must be persisted already complete");
    }
}
