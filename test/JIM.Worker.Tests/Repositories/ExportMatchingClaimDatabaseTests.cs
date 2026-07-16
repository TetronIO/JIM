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
/// Real-PostgreSQL verification of the atomic join claim added for #1051: export matching's
/// join-before-provision write must guard against two Metaverse Objects racing to join the same
/// unclaimed Connected System Object. The in-memory provider performs no affected-row-count checks
/// (per the raw-SQL-write house rule in <c>src/CLAUDE.md</c>), so only a real database run proves the
/// conditional <c>UPDATE ... WHERE "MetaverseObjectId" IS NULL</c> actually loses the race correctly,
/// and that the worker's tracked-instance fix-up does not clobber the claimed row on the next
/// <c>SaveChangesAsync</c>. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the
/// other <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExportMatchingClaimDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL export matching claim tests.");

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
    /// Seeds a minimal unclaimed CSO in a target system, plus two candidate MVOs, mirroring the
    /// join-before-provision shape (export matching found the CSO but has not yet claimed it).
    /// </summary>
    private async Task<(Guid CsoId, Guid FirstMvoId, Guid SecondMvoId)> SeedUnclaimedCsoAndTwoMvosAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone Target", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        var employeeIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(employeeIdAttr);

        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        var firstMvo = new MetaverseObject { Type = mvType };
        var secondMvo = new MetaverseObject { Type = mvType };
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            ExternalIdAttributeId = employeeIdAttr.Id
        };
        seed.Add(firstMvo);
        seed.Add(secondMvo);
        seed.Add(cso);
        await seed.SaveChangesAsync();

        return (cso.Id, firstMvo.Id, secondMvo.Id);
    }

    /// <summary>
    /// The first claim on an unclaimed CSO must succeed and persist all four join fields, verified
    /// by re-reading the row on a fresh context.
    /// </summary>
    [Test]
    public async Task TryClaimConnectedSystemObjectForJoinAsync_UnclaimedCso_ClaimSucceedsAndPersistsAsync()
    {
        var (csoId, firstMvoId, _) = await SeedUnclaimedCsoAndTwoMvosAsync();
        var dateJoined = DateTime.UtcNow;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var claimed = await repository.Sync.TryClaimConnectedSystemObjectForJoinAsync(csoId, firstMvoId, dateJoined);

        Assert.That(claimed, Is.True, "The first claim on an unclaimed CSO must succeed");

        await using var verify = NewContext();
        var row = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(row.MetaverseObjectId, Is.EqualTo(firstMvoId));
        Assert.That(row.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
        Assert.That(row.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        Assert.That(row.DateJoined, Is.Not.Null);
        Assert.That(row.DateJoined!.Value, Is.EqualTo(dateJoined).Within(TimeSpan.FromSeconds(1)),
            "Npgsql round-trips DateTime as UTC; allow a small tolerance for timestamp precision");
    }

    /// <summary>
    /// A second claim for a different Metaverse Object after the first has already claimed the row
    /// must fail and must not disturb the first claim's persisted values (the concurrency guard
    /// this issue exists to add).
    /// </summary>
    [Test]
    public async Task TryClaimConnectedSystemObjectForJoinAsync_AlreadyClaimed_SecondClaimFailsAndFirstClaimUnchangedAsync()
    {
        var (csoId, firstMvoId, secondMvoId) = await SeedUnclaimedCsoAndTwoMvosAsync();
        var firstDateJoined = DateTime.UtcNow;

        await using var firstCtx = NewContext();
        var firstRepository = new PostgresDataRepository(firstCtx);
        var firstClaimed = await firstRepository.Sync.TryClaimConnectedSystemObjectForJoinAsync(csoId, firstMvoId, firstDateJoined);
        Assert.That(firstClaimed, Is.True, "Precondition: the first claim must succeed");

        await using var secondCtx = NewContext();
        var secondRepository = new PostgresDataRepository(secondCtx);
        var secondClaimed = await secondRepository.Sync.TryClaimConnectedSystemObjectForJoinAsync(
            csoId, secondMvoId, DateTime.UtcNow.AddSeconds(1));

        Assert.That(secondClaimed, Is.False, "A second claim on an already-claimed CSO must fail");

        await using var verify = NewContext();
        var row = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(row.MetaverseObjectId, Is.EqualTo(firstMvoId),
            "The CSO must remain claimed by the first Metaverse Object");
        Assert.That(row.DateJoined!.Value, Is.EqualTo(firstDateJoined).Within(TimeSpan.FromSeconds(1)),
            "The failed second claim must not overwrite the first claim's DateJoined");
    }

    /// <summary>
    /// Tracked-instance discipline (the raw-SQL-write house rule in <c>src/CLAUDE.md</c>): the same
    /// pattern the export evaluation server follows. Load the CSO tracked on a context, claim it via
    /// the repository (raw SQL, bypasses the change tracker), fix up the tracked instance the way the
    /// server does, then SaveChangesAsync on that context must not throw and must not overwrite the
    /// row with stale values.
    /// </summary>
    [Test]
    public async Task TryClaimConnectedSystemObjectForJoinAsync_TrackedInstanceFixedUpThenSaved_DoesNotOverwriteRowAsync()
    {
        var (csoId, firstMvoId, _) = await SeedUnclaimedCsoAndTwoMvosAsync();
        var dateJoined = DateTime.UtcNow;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Load the CSO tracked, as the worker's long-lived context would have from an earlier query
        // in the same run (e.g. AttemptExportMatchingAsync's candidate lookup).
        var trackedCso = await ctx.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);

        var claimed = await repository.Sync.TryClaimConnectedSystemObjectForJoinAsync(csoId, firstMvoId, dateJoined);
        Assert.That(claimed, Is.True);

        // Fix up the tracked instance exactly as CreateOrUpdatePendingExportWithNoNetChangeAsync does,
        // because the raw UPDATE bypassed the change tracker.
        trackedCso.MetaverseObjectId = firstMvoId;
        trackedCso.Status = ConnectedSystemObjectStatus.Normal;
        trackedCso.JoinType = ConnectedSystemObjectJoinType.Joined;
        trackedCso.DateJoined = dateJoined;

        // SaveChangesAsync must not throw (no DbUpdateConcurrencyException) and must not write stale
        // pre-claim values back over the row.
        Assert.DoesNotThrowAsync(async () => await ctx.SaveChangesAsync());

        await using var verify = NewContext();
        var row = await verify.ConnectedSystemObjects.SingleAsync(c => c.Id == csoId);
        Assert.That(row.MetaverseObjectId, Is.EqualTo(firstMvoId));
        Assert.That(row.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
        Assert.That(row.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
    }
}
