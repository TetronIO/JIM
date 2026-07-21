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
/// Real-PostgreSQL verification of keyset pagination in <c>GetConnectedSystemObjectsAsync</c>
/// (the synchronisation CSO page loader).
/// </summary>
/// <remarks>
/// Offset pagination re-scans and discards all rows before the offset on every page, so per-page
/// cost grows linearly with page number: at Scale500k25kGroups the confirming Full Synchronisation
/// spent 977s across 1,050 page loads (~200ms early pages degrading to ~1.5s late pages). The
/// keyset cursor (<c>afterId</c>) keeps every page O(pageSize). Real PostgreSQL matters here
/// because PostgreSQL orders uuid columns bytewise while .NET's <c>Guid.CompareTo</c> compares
/// component-wise; the pagination is only correct because the cursor value is taken from the last
/// row as returned by the database and compared by the same engine that ordered it. These tests
/// prove completeness and non-overlap of keyset pages against the engine that matters. Opt-in via
/// the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class KeysetCsoPagingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL keyset CSO paging tests.");

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

    private async Task<(int SystemId, HashSet<Guid> CsoIds)> SeedCsosAsync(int count)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        type.Attributes.Add(mailAttr);
        seed.AddRange(connectorDefinition, system, type);
        await seed.SaveChangesAsync();

        var csoIds = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = mailAttr.Id
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Attribute = mailAttr, AttributeId = mailAttr.Id, StringValue = $"user{i}@example.com"
            });
            seed.Add(cso);
            csoIds.Add(cso.Id);
        }
        await seed.SaveChangesAsync();
        return (system.Id, csoIds);
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_KeysetCursorChaining_EnumeratesAllCsosWithoutOverlapAsync()
    {
        // Arrange
        var (systemId, expectedIds) = await SeedCsosAsync(10);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Act: walk pages of 3 using the keyset cursor, exactly as the full sync loop does
        var seenIds = new List<Guid>();
        Guid? afterId = null;
        for (var page = 1; page <= 4; page++)
        {
            var result = await repository.Sync.GetConnectedSystemObjectsAsync(
                systemId, page, pageSize: 3, knownTotalCount: 10, lastSyncTimestamp: null, afterId: afterId);

            seenIds.AddRange(result.Results.Select(cso => cso.Id));
            if (result.Results.Count > 0)
                afterId = result.Results[^1].Id;
        }

        // Assert: every CSO seen exactly once
        Assert.That(seenIds, Has.Count.EqualTo(10), "Keyset paging must enumerate every CSO exactly once");
        Assert.That(seenIds, Is.Unique, "Keyset pages must not overlap");
        Assert.That(seenIds, Is.EquivalentTo(expectedIds));
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_KeysetWithWatermark_LoadsChangedCsoAttributesAcrossPagesAsync()
    {
        // Arrange: watermark older than every CSO, so all are "changed" and attributes must load
        var (systemId, expectedIds) = await SeedCsosAsync(7);
        var watermark = DateTime.UtcNow.AddDays(-1);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Act
        var seenIds = new List<Guid>();
        Guid? afterId = null;
        for (var page = 1; page <= 3; page++)
        {
            var result = await repository.Sync.GetConnectedSystemObjectsAsync(
                systemId, page, pageSize: 3, knownTotalCount: 7, lastSyncTimestamp: watermark, afterId: afterId);

            foreach (var cso in result.Results)
            {
                Assert.That(cso.IsUnchangedSinceLastSync, Is.False,
                    "All CSOs were created after the watermark, so none may take the unchanged fast path");
                Assert.That(cso.AttributeValues, Is.Not.Empty,
                    $"Changed CSO {cso.Id} must have its attribute values loaded on every keyset page");
            }

            seenIds.AddRange(result.Results.Select(cso => cso.Id));
            if (result.Results.Count > 0)
                afterId = result.Results[^1].Id;
        }

        // Assert
        Assert.That(seenIds, Has.Count.EqualTo(7));
        Assert.That(seenIds, Is.Unique);
        Assert.That(seenIds, Is.EquivalentTo(expectedIds));
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_NoCursor_MatchesKeysetFirstPageAsync()
    {
        // Arrange
        var (systemId, _) = await SeedCsosAsync(5);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Act: offset path (no cursor) vs keyset path from a null cursor
        var offsetFirstPage = await repository.Sync.GetConnectedSystemObjectsAsync(
            systemId, page: 1, pageSize: 3);
        var keysetFirstPage = await repository.Sync.GetConnectedSystemObjectsAsync(
            systemId, page: 1, pageSize: 3, knownTotalCount: null, lastSyncTimestamp: null, afterId: null);

        // Assert: identical first page either way
        Assert.That(
            keysetFirstPage.Results.Select(cso => cso.Id),
            Is.EqualTo(offsetFirstPage.Results.Select(cso => cso.Id)),
            "A null cursor must behave identically to the first offset page");
    }
}
