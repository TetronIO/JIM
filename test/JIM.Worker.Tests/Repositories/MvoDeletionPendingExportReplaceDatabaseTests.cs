// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that the set-based MVO deletion flush (issue #993) can replace an
/// existing non-Delete Pending Export and then delete the MVO on the same tracking DbContext.
/// </summary>
/// <remarks>
/// The worker's long-lived context tracks Pending Exports loaded by reconciliation and other
/// tracked queries earlier in the same run.
/// <c>DeletePendingExportsByConnectedSystemObjectIdsAsync</c> removes
/// their rows via raw SQL, so it must also detach the tracked instances:
/// <c>PendingExport.SourceMetaverseObject</c> is configured <c>OnDelete(SetNull)</c>, and when the
/// MVO is deleted EF Core's cascade fix-up otherwise issues an UPDATE that targets the
/// already-deleted row, matches zero rows, and throws <c>DbUpdateConcurrencyException</c>
/// (Scenario4-DeletionRules Test 3 failure, 2026-07-13). The in-memory provider has no relational
/// row-count checks, so only a real database run can catch this. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoDeletionPendingExportReplaceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL MVO deletion Pending Export replace tests.");

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
    /// Seeds one user-shaped MVO/CSO pair with an unexported Create Pending Export attached to the
    /// CSO and sourced from the MVO, mirroring a provisioned system that has not yet run an export
    /// when the MVO's deletion is triggered (the Scenario4-DeletionRules Test 3 shape).
    /// </summary>
    private async Task<(Guid MvoId, Guid CsoId)> SeedJoinedUserWithCreatePendingExportAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(mailAttr);

        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var mvo = new MetaverseObject { Type = mvType };
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = mailAttr.Id
        };
        seed.Add(mvo);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = cso.Id,
            SourceMetaverseObjectId = mvo.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = mailAttr.Id,
            StringValue = "test.user@example.com",
            ChangeType = PendingExportAttributeChangeType.Update
        });
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        return (mvo.Id, cso.Id);
    }

    /// <summary>
    /// The deletion flush's collision policy replaces a non-Delete Pending Export via raw SQL and
    /// then deletes the MVO on the same tracking context. The raw delete must detach the tracked
    /// Pending Export, or EF's SetNull cascade fix-up on SourceMetaverseObjectId targets the
    /// deleted row and the MVO delete throws DbUpdateConcurrencyException.
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectsAsync_ReplacedPendingExportStillTracked_DeletesWithoutConcurrencyConflictAsync()
    {
        var (mvoId, csoId) = await SeedJoinedUserWithCreatePendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Page processing tracks the MVO ahead of queueing its deletion.
        var mvo = await ctx.MetaverseObjects.SingleAsync(m => m.Id == mvoId);

        // Reconciliation (or any earlier tracked query in the run) leaves the existing Create
        // Pending Export tracked when the deletion flush's collision policy replaces it via raw
        // SQL, so the raw delete must detach the tracked instance.
        var trackedPe = await ctx.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.ConnectedSystemObjectId == csoId);
        Assert.That(trackedPe.SourceMetaverseObjectId, Is.EqualTo(mvoId),
            "The seeded Pending Export must be sourced from the MVO under deletion.");

        await repository.Sync.DeletePendingExportsByConnectedSystemObjectIdsAsync([csoId]);

        await repository.Sync.DeleteMetaverseObjectsAsync([mvo]);

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjects.AnyAsync(m => m.Id == mvoId), Is.False,
            "The MVO must be deleted.");
        Assert.That(await verify.PendingExports.AnyAsync(), Is.False,
            "The replaced Pending Export must be deleted.");
    }

    /// <summary>
    /// Same shape, but with a tracked attribute value change left modified and unsaved (as
    /// reconciliation earlier in the run can do). The raw delete must detach the tracked child
    /// rows too, or the next SaveChangesAsync issues an UPDATE against the deleted child row.
    /// </summary>
    [Test]
    public async Task DeleteMetaverseObjectsAsync_ReplacedPendingExportChildModified_DeletesWithoutConcurrencyConflictAsync()
    {
        var (mvoId, csoId) = await SeedJoinedUserWithCreatePendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var mvo = await ctx.MetaverseObjects.SingleAsync(m => m.Id == mvoId);

        // Reconciliation earlier in the run can leave a tracked attribute value change modified
        // but not yet saved when the deletion flush replaces the Pending Export.
        var trackedPe = await ctx.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.ConnectedSystemObjectId == csoId);
        trackedPe.AttributeValueChanges.Single().ExportAttemptCount = 1;

        await repository.Sync.DeletePendingExportsByConnectedSystemObjectIdsAsync([csoId]);

        await repository.Sync.DeleteMetaverseObjectsAsync([mvo]);

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjects.AnyAsync(m => m.Id == mvoId), Is.False,
            "The MVO must be deleted.");
        Assert.That(await verify.PendingExportAttributeValueChanges.AnyAsync(), Is.False,
            "The replaced Pending Export's attribute value changes must be deleted.");
    }
}
