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
/// Real-PostgreSQL verification of the post-write tracker detach in the batch Pending Export
/// update and delete paths (#1004). The legacy implementation called <c>DbContext.Entry()</c> once
/// per attribute value change; every call ran EF change detection over the whole tracker, which is
/// quadratic in change count (a 100,000-member group's export batch spent over ten minutes in it)
/// and, on a tracker holding undetected duplicate-key graphs (routine mid-sync), threw
/// "another instance with the same key value is already being tracked". The detach must instead
/// enumerate the tracker once with change detection suspended, per the house pattern.
/// Opt-in via the JIM_TEST_RESET_* environment variables; ignored when JIM_TEST_RESET_DB is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportBatchDetachDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export batch detach tests.");

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
    /// Seeds a CSO with a Pending Export carrying one attribute value change, mirroring an export
    /// batch member awaiting post-export bookkeeping.
    /// </summary>
    private async Task<(Guid CsoId, Guid PendingExportId)> SeedCsoWithPendingExportAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "GROUP", ConnectedSystem = system, Selected = true };
        var memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(memberAttr);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = memberAttr.Id,
            StringValue = "cn=someone,ou=users,dc=example,dc=local",
            ChangeType = PendingExportAttributeChangeType.Remove
        });
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        return (cso.Id, pendingExport.Id);
    }

    /// <summary>
    /// Loads the Pending Export tracked with its children and attribute metadata, then poisons the
    /// tracker with an undetected duplicate-key graph: a tracked entity's collection navigation
    /// gains an untracked child whose graph carries a duplicate instance (same key, different
    /// object) of an already-tracked attribute. Cross-page reference resolution routinely builds
    /// such graphs mid-sync, and no DetectChanges has run since.
    /// </summary>
    private static async Task<PendingExport> LoadPendingExportAndPoisonTrackerAsync(JimDbContext ctx, Guid csoId)
    {
        var trackedPe = await ctx.PendingExports
            .Include(pe => pe.AttributeValueChanges)
                .ThenInclude(avc => avc.Attribute)
            .SingleAsync(pe => pe.ConnectedSystemObjectId == csoId);
        var trackedAttr = trackedPe.AttributeValueChanges.Single().Attribute;

        var duplicateAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = trackedAttr.Id, Name = trackedAttr.Name, Type = trackedAttr.Type,
            AttributePlurality = trackedAttr.AttributePlurality
        };
        trackedPe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = duplicateAttr,
            AttributeId = duplicateAttr.Id,
            StringValue = "cn=untracked.duplicate,ou=users,dc=example,dc=local",
            ChangeType = PendingExportAttributeChangeType.Remove
        });
        return trackedPe;
    }

    /// <summary>
    /// The legacy per-entity Entry() detach triggered DetectChanges, which attached the undetected
    /// duplicate-key graph and threw an identity conflict; the export batch then failed despite the
    /// target system write having succeeded. The detach must not trigger change detection.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_TrackerHoldsUndetectedDuplicateKeyGraph_UpdatesWithoutIdentityConflictAsync()
    {
        var (csoId, pendingExportId) = await SeedCsoWithPendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var trackedPe = await LoadPendingExportAndPoisonTrackerAsync(ctx, csoId);
        trackedPe.Status = PendingExportStatus.Exported;

        Assert.DoesNotThrowAsync(() => repository.Sync.UpdatePendingExportsAsync([trackedPe]),
            "The post-export detach must not trigger change detection over a poisoned tracker");

        await using var verify = NewContext();
        var status = await verify.PendingExports.Where(pe => pe.Id == pendingExportId)
            .Select(pe => pe.Status).SingleAsync();
        Assert.That(status, Is.EqualTo(PendingExportStatus.Exported),
            "The bulk status update must have been persisted");
    }

    /// <summary>
    /// Same poisoned-tracker shape through the delete path. The legacy loop swallowed the identity
    /// conflict and left the deleted entities tracked as stale entries; the detach must succeed so
    /// no later SaveChangesAsync can act on raw-deleted rows.
    /// </summary>
    [Test]
    public async Task DeletePendingExportsAsync_TrackerHoldsUndetectedDuplicateKeyGraph_DetachesDeletedEntitiesAsync()
    {
        var (csoId, _) = await SeedCsoWithPendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var trackedPe = await LoadPendingExportAndPoisonTrackerAsync(ctx, csoId);

        Assert.DoesNotThrowAsync(() => repository.Sync.DeletePendingExportsAsync([trackedPe]));

        Assert.That(ctx.Entry(trackedPe).State, Is.EqualTo(EntityState.Detached),
            "The deleted Pending Export must be detached, not left tracked as a stale entry");

        await using var verify = NewContext();
        Assert.That(await verify.PendingExports.AnyAsync(), Is.False, "The Pending Export row must be deleted");
        Assert.That(await verify.PendingExportAttributeValueChanges.AnyAsync(), Is.False,
            "The attribute value change rows must be deleted");
    }

    /// <summary>
    /// Pin: with a healthy tracker, the batch update still detaches the Pending Export and its
    /// children so a later SaveChangesAsync on the same long-lived context cannot re-persist what
    /// the raw SQL already wrote.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_TrackedExportAndChildren_DetachedAndLaterSaveCleanAsync()
    {
        var (csoId, pendingExportId) = await SeedCsoWithPendingExportAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var trackedPe = await ctx.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .SingleAsync(pe => pe.ConnectedSystemObjectId == csoId);
        trackedPe.Status = PendingExportStatus.Exported;
        trackedPe.AttributeValueChanges.Single().ExportAttemptCount = 1;

        await repository.Sync.UpdatePendingExportsAsync([trackedPe]);

        Assert.That(ctx.Entry(trackedPe).State, Is.EqualTo(EntityState.Detached),
            "The updated Pending Export must be detached after the raw SQL write");
        Assert.That(ctx.Entry(trackedPe.AttributeValueChanges.Single()).State, Is.EqualTo(EntityState.Detached),
            "The updated attribute value changes must be detached after the raw SQL write");

        Assert.DoesNotThrowAsync(() => ctx.SaveChangesAsync());

        await using var verify = NewContext();
        var persisted = await verify.PendingExports.Where(pe => pe.Id == pendingExportId)
            .Select(pe => pe.Status).SingleAsync();
        Assert.That(persisted, Is.EqualTo(PendingExportStatus.Exported));
    }
}
