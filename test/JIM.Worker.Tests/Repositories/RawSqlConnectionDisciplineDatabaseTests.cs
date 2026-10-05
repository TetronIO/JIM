// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that raw-SQL repository helpers close the connections they open.
/// EF Core only auto-closes connections it opened itself; a connection opened by repository code
/// stays checked out of the Npgsql pool until the DbContext is disposed. The parallel export path
/// creates a context per batch and runs these helpers on it, so an unclosed connection pins one
/// pooled connection per batch and exhausts the pool (Max Pool Size 30) - the
/// Scale200k10kGroups export failure of 2026-07-13 (batch 29 onwards all failed with
/// "The connection pool has been exhausted").
/// </summary>
/// <remarks>
/// Neither a mocked DbContext nor the EF in-memory provider exercises real connections, so only a
/// real database run can pin this. The two methods tested here are representative of the two raw
/// shapes (COPY bulk write, NpgsqlBatch read); the discipline applies to every
/// <c>GetDbConnection()</c> site. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment
/// variables as the other <c>RequiresPostgres</c> fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class RawSqlConnectionDisciplineDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL raw SQL connection discipline tests.");

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

    private async Task<(Guid CsoId, Guid PeId, Guid AvcId, int MailAttrId)> SeedPendingExportAsync(string systemName = "Yellowstone HR")
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector ({systemName})", BuiltIn = true };
        var system = new ConnectedSystem { Name = systemName, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(mailAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = mailAttr.Id
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        var avc = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttr.Id,
            StringValue = "test.user@example.com",
            ChangeType = PendingExportAttributeChangeType.Update
        };
        pe.AttributeValueChanges.Add(avc);
        seed.Add(pe);
        await seed.SaveChangesAsync();

        return (cso.Id, pe.Id, avc.Id, mailAttr.Id);
    }

    /// <summary>
    /// The COPY-based bulk Pending Export update (the parallel export batch persist path) must
    /// close the connection it opened, or each per-batch context pins a pooled connection.
    /// </summary>
    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_AfterCompletion_LeavesConnectionClosedAsync()
    {
        var (csoId, peId, avcId, mailAttrId) = await SeedPendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var untrackedUpdate = new PendingExport
        {
            Id = peId,
            ConnectedSystemObjectId = csoId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            {
                new PendingExportAttributeValueChange
                {
                    Id = avcId,
                    AttributeId = mailAttrId,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1
                }
            }
        };

        await repository.Sync.UpdateUntrackedPendingExportsAsync([untrackedUpdate]);

        Assert.That(ctx.Database.GetDbConnection().State, Is.EqualTo(System.Data.ConnectionState.Closed),
            "The raw COPY helper must close the connection it opened; an open connection stays checked out of the pool for the context's lifetime.");
    }

    /// <summary>
    /// The NpgsqlBatch-based cross-page RPEI merge fetch must close the connection it opened.
    /// </summary>
    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_AfterCompletion_LeavesConnectionClosedAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Sync.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
            Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.That(result, Is.Empty, "No RPEIs are seeded; the fetch should return empty.");
        Assert.That(ctx.Database.GetDbConnection().State, Is.EqualTo(System.Data.ConnectionState.Closed),
            "The raw batch-read helper must close the connection it opened.");
    }

    /// <summary>
    /// The bulk Pending Export update stages rows in session-scoped temp tables (ON COMMIT PRESERVE
    /// ROWS) that are only dropped on its success path, so a call that fails part-way leaves its rows
    /// on the physical connection. The next call must not apply them: it must not rely on Npgsql's
    /// pool reset (DISCARD ALL) having run in between, because nothing guarantees that (a connection
    /// held open across calls, or No Reset On Close). Applying them would silently write an earlier
    /// call's values over Pending Exports the current call never touched.
    /// </summary>
    [Test]
    public async Task UpdateUntrackedPendingExportsAsync_LeftoverRowsFromFailedCallOnSameSession_AreNotAppliedAsync()
    {
        var target = await SeedPendingExportAsync("Yellowstone HR");
        var bystander = await SeedPendingExportAsync("Glitterband HR");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Hold the physical connection open across both calls, so no pool reset runs between them.
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.ExecuteSqlRawAsync("SET lock_timeout = '500ms'");

        // Make a call for the bystander fail after both COPYs have staged its rows: another session
        // holds its attribute change row, so the attribute change UPDATE times out before the cleanup.
        await using (var locker = new NpgsqlConnection(_connectionString))
        {
            await locker.OpenAsync();
            await using var lockTransaction = await locker.BeginTransactionAsync();
            await using (var lockCmd = new NpgsqlCommand(
                """SELECT 1 FROM "PendingExportAttributeValueChanges" WHERE "Id" = @id FOR UPDATE""", locker, lockTransaction))
            {
                lockCmd.Parameters.AddWithValue("id", bystander.AvcId);
                await lockCmd.ExecuteNonQueryAsync();
            }

            var failedCall = BuildUntrackedUpdate(bystander, PendingExportStatus.Exported, errorCount: 0,
                PendingExportAttributeChangeStatus.ExportedPendingConfirmation, exportAttemptCount: 1);
            var ex = Assert.ThrowsAsync<PostgresException>(() => repository.Sync.UpdateUntrackedPendingExportsAsync([failedCall]));
            Assert.That(ex!.SqlState, Is.EqualTo(PostgresErrorCodes.LockNotAvailable),
                "Test setup: the bystander call must fail at the attribute change UPDATE, after its rows were staged.");

            await lockTransaction.RollbackAsync();
        }

        await ctx.Database.ExecuteSqlRawAsync("RESET lock_timeout");

        // The bystander then moves on independently of the failed call.
        await using (var other = NewContext())
        {
            await other.PendingExports.Where(pe => pe.Id == bystander.PeId).ExecuteUpdateAsync(s => s
                .SetProperty(pe => pe.Status, PendingExportStatus.Failed)
                .SetProperty(pe => pe.ErrorCount, 3));
            await other.PendingExportAttributeValueChanges.Where(avc => avc.Id == bystander.AvcId).ExecuteUpdateAsync(s => s
                .SetProperty(avc => avc.Status, PendingExportAttributeChangeStatus.Failed)
                .SetProperty(avc => avc.ExportAttemptCount, 3));
        }

        // Act: an unrelated update on the same session.
        await repository.Sync.UpdateUntrackedPendingExportsAsync([
            BuildUntrackedUpdate(target, PendingExportStatus.Exported, errorCount: 0,
                PendingExportAttributeChangeStatus.ExportedPendingConfirmation, exportAttemptCount: 1)
        ]);

        await using var verify = NewContext();
        var bystanderPe = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == bystander.PeId);
        var bystanderAvc = await verify.PendingExportAttributeValueChanges.AsNoTracking().SingleAsync(avc => avc.Id == bystander.AvcId);
        var targetPe = await verify.PendingExports.AsNoTracking().SingleAsync(pe => pe.Id == target.PeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targetPe.Status, Is.EqualTo(PendingExportStatus.Exported), "The current call's own update must apply.");
            Assert.That(bystanderPe.Status, Is.EqualTo(PendingExportStatus.Failed),
                "A leftover row from the failed call reverted an unrelated Pending Export's Status.");
            Assert.That(bystanderPe.ErrorCount, Is.EqualTo(3),
                "A leftover row from the failed call reverted an unrelated Pending Export's ErrorCount.");
            Assert.That(bystanderAvc.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Failed),
                "A leftover row from the failed call reverted an unrelated attribute change's Status.");
            Assert.That(bystanderAvc.ExportAttemptCount, Is.EqualTo(3),
                "A leftover row from the failed call reverted an unrelated attribute change's ExportAttemptCount.");
        }
    }

    private static PendingExport BuildUntrackedUpdate(
        (Guid CsoId, Guid PeId, Guid AvcId, int MailAttrId) seeded,
        PendingExportStatus status,
        int errorCount,
        PendingExportAttributeChangeStatus attributeChangeStatus,
        int exportAttemptCount) => new()
    {
        Id = seeded.PeId,
        ConnectedSystemObjectId = seeded.CsoId,
        ChangeType = PendingExportChangeType.Update,
        Status = status,
        ErrorCount = errorCount,
        AttributeValueChanges =
        {
            new PendingExportAttributeValueChange
            {
                Id = seeded.AvcId,
                AttributeId = seeded.MailAttrId,
                Status = attributeChangeStatus,
                ExportAttemptCount = exportAttemptCount
            }
        }
    };
}
