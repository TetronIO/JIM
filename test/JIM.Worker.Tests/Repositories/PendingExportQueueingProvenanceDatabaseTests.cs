// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that a Pending Export keeps the Run Profile Execution Item that queued it
/// (#1223), which is what lets the export run days later say why it exported anything.
/// <para>
/// Only a real database can prove this. Pending Exports are staged by raw SQL that bypasses the EF model, so a
/// column present on the model but missed by the writer, or written out of position, persists as null with no
/// error anywhere: the export would run, find nothing to blame, and record no cause. The in-memory provider
/// goes through EF and structurally cannot see either fault.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run this fixture outside the sanctioned
/// scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportQueueingProvenanceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export provenance tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
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
    /// The queueing item survives the raw-SQL bulk staging path.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_RecordsTheQueueingRunProfileExecutionItemAsync()
    {
        var systemId = await SeedSystemAsync();
        var queueingItemId = Guid.NewGuid();
        var exportId = Guid.NewGuid();

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = exportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                QueuedByRunProfileExecutionItemId = queueingItemId
            }
        ]);

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == exportId);
        Assert.That(stored.QueuedByRunProfileExecutionItemId, Is.EqualTo(queueingItemId));
    }

    /// <summary>
    /// The column deliberately carries no foreign key: the Activity that staged an export ages out of history
    /// long before the export it explains, and a cascade would delete the very answer to "why did this happen".
    /// An id pointing at nothing must therefore store cleanly rather than fail the write.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_QueueingItemNoLongerRetained_StoresTheIdAnywayAsync()
    {
        var systemId = await SeedSystemAsync();
        var purgedItemId = Guid.NewGuid();
        var exportId = Guid.NewGuid();

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = exportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Delete,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                QueuedByRunProfileExecutionItemId = purgedItemId
            }
        ]);

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == exportId);
        Assert.That(stored.QueuedByRunProfileExecutionItemId, Is.EqualTo(purgedItemId));
    }

    /// <summary>
    /// A staging path with no execution item in hand stores a null, which is the "cause not recorded" case the
    /// export path degrades to rather than inventing one.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_WithNoQueueingItem_StoresNullAsync()
    {
        var systemId = await SeedSystemAsync();
        var exportId = Guid.NewGuid();

        await using var write = NewContext();
        await new PostgresDataRepository(write).Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = exportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == exportId);
        Assert.That(stored.QueuedByRunProfileExecutionItemId, Is.Null);
    }

    /// <summary>
    /// The retry and reconciliation update paths must leave the queueing item alone: it is a fact about how the
    /// export came to exist, and a partial update that omitted it from its column list would blank it.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_AfterAFailedAttempt_KeepsTheQueueingItemAsync()
    {
        var systemId = await SeedSystemAsync();
        var queueingItemId = Guid.NewGuid();
        var exportId = Guid.NewGuid();

        await using var write = NewContext();
        var repository = new PostgresDataRepository(write);
        await repository.Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = exportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                QueuedByRunProfileExecutionItemId = queueingItemId
            }
        ]);

        await using var update = NewContext();
        var updateRepository = new PostgresDataRepository(update);
        var toRetry = await update.PendingExports.SingleAsync(pe => pe.Id == exportId);
        toRetry.Status = PendingExportStatus.ExportNotConfirmed;
        toRetry.ErrorCount = 1;
        toRetry.LastErrorMessage = "the connector rejected the change";
        await updateRepository.Sync.UpdatePendingExportsAsync([toRetry]);

        await using var verify = NewContext();
        var stored = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == exportId);
        Assert.Multiple(() =>
        {
            Assert.That(stored.ErrorCount, Is.EqualTo(1), "the update itself must have taken");
            Assert.That(stored.QueuedByRunProfileExecutionItemId, Is.EqualTo(queueingItemId));
        });
    }

    /// <summary>
    /// The set-once fix-up used by the deletion-cascade and recall paths, whose Pending Exports are persisted
    /// before the item reporting them exists. Raw SQL, so only a real database can prove the array-parameter
    /// UPDATE actually lands the right id on the right row.
    /// </summary>
    [Test]
    public async Task SetPendingExportQueueingItemsAsync_PersistedExports_StampsEachWithItsOwnItemAsync()
    {
        var systemId = await SeedSystemAsync();
        var firstExportId = Guid.NewGuid();
        var secondExportId = Guid.NewGuid();
        var firstItemId = Guid.NewGuid();
        var secondItemId = Guid.NewGuid();

        await using var write = NewContext();
        var repository = new PostgresDataRepository(write);
        await repository.Sync.CreatePendingExportsAsync([
            new PendingExport
            {
                Id = firstExportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Delete,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            },
            new PendingExport
            {
                Id = secondExportId,
                ConnectedSystemId = systemId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            }
        ]);

        await repository.Sync.SetPendingExportQueueingItemsAsync(
            [(firstExportId, firstItemId), (secondExportId, secondItemId)]);

        await using var verify = NewContext();
        var stamped = await verify.PendingExports.AsNoTracking()
            .ToDictionaryAsync(pe => pe.Id, pe => pe.QueuedByRunProfileExecutionItemId);
        Assert.Multiple(() =>
        {
            Assert.That(stamped[firstExportId], Is.EqualTo(firstItemId));
            Assert.That(stamped[secondExportId], Is.EqualTo(secondItemId));
        });
    }

    private async Task<int> SeedSystemAsync()
    {
        await using var ctx = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        ctx.AddRange(connectorDefinition, connectedSystem);
        await ctx.SaveChangesAsync();
        return connectedSystem.Id;
    }
}
