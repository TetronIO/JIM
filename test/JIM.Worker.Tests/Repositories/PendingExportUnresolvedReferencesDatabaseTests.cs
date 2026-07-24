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
/// Real-PostgreSQL verification of
/// <see cref="JIM.PostgresData.Repositories.SyncRepository.GetPendingExportsWithUnresolvedReferencesAsync"/>,
/// the deferred-reference second-pass lookup that pushes the "Pending status with unresolved
/// references" predicate into SQL (issue #1102).
/// </summary>
/// <remarks>
/// Also proves the accompanying migration (the <c>HasUnresolvedReferences</c> partial index)
/// applies cleanly: <see cref="OneTimeSetUp"/> runs the full migration chain via
/// <c>ctx.Database.Migrate()</c>. The EF Core in-memory provider auto-fixes navigation properties
/// regardless of the query's <c>Include</c> chain, which would mask a missing
/// <c>AttributeValueChanges.Attribute</c> Include; only a real database run can verify it.
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// <c>RequiresPostgres</c> fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportUnresolvedReferencesDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deferred-reference Pending Export tests.");

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
    /// Seeds two Connected Systems, each with a single-valued text attribute, and returns their
    /// ids plus the id of the attribute belonging to the first system for use in Pending Export
    /// Attribute Value Changes. Returns the id only (not the entity): the entity's navigation
    /// properties reach back through its full parent graph (type, system, connector definition),
    /// and attaching that detached graph to a later, separate <see cref="NewContext"/> would mark
    /// the already-persisted parents as new inserts and violate their primary keys.
    /// </summary>
    private async Task<(int SystemAId, int SystemBId, int AttributeAId)> SeedTwoSystemsAsync()
    {
        await using var seed = NewContext();

        var connectorDefinitionA = new ConnectorDefinition { Name = "Test Connector A", BuiltIn = true };
        var systemA = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinitionA };
        var csTypeA = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = systemA, Selected = true };
        var attributeA = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = csTypeA, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csTypeA.Attributes.Add(attributeA);

        var connectorDefinitionB = new ConnectorDefinition { Name = "Test Connector B", BuiltIn = true };
        var systemB = new ConnectedSystem { Name = "Yellowstone APAC", ConnectorDefinition = connectorDefinitionB };
        var csTypeB = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = systemB, Selected = true };
        var attributeB = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager", ConnectedSystemObjectType = csTypeB, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csTypeB.Attributes.Add(attributeB);

        seed.AddRange(connectorDefinitionA, systemA, csTypeA, connectorDefinitionB, systemB, csTypeB);
        await seed.SaveChangesAsync();

        return (systemA.Id, systemB.Id, attributeA.Id);
    }

    private static PendingExport BuildPendingExport(
        int connectedSystemId, PendingExportStatus status, bool hasUnresolvedReferences, int attributeId)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ChangeType = PendingExportChangeType.Update,
            Status = status,
            HasUnresolvedReferences = hasUnresolvedReferences,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    AttributeId = attributeId,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    UnresolvedReferenceValue = Guid.NewGuid().ToString()
                }
            }
        };
    }

    /// <summary>
    /// The targeted lookup must return only Pending Exports that are BOTH Pending status AND have
    /// unresolved references, scoped to the requested Connected System, and must hydrate each
    /// returned export's <c>AttributeValueChanges[].Attribute</c> navigation (#1102). A resolved
    /// Pending row, unresolved rows in other statuses (Exported, Failed), and an unresolved Pending
    /// row belonging to a different Connected System must all be excluded.
    /// </summary>
    [Test]
    public async Task GetPendingExportsWithUnresolvedReferencesAsync_ReturnsOnlyPendingUnresolvedForSystemAndHydratesAttributeAsync()
    {
        var (systemAId, systemBId, attributeAId) = await SeedTwoSystemsAsync();

        var matching = BuildPendingExport(systemAId, PendingExportStatus.Pending, hasUnresolvedReferences: true, attributeAId);
        var resolvedPending = BuildPendingExport(systemAId, PendingExportStatus.Pending, hasUnresolvedReferences: false, attributeAId);
        var exportedUnresolved = BuildPendingExport(systemAId, PendingExportStatus.Exported, hasUnresolvedReferences: true, attributeAId);
        var failedUnresolved = BuildPendingExport(systemAId, PendingExportStatus.Failed, hasUnresolvedReferences: true, attributeAId);
        var otherSystemUnresolved = BuildPendingExport(systemBId, PendingExportStatus.Pending, hasUnresolvedReferences: true, attributeAId);

        await using (var seed = NewContext())
        {
            seed.PendingExports.AddRange(matching, resolvedPending, exportedUnresolved, failedUnresolved, otherSystemUnresolved);
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Sync.GetPendingExportsWithUnresolvedReferencesAsync(systemAId);

        Assert.That(result.Select(pe => pe.Id), Is.EquivalentTo(new[] { matching.Id }),
            "Only the Pending, unresolved-references row for the requested Connected System must be returned.");

        var loaded = result.Single();
        Assert.That(loaded.AttributeValueChanges, Has.Count.EqualTo(1));
        Assert.That(loaded.AttributeValueChanges[0].Attribute, Is.Not.Null,
            "AttributeValueChanges[].Attribute must be hydrated for TryResolveReferencesFromLookup and its logging.");
        Assert.That(loaded.AttributeValueChanges[0].Attribute.Name, Is.EqualTo("manager"));
    }
}
