// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Real-PostgreSQL verification of the #288 preview backstops (PRD requirements 8 and 10, plan Phase 2): the
/// rollback-only transaction genuinely discards anything written inside it, and an outbound preview leaves the
/// synchronisation-integrity tables byte-identical, proven with the Phase 0 isolation snapshot rather than
/// asserted by inspection. The in-memory provider holds no transactions and cannot run the digest SQL, so only
/// this fixture can prove either.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class OutboundPreviewIsolationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL preview backstop tests.");

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
    public async Task BeginRollbackOnlyTransactionAsync_AWriteInsideTheScope_IsDiscardedOnDisposalAsync()
    {
        // The backstop's whole promise: a write that slipped every other preview guard is discarded, not
        // committed, because disposal rolls back unconditionally.
        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            var rollbackScope = await jim.SyncRepository.BeginRollbackOnlyTransactionAsync();
            Assert.That(rollbackScope, Is.Not.Null, "A relational provider must supply the transaction backstop");

            ctx.Activities.Add(new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = "Preview backstop probe",
                Created = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();

            await rollbackScope!.DisposeAsync();
        }

        await using var verifyCtx = NewContext();
        Assert.That(await verifyCtx.Activities.CountAsync(), Is.EqualTo(0),
            "The rollback-only transaction must discard everything written inside it");
    }

    [Test]
    public async Task EvaluateOutboundPreviewAsync_AgainstALiveDatabase_LeavesTheIntegrityTablesByteIdenticalAsync()
    {
        // Seed a row so the snapshot digests real content rather than eight empty tables.
        await using (var seedCtx = NewContext())
        {
            seedCtx.Activities.Add(new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = "Pre-existing Activity",
                Created = DateTime.UtcNow
            });
            await seedCtx.SaveChangesAsync();
        }

        var before = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);

        await using (var ctx = NewContext())
        {
            // The sync repository is passed explicitly, as every DI registration does; the bootstrap-only
            // default leaves JimApplication.SyncRepo null.
            var repo = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repo, syncRepository: new JIM.PostgresData.Repositories.SyncRepository(repo));
            var result = await jim.ExportEvaluation.EvaluateOutboundPreviewAsync([Guid.NewGuid()]);
            Assert.That(result.SkippedMetaverseObjectCount, Is.EqualTo(1),
                "An unknown Metaverse Object id is reported as skipped, not an error");
        }

        var after = await DatabaseIsolationSnapshot.CaptureAsync(_connectionString);
        Assert.That(() => after.AssertUnchangedSince(before), Throws.Nothing);
    }
}
