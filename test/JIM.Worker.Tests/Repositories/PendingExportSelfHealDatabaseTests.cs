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
/// Real-PostgreSQL verification of the duplicate self-heal in
/// <c>GetPendingExportsLightweightByConnectedSystemObjectIdsAsync</c>: when more than one Pending
/// Export exists for the same CSO (a prior data integrity violation), the fetch keeps the newest
/// by CreatedAt, deletes the older row(s) and their attribute value changes via raw SQL, and
/// returns only the keeper.
/// </summary>
/// <remarks>
/// The self-heal deletes via <c>ExecuteSqlRawAsync</c>, which neither a mocked DbContext nor the
/// EF in-memory provider can execute, so only a real database run can verify both the returned
/// dictionary AND that the duplicate rows are actually gone. Coverage migrated from the removed
/// heavy fetch variant's mock-based tests (issue #997), which could only assert the returned
/// dictionary. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportSelfHealDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export self-heal tests.");

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
    /// Restores the unique index the seeding helper drops, so later fixtures sharing this test
    /// database see the real schema. The table holds no duplicates by now (the self-heal under
    /// test removed them), so recreation always succeeds.
    /// </summary>
    [TearDown]
    public async Task TearDown()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PendingExports_ConnectedSystemObjectId_Unique""
              ON ""PendingExports"" (""ConnectedSystemObjectId"") WHERE ""ConnectedSystemObjectId"" IS NOT NULL");
    }

    private sealed record SeededState(Guid Cso1Id, Guid Cso2Id, int SystemId, int MailAttrId);

    /// <summary>
    /// Seeds a Connected System with one schema attribute and two CSOs. Pending Exports are added
    /// per test via <see cref="AddPendingExportAsync"/>. The PendingExports unique index on
    /// ConnectedSystemObjectId only guards new writes; historic duplicates predating it are what
    /// the self-heal exists for, so tests insert duplicates with the index dropped.
    /// </summary>
    private async Task<SeededState> SeedTwoCsosAsync()
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
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();

        var csos = new List<ConnectedSystemObject>();
        for (var i = 0; i < 2; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Type = csType,
                ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = mailAttr.Id
            };
            seed.Add(cso);
            csos.Add(cso);
        }
        await seed.SaveChangesAsync();

        return new SeededState(csos[0].Id, csos[1].Id, system.Id, mailAttr.Id);
    }

    /// <summary>
    /// Inserts a Pending Export (with one attribute value change) directly, bypassing the unique
    /// ConnectedSystemObjectId index the way historic data-integrity incidents did: the index is
    /// dropped for the insert and restored in TearDown once the self-heal has removed duplicates.
    /// </summary>
    private async Task<Guid> AddPendingExportAsync(SeededState state, Guid csoId, DateTime createdAt)
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_PendingExports_ConnectedSystemObjectId_Unique""");

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = state.SystemId,
            ConnectedSystemObjectId = csoId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = createdAt
        };
        pe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = state.MailAttrId,
            StringValue = $"user-{createdAt:HHmmssfff}@example.com",
            ChangeType = PendingExportAttributeChangeType.Update
        });
        ctx.Add(pe);
        await ctx.SaveChangesAsync();
        return pe.Id;
    }

    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_WithDuplicatePendingExportsForSameCso_SelfHealsKeepingNewestAsync()
    {
        var state = await SeedTwoCsosAsync();
        var olderPeId = await AddPendingExportAsync(state, state.Cso1Id, DateTime.UtcNow.AddMinutes(-10));
        var newerPeId = await AddPendingExportAsync(state, state.Cso1Id, DateTime.UtcNow.AddMinutes(-1));

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync([state.Cso1Id]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[state.Cso1Id].Id, Is.EqualTo(newerPeId), "The newest Pending Export must be kept.");

        await using var verify = NewContext();
        Assert.That(await verify.PendingExports.AnyAsync(pe => pe.Id == olderPeId), Is.False,
            "The older duplicate row must be deleted from the database.");
        Assert.That(await verify.PendingExportAttributeValueChanges.CountAsync(), Is.EqualTo(1),
            "The older duplicate's attribute value changes must be deleted with it.");
        Assert.That(await verify.PendingExports.CountAsync(), Is.EqualTo(1),
            "Exactly the keeper row must remain.");
    }

    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_WithDuplicatesAmongMultipleCsos_SelfHealsOnlyDuplicatesAsync()
    {
        var state = await SeedTwoCsosAsync();
        var olderPeId = await AddPendingExportAsync(state, state.Cso1Id, DateTime.UtcNow.AddMinutes(-10));
        var cso2PeId = await AddPendingExportAsync(state, state.Cso2Id, DateTime.UtcNow.AddMinutes(-5));
        var newerPeId = await AddPendingExportAsync(state, state.Cso1Id, DateTime.UtcNow.AddMinutes(-1));

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync([state.Cso1Id, state.Cso2Id]);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[state.Cso1Id].Id, Is.EqualTo(newerPeId), "The newest Pending Export must be kept for the CSO with duplicates.");
        Assert.That(result[state.Cso2Id].Id, Is.EqualTo(cso2PeId), "The CSO without duplicates must be unaffected.");

        await using var verify = NewContext();
        Assert.That(await verify.PendingExports.AnyAsync(pe => pe.Id == olderPeId), Is.False,
            "Only the older duplicate row may be deleted.");
        Assert.That(await verify.PendingExports.CountAsync(), Is.EqualTo(2),
            "The keeper and the unaffected CSO's Pending Export must remain.");
    }
}
