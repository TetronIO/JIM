// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <c>ISyncRepository.RecoverStrandedExecutingPendingExportsAsync</c>, the
/// crash-recovery statement called once at Worker startup for Pending Exports left in Status Executing by
/// a worker crash or restart mid-export. Removing the old synchronisation-side confirmation check (which
/// used to flip these back to ExportNotConfirmed by accident) left nothing else to unstick them: neither
/// the export queue (<c>GetExecutableExportsAsync</c> takes Pending/Exported/ExportNotConfirmed only) nor
/// import reconciliation (which now deliberately excludes Executing) will ever touch them again without
/// this recovery.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportStrandedExecutingRecoveryDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL stranded Executing Pending Export recovery tests.");

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

    private sealed record RecoverySeed(
        Guid SentChangeExecutingId,
        Guid PendingOnlyExecutingId,
        Guid PendingId,
        Guid ExportedId,
        Guid ExportNotConfirmedId,
        Guid FailedId,
        Guid ParkedId);

    /// <summary>
    /// Seeds one Connected System with a Pending Export per scenario: two genuinely stranded Executing
    /// exports (one with a change already sent, one with nothing sent yet) and one Pending Export per
    /// every other status, none of which the recovery may touch.
    /// </summary>
    private async Task<RecoverySeed> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "jimPerson", ConnectedSystem = connectedSystem, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        objectType.Attributes.Add(mailAttr);
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        PendingExport MakePendingExport(PendingExportStatus status, PendingExportAttributeChangeStatus? changeStatus)
        {
            var cso = new ConnectedSystemObject { Type = objectType, ConnectedSystem = connectedSystem, Status = ConnectedSystemObjectStatus.Normal };
            seed.Add(cso);

            var pe = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystem.Id,
                ConnectedSystemObject = cso,
                ChangeType = PendingExportChangeType.Update,
                Status = status,
                ErrorCount = 3,
                CreatedAt = DateTime.UtcNow
            };
            if (changeStatus.HasValue)
            {
                pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = mailAttr.Id,
                    StringValue = "user@example.com",
                    ChangeType = PendingExportAttributeChangeType.Update,
                    Status = changeStatus.Value
                });
            }
            seed.Add(pe);
            return pe;
        }

        var sentChangeExecuting = MakePendingExport(PendingExportStatus.Executing, PendingExportAttributeChangeStatus.ExportedPendingConfirmation);
        var pendingOnlyExecuting = MakePendingExport(PendingExportStatus.Executing, PendingExportAttributeChangeStatus.Pending);
        var pending = MakePendingExport(PendingExportStatus.Pending, PendingExportAttributeChangeStatus.Pending);
        var exported = MakePendingExport(PendingExportStatus.Exported, PendingExportAttributeChangeStatus.ExportedPendingConfirmation);
        var exportNotConfirmed = MakePendingExport(PendingExportStatus.ExportNotConfirmed, PendingExportAttributeChangeStatus.ExportedNotConfirmed);
        var failed = MakePendingExport(PendingExportStatus.Failed, PendingExportAttributeChangeStatus.Failed);
        var parked = MakePendingExport(PendingExportStatus.Parked, PendingExportAttributeChangeStatus.ExportedNotConfirmed);

        await seed.SaveChangesAsync();

        return new RecoverySeed(sentChangeExecuting.Id, pendingOnlyExecuting.Id, pending.Id, exported.Id, exportNotConfirmed.Id, failed.Id, parked.Id);
    }

    [Test]
    public async Task RecoverStrandedExecutingPendingExportsAsync_MovesExecutingByWhetherAChangeWasSentAndLeavesEverythingElseAloneAsync()
    {
        var recoverySeed = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var recoveredCount = await repository.Sync.RecoverStrandedExecutingPendingExportsAsync();

        Assert.That(recoveredCount, Is.EqualTo(2), "Only the two genuinely stranded Executing Pending Exports should be recovered.");

        await using var verify = NewContext();
        var all = await verify.PendingExports.ToDictionaryAsync(pe => pe.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(all[recoverySeed.SentChangeExecutingId].Status, Is.EqualTo(PendingExportStatus.Exported),
                "An Executing export with an already-sent change must move to Exported so the next confirming import reconciles it.");
            Assert.That(all[recoverySeed.PendingOnlyExecutingId].Status, Is.EqualTo(PendingExportStatus.Pending),
                "An Executing export with nothing sent yet must move to Pending so the next export run retries it.");

            Assert.That(all[recoverySeed.PendingId].Status, Is.EqualTo(PendingExportStatus.Pending), "A Pending export must be left alone.");
            Assert.That(all[recoverySeed.ExportedId].Status, Is.EqualTo(PendingExportStatus.Exported), "An Exported export must be left alone.");
            Assert.That(all[recoverySeed.ExportNotConfirmedId].Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed), "An ExportNotConfirmed export must be left alone.");
            Assert.That(all[recoverySeed.FailedId].Status, Is.EqualTo(PendingExportStatus.Failed), "A Failed export must be left alone.");
            Assert.That(all[recoverySeed.ParkedId].Status, Is.EqualTo(PendingExportStatus.Parked), "A Parked export must be left alone.");

            // ErrorCount is never touched by this recovery, recovered or not.
            foreach (var pe in all.Values)
                Assert.That(pe.ErrorCount, Is.EqualTo(3), $"ErrorCount must never be touched by recovery (Pending Export {pe.Id}).");
        }
    }

    [Test]
    public async Task RecoverStrandedExecutingPendingExportsAsync_NoExecutingExports_ReturnsZeroAsync()
    {
        await SeedAsync();

        // Move the two Executing seeds out of the way first so this run genuinely has none.
        await using (var setup = NewContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                @"UPDATE ""PendingExports"" SET ""Status"" = {0} WHERE ""Status"" = {1}",
                (int)PendingExportStatus.Pending, (int)PendingExportStatus.Executing);
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var recoveredCount = await repository.Sync.RecoverStrandedExecutingPendingExportsAsync();

        Assert.That(recoveredCount, Is.EqualTo(0));
    }
}
