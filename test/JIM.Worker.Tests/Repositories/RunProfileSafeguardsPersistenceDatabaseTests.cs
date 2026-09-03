// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that a Run Profile's safeguards (#1618) survive the repository's update
/// path. <c>ConnectedSystemRepository.UpdateConnectedSystemRunProfileAsync</c> re-fetches the tracked row
/// and copies the caller's properties onto it one by one, so a column added to the entity but not to that
/// copy is silently dropped on every update: the API accepts the limit, reports success, and persists
/// nothing. Neither the in-memory harness nor the mocked API tests can see that, which is how Scenario 21
/// found it at runtime. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run this
/// fixture outside the sanctioned scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RunProfileSafeguardsPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Run Profile safeguards persistence tests.");

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

    /// <summary>
    /// Seeds a Connected System with one Export Run Profile carrying no limits, and returns the Run
    /// Profile's id.
    /// </summary>
    private async Task<int> SeedExportRunProfileAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Contoso AD", ConnectorDefinition = connectorDefinition };
        var runProfile = new ConnectedSystemRunProfile { Name = "Export to AD", RunType = ConnectedSystemRunType.Export, ConnectedSystemId = 0 };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        runProfile.ConnectedSystemId = system.Id;
        seed.Add(runProfile);
        await seed.SaveChangesAsync();
        return runProfile.Id;
    }

    private async Task<ConnectedSystemRunProfile> ReadBackAsync(int runProfileId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemRunProfiles.AsNoTracking().SingleAsync(rp => rp.Id == runProfileId);
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_LimitsSetOnADetachedCopy_PersistsAllThreeAsync()
    {
        var runProfileId = await SeedExportRunProfileAsync();

        // The API and the portal both hand the repository a Run Profile loaded on another context, so the
        // update has to copy the limits across rather than rely on change tracking.
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var detached = await ctx.ConnectedSystemRunProfiles.AsNoTracking().SingleAsync(rp => rp.Id == runProfileId);
            detached.MaxCreates = 5;
            detached.MaxUpdates = 0;
            detached.MaxDeletes = 100;
            await repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(detached);
        }

        var persisted = await ReadBackAsync(runProfileId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.MaxCreates, Is.EqualTo(5), "Max creates must survive the update's property copy");
            Assert.That(persisted.MaxUpdates, Is.EqualTo(0), "A limit of zero is a real value and must survive the update");
            Assert.That(persisted.MaxDeletes, Is.EqualTo(100), "Max deletes must survive the update's property copy");
        }
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_LimitsClearedOnADetachedCopy_PersistsTheClearAsync()
    {
        var runProfileId = await SeedExportRunProfileAsync();
        await using (var ctx = NewContext())
        {
            var tracked = await ctx.ConnectedSystemRunProfiles.SingleAsync(rp => rp.Id == runProfileId);
            tracked.MaxDeletes = 100;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var detached = await ctx.ConnectedSystemRunProfiles.AsNoTracking().SingleAsync(rp => rp.Id == runProfileId);
            detached.MaxDeletes = null;
            await repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(detached);
        }

        var persisted = await ReadBackAsync(runProfileId);
        Assert.That(persisted.MaxDeletes, Is.Null, "Clearing a limit through the update must persist as null, not keep the old value");
    }

    /// <summary>
    /// Seeds a Connected System with one Full Import Run Profile carrying no limits, and returns the
    /// Run Profile's id.
    /// </summary>
    private async Task<int> SeedFullImportRunProfileAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Contoso AD", ConnectorDefinition = connectorDefinition };
        var runProfile = new ConnectedSystemRunProfile { Name = "Full Import from AD", RunType = ConnectedSystemRunType.FullImport, ConnectedSystemId = 0 };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        runProfile.ConnectedSystemId = system.Id;
        seed.Add(runProfile);
        await seed.SaveChangesAsync();
        return runProfile.Id;
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_DeletionDetectionLimitsSetOnADetachedCopy_PersistsBothAsync()
    {
        var runProfileId = await SeedFullImportRunProfileAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var detached = await ctx.ConnectedSystemRunProfiles.AsNoTracking().SingleAsync(rp => rp.Id == runProfileId);
            detached.MaxDetectedDeletions = 500;
            detached.MaxDetectedDeletionsPercent = 0;
            await repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(detached);
        }

        var persisted = await ReadBackAsync(runProfileId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.MaxDetectedDeletions, Is.EqualTo(500), "Max detected deletions must survive the update's property copy");
            Assert.That(persisted.MaxDetectedDeletionsPercent, Is.EqualTo(0), "A limit of zero is a real value and must survive the update");
        }
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_DeletionDetectionLimitsClearedOnADetachedCopy_PersistsTheClearAsync()
    {
        var runProfileId = await SeedFullImportRunProfileAsync();
        await using (var ctx = NewContext())
        {
            var tracked = await ctx.ConnectedSystemRunProfiles.SingleAsync(rp => rp.Id == runProfileId);
            tracked.MaxDetectedDeletions = 500;
            tracked.MaxDetectedDeletionsPercent = 10;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var detached = await ctx.ConnectedSystemRunProfiles.AsNoTracking().SingleAsync(rp => rp.Id == runProfileId);
            detached.MaxDetectedDeletions = null;
            detached.MaxDetectedDeletionsPercent = null;
            await repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(detached);
        }

        var persisted = await ReadBackAsync(runProfileId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.MaxDetectedDeletions, Is.Null, "Clearing the limit through the update must persist as null, not keep the old value");
            Assert.That(persisted.MaxDetectedDeletionsPercent, Is.Null, "Clearing the limit through the update must persist as null, not keep the old value");
        }
    }
}
