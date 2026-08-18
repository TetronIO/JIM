// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Support;

/// <summary>
/// Real-PostgreSQL verification of the preview isolation snapshot (#288, PRD requirement 10): that a capture
/// reflects the database it was taken over, that an insert and an in-place update are both detected, and that a
/// watched table going missing fails loudly instead of silently weakening the guarantee. The in-memory provider
/// cannot execute the raw digest SQL, so only this fixture can prove any of it.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DatabaseIsolationSnapshotDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL isolation snapshot tests.");

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
    public async Task Capture_NothingHappensBetweenCaptures_AssertsUnchangedAsync()
    {
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
    }

    [Test]
    public async Task Capture_ActivityInsertedBetweenCaptures_FailsNamingActivitiesAsync()
    {
        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        await using (var ctx = NewContext())
        {
            ctx.Activities.Add(new Activity
            {
                TargetOperationType = ActivityTargetOperationType.Update,
                Status = ActivityStatus.Complete
            });
            await ctx.SaveChangesAsync();
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        Assert.That(() => after.AssertUnchangedSince(before),
            Throws.InstanceOf<AssertionException>().With.Message.Contain("Activities"));
    }

    [Test]
    public async Task Capture_RowUpdatedInPlaceWithNoCountChange_StillFailsAsync()
    {
        // The case a count-only snapshot cannot see: same number of rows, different content. A preview that
        // rewrote an Activity's message would otherwise pass the isolation assertion.
        await using (var ctx = NewContext())
        {
            ctx.Activities.Add(new Activity
            {
                TargetOperationType = ActivityTargetOperationType.Update,
                Status = ActivityStatus.Complete,
                Message = "before"
            });
            await ctx.SaveChangesAsync();
        }

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"Activities\" SET \"Message\" = 'after'");

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        Assert.That(() => after.AssertUnchangedSince(before),
            Throws.InstanceOf<AssertionException>()
                .With.Message.Contain("Activities").And.Message.Contain("content changed"));
    }

    [Test]
    public void Capture_WatchedTableDoesNotExist_ThrowsNamingIt()
    {
        // A schema rename must break the snapshot loudly. Skipping a missing table would leave every isolation
        // assertion in the suite quietly watching seven tables while claiming to watch eight.
        Assert.That(async () => await DatabaseIsolationSnapshot.CaptureAsync(
                _connectionString, ["Activities", "NoSuchTable"]),
            Throws.InvalidOperationException.With.Message.Contain("NoSuchTable"));
    }
}
