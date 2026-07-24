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
/// SPEC-1082 test plan item 7: real-PostgreSQL verification of the CSO import content hash
/// stamp-ordering invariant (D6). The in-memory unit suite proves wiring; only a real database run
/// can prove the raw-SQL UPDATE column lists, the create path's NULL-writing, and the update path's
/// exclusion actually hold at the SQL layer. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment
/// variables as the other <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is
/// absent. Do NOT run this fixture outside the sanctioned scratch-database workflow: <c>SetUp</c>
/// TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoImportStateStampDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL CSO import state stamp tests.");

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

    private async Task<(ConnectedSystemObjectTypeAttribute Attribute, Guid CsoId)> SeedCsoAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var extIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "id", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        csType.Attributes.Add(extIdAttr);
        seed.AddRange(connectorDefinition, system, csType, extIdAttr);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = extIdAttr.Id,
            LastUpdated = null
        };
        seed.Add(cso);
        await seed.SaveChangesAsync();

        return (extIdAttr, cso.Id);
    }

    /// <summary>
    /// D6: <c>StampImportStateAsync</c> persists both columns and does NOT change LastUpdated.
    /// </summary>
    [Test]
    public async Task StampImportStateAsync_PersistsBothColumns_DoesNotChangeLastUpdatedAsync()
    {
        var (_, csoId) = await SeedCsoAsync();
        var hash = Guid.NewGuid();
        var fingerprint = Guid.NewGuid();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.StampImportStateAsync([(csoId, hash, fingerprint)]);

        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(stored.ImportStateHash, Is.EqualTo(hash));
        Assert.That(stored.ImportStateFingerprint, Is.EqualTo(fingerprint));
        Assert.That(stored.LastUpdated, Is.Null, "StampImportStateAsync must never touch LastUpdated (the #891 watermark)");
    }

    /// <summary>
    /// D6: an empty stamp list is a no-op (does not throw, issues no statement).
    /// </summary>
    [Test]
    public async Task StampImportStateAsync_EmptyList_NoOpAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        Assert.DoesNotThrowAsync(async () => await repository.Sync.StampImportStateAsync([]));
    }

    /// <summary>
    /// D6: the create path (SyncRepository.CreateConnectedSystemObjectsAsync, the two-phase parallel
    /// writer's small-batch/single-connection fallback) always writes NULL for both stamp columns;
    /// a newly created CSO is never born pre-stamped.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_NewCso_WritesNullImportStateAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Create Path System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var extIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "id", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        csType.Attributes.Add(extIdAttr);
        seed.AddRange(connectorDefinition, system, csType, extIdAttr);
        await seed.SaveChangesAsync();

        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = csoId,
            TypeId = csType.Id,
            ConnectedSystemId = system.Id,
            Type = csType,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = extIdAttr.Id
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.CreateConnectedSystemObjectsAsync([cso]);

        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(stored.ImportStateHash, Is.Null, "newly created CSOs must never carry a pre-stamped content hash");
        Assert.That(stored.ImportStateFingerprint, Is.Null, "newly created CSOs must never carry a pre-stamped fingerprint");
    }

    /// <summary>
    /// D6: the parent-row bulk UPDATE (BulkUpdateConnectedSystemObjectsRawAsync, used by
    /// UpdateConnectedSystemObjectsAsync for the join-state/status fields it writes) must leave a
    /// previously stamped hash/fingerprint untouched - the exclusion list is honoured, not just
    /// documented.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_StampedCso_LeavesImportStateUntouchedAsync()
    {
        var (_, csoId) = await SeedCsoAsync();
        var hash = Guid.NewGuid();
        var fingerprint = Guid.NewGuid();

        await using (var stampCtx = NewContext())
        {
            var stampRepository = new PostgresDataRepository(stampCtx);
            await stampRepository.Sync.StampImportStateAsync([(csoId, hash, fingerprint)]);
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == csoId);
        cso.Status = ConnectedSystemObjectStatus.Obsolete;

        await repository.Sync.UpdateConnectedSystemObjectsAsync([cso]);

        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(stored.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "the parent bulk update must still apply the fields it owns");
        Assert.That(stored.ImportStateHash, Is.EqualTo(hash), "the parent bulk update must not overwrite a previously stamped hash (D6 exclusion list)");
        Assert.That(stored.ImportStateFingerprint, Is.EqualTo(fingerprint), "the parent bulk update must not overwrite a previously stamped fingerprint (D6 exclusion list)");
    }

    /// <summary>
    /// D10 regression (found at runtime, 2026-07-24): <c>UpdateConnectedSystemRunProfileAsync</c>
    /// persists via a hand-typed field copy onto a re-fetched tracked entity, so a field missing
    /// from that copy is silently dropped while the API response (mapped from the caller's mutated
    /// instance) still echoes the new value. This round-trip proves
    /// <c>VerifyImportContentHashes</c> survives the repository's update path on a fresh context.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_VerificationModeFlag_RoundTripsAsync()
    {
        int runProfileId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
            seed.ConnectedSystems.Add(system);
            await seed.SaveChangesAsync();

            var runProfile = new ConnectedSystemRunProfile
            {
                Name = "Full Import", ConnectedSystemId = system.Id, RunType = ConnectedSystemRunType.FullImport
            };
            seed.ConnectedSystemRunProfiles.Add(runProfile);
            await seed.SaveChangesAsync();
            runProfileId = runProfile.Id;
        }

        // Mirror the API controller's shape: load without tracking, mutate, persist through the
        // repository method, then verify on a FRESH context.
        await using (var updateCtx = NewContext())
        {
            var repository = new PostgresDataRepository(updateCtx);
            var loaded = await updateCtx.ConnectedSystemRunProfiles.AsNoTracking().SingleAsync(rp => rp.Id == runProfileId);
            loaded.VerifyImportContentHashes = true;
            await repository.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(loaded);
        }

        await using var verify = NewContext();
        var stored = await verify.ConnectedSystemRunProfiles.SingleAsync(rp => rp.Id == runProfileId);
        Assert.That(stored.VerifyImportContentHashes, Is.True,
            "UpdateConnectedSystemRunProfileAsync must persist VerifyImportContentHashes; a hand-typed field copy that omits it silently drops the write");
    }
}
