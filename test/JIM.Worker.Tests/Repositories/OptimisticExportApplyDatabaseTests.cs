// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <c>SyncRepository.ApplyExportedAttributeValuesAsync</c> (issue
/// #1079: optimistic export apply). The in-memory unit suite (<c>OptimisticExportApplyCalculatorTests</c>,
/// <c>ExportExecutionTests</c>) proves the calculation and wiring; only a real database run can prove
/// the raw-SQL delete is safe against a context holding tracked instances of the affected rows, and
/// that the persisted round-trip is byte-for-byte what the calculator expects on a subsequent pass.
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. Do NOT run
/// this fixture outside the sanctioned scratch-database workflow: <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class OptimisticExportApplyDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL optimistic export apply tests.");

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
    /// Seeds a Connected System with one object type, one Text attribute, and one Normal-status
    /// CSO carrying the given initial attribute values.
    /// </summary>
    private async Task<(ConnectedSystemObjectTypeAttribute Attribute, Guid CsoId)> SeedCsoAsync(
        params string[] initialValues)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.Add(mailAttr);
        seed.AddRange(connectorDefinition, system, csType, mailAttr);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = mailAttr.Id,
            LastUpdated = null
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();

        seed.ConnectedSystemObjectAttributeValues.AddRange(initialValues.Select(stringValue => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = mailAttr.Id,
            StringValue = stringValue
        }));
        await seed.SaveChangesAsync();

        return (mailAttr, cso.Id);
    }

    /// <summary>
    /// (1) Apply inserts and deletes real rows, and leaves the parent CSO's LastUpdated and Status
    /// untouched (D2: re-arms the Full Synchronisation unchanged-object watermark).
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_InsertsAndDeletesRealRows_LeavesLastUpdatedAndStatusUntouchedAsync()
    {
        var (attr, csoId) = await SeedCsoAsync("old@example.com");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var existing = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
            .SingleAsync(av => av.ConnectedSystemObject.Id == csoId);
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);

        var newValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = attr.Id,
            StringValue = "new@example.com"
        };

        await repository.Sync.ApplyExportedAttributeValuesAsync([newValue], [existing.Id]);

        await using var verify = NewContext();
        var storedValues = await verify.ConnectedSystemObjectAttributeValues
            .Where(av => av.ConnectedSystemObject.Id == csoId)
            .ToListAsync();
        Assert.That(storedValues.Select(v => v.StringValue), Is.EquivalentTo(new[] { "new@example.com" }),
            "the old row must be deleted and the new row inserted");

        var storedCso = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(storedCso.LastUpdated, Is.Null, "optimistic apply must never stamp LastUpdated (D2)");
        Assert.That(storedCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "optimistic apply must never touch parent CSO fields (D2)");
    }

    /// <summary>
    /// (2) The raw-SQL delete is safe while a context holds tracked instances of the affected rows
    /// (the detach guard proof): a tracked attribute value that optimistic apply just deleted must
    /// not resurface as a DbUpdateConcurrencyException when the same context later cascade-deletes
    /// its parent CSO.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_DeletePathWhileContextHoldsTrackedInstance_DeletesWithoutConcurrencyConflictAsync()
    {
        var (_, csoId) = await SeedCsoAsync("doomed@example.com");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Track the attribute value AND its parent CSO on this context before the raw delete runs,
        // mirroring how the worker's long-lived context can hold instances loaded earlier in a run.
        var trackedCso = await ctx.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
            .SingleAsync(c => c.Id == csoId);
        var trackedValue = trackedCso.AttributeValues.Single();

        await repository.Sync.ApplyExportedAttributeValuesAsync([], [trackedValue.Id]);

        Assert.That(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>().Any(e => e.Entity.Id == trackedValue.Id), Is.False,
            "the raw delete must detach the tracked instance");

        // Deleting the parent CSO on this same context cascades to every STILL-tracked child; if the
        // attribute value above were left tracked (Unchanged), EF's cascade would issue a DELETE
        // against the already-gone row, affect 0 rows, and throw DbUpdateConcurrencyException.
        ctx.ConnectedSystemObjects.Remove(trackedCso);
        Assert.DoesNotThrowAsync(async () => await ctx.SaveChangesAsync());

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystemObjects.AnyAsync(c => c.Id == csoId), Is.False);
    }

    /// <summary>
    /// (3) Idempotency at the SQL layer: re-running the calculator against a Connected System
    /// Object freshly reloaded from the database after a prior apply finds the exported value
    /// already there and produces an empty delta, so a repeated ApplyExportedAttributeValuesAsync
    /// call for the same Pending Export is never issued with non-empty content.
    /// </summary>
    [Test]
    public async Task ApplyExportedAttributeValuesAsync_ReappliedAfterFreshReload_CalculatorFindsNothingLeftToDoAsync()
    {
        var (attr, csoId) = await SeedCsoAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
        var addedValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            AttributeId = attr.Id,
            StringValue = "idempotent@example.com"
        };

        await repository.Sync.ApplyExportedAttributeValuesAsync([addedValue], []);

        // Reload fresh from the database, as the next batch/run would, and re-run the calculator
        // against the SAME Pending Export attribute change.
        await using var verify = NewContext();
        var reloadedCso = await verify.ConnectedSystemObjects
            .Include(c => c.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .SingleAsync(c => c.Id == csoId);

        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attr.Id,
            Attribute = attr,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = "idempotent@example.com"
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = reloadedCso,
            ConnectedSystemObjectId = reloadedCso.Id,
            AttributeValueChanges = [change]
        };

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pendingExport]);

        Assert.That(delta.Additions, Is.Empty, "the persisted round-trip must satisfy the calculator's existence check");
        Assert.That(delta.RemovalValueIds, Is.Empty);
    }

    /// <summary>
    /// Seeds a Connected System with one USER object type carrying a single-valued Reference
    /// attribute, plus a "referenced" CSO (the resolution target) and a "source" CSO (the one the
    /// Pending Export is against).
    /// </summary>
    private async Task<(ConnectedSystemObjectTypeAttribute ReferenceAttribute, ConnectedSystemObject ReferencedCso, ConnectedSystemObject SourceCso)> SeedReferenceScenarioAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Target System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var managerAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(managerAttr);
        seed.AddRange(connectorDefinition, system, csType, managerAttr);
        await seed.SaveChangesAsync();

        var referencedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        var sourceCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal
        };
        seed.AddRange(referencedCso, sourceCso);
        await seed.SaveChangesAsync();

        return (managerAttr, referencedCso, sourceCso);
    }

    /// <summary>
    /// SPEC-1079B RED test 1 (persistence round-trip): <c>ResolvedReferenceCsoId</c> must survive a
    /// real database round-trip through <c>UpdatePendingExportsAsync</c> (the path
    /// <c>ExportExecutionServer.ProcessDeferredExportsAsync</c> uses to persist a just-resolved
    /// reference before the deferred batch executes, and the path <c>ProcessBatchSuccessAsync</c>
    /// uses after every export attempt). Before the property is mapped this fails: EF's
    /// <c>[NotMapped]</c> attribute means the column is never written, so a fresh reload sees null.
    /// </summary>
    [Test]
    public async Task UpdatePendingExportsAsync_PersistsResolvedReferenceCsoId_RoundTripsOnFreshReloadAsync()
    {
        var (managerAttr, referencedCso, sourceCso) = await SeedReferenceScenarioAsync();

        await using var seed = NewContext();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceCso.ConnectedSystemId,
            ConnectedSystemObjectId = sourceCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            MaxRetries = 3,
            CreatedAt = DateTime.UtcNow
        };
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = managerAttr.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            UnresolvedReferenceValue = Guid.NewGuid().ToString(),
            Status = PendingExportAttributeChangeStatus.Pending
        };
        pendingExport.AttributeValueChanges.Add(change);
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        // Act: simulate reference resolution (TryResolveReferencesFromLookup) and persist exactly
        // as ExportExecutionServer does - StringValue set, UnresolvedReferenceValue cleared,
        // ResolvedReferenceCsoId stamped - via UpdatePendingExportsAsync.
        change.StringValue = "CN=Manager,DC=test";
        change.UnresolvedReferenceValue = null;
        change.ResolvedReferenceCsoId = referencedCso.Id;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.UpdatePendingExportsAsync([pendingExport]);

        await using var verify = NewContext();
        var reloadedChange = await verify.PendingExportAttributeValueChanges.AsNoTracking()
            .SingleAsync(c => c.Id == change.Id);

        Assert.That(reloadedChange.ResolvedReferenceCsoId, Is.EqualTo(referencedCso.Id),
            "ResolvedReferenceCsoId must survive a real database round-trip through UpdatePendingExportsAsync");
        Assert.That(reloadedChange.StringValue, Is.EqualTo("CN=Manager,DC=test"));
    }

    /// <summary>
    /// SPEC-1079B RED test 2 (insert path): <c>ResolvedReferenceCsoId</c> must also survive the
    /// initial multi-row INSERT (<c>CreatePendingExportsAsync</c> -&gt;
    /// <c>BulkInsertPendingExportAttributeValueChangesRawAsync</c>), covering a change created with
    /// the id already set (for example a change built fresh from an already-resolved value).
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_PersistsResolvedReferenceCsoId_RoundTripsOnFreshReloadAsync()
    {
        var (managerAttr, referencedCso, sourceCso) = await SeedReferenceScenarioAsync();

        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = managerAttr.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "CN=Manager,DC=test",
            ResolvedReferenceCsoId = referencedCso.Id,
            Status = PendingExportAttributeChangeStatus.Pending
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceCso.ConnectedSystemId,
            ConnectedSystemObjectId = sourceCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            MaxRetries = 3,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = [change]
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.CreatePendingExportsAsync([pendingExport]);

        await using var verify = NewContext();
        var reloadedChange = await verify.PendingExportAttributeValueChanges.AsNoTracking()
            .SingleAsync(c => c.Id == change.Id);

        Assert.That(reloadedChange.ResolvedReferenceCsoId, Is.EqualTo(referencedCso.Id),
            "ResolvedReferenceCsoId must survive the initial insert round-trip through CreatePendingExportsAsync");
    }
}
