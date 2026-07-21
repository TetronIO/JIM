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
/// Real-PostgreSQL verification of the chunked whole-system Pending Export preload
/// (<c>GetPendingExportsLightweightByConnectedSystemIdAsync</c>).
/// </summary>
/// <remarks>
/// The Scale500k25kGroups confirming import (2026-07-19) failed when the previous implementation
/// loaded 525K Pending Exports joined to 9.8M attribute value changes in a single EF statement,
/// exceeding the server-side statement_timeout (5 min). The rewrite loads keyset-paginated chunks
/// with detached (AsNoTracking) entities and stitches Attribute navigations from a shared lookup.
/// These tests lock in the chunked implementation's semantics: completeness across chunk
/// boundaries, value change and Attribute stitching, Connected System and null-CSO scoping, and
/// duplicate self-healing across chunks. The timeout itself is only reproducible at multi-million
/// row volume, so the at-scale proof lives in the integration suite; opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportPreloadDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export preload tests.");

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

    private sealed record PreloadSeed(
        int TargetSystemId,
        Dictionary<Guid, Guid> PendingExportIdsByCsoId,
        Guid CsoWithoutValueChangesId,
        int MailAttributeId,
        int DisplayNameAttributeId);

    /// <summary>
    /// Seeds a target Connected System with <paramref name="csoCount"/> CSOs, each carrying one
    /// Pending Export with value changes on two attributes (except the last CSO, whose Pending
    /// Export has no value changes at all). Also seeds rows the preload must exclude: a Pending
    /// Export with no Connected System Object, and one belonging to another Connected System.
    /// </summary>
    private async Task<PreloadSeed> SeedPendingExportsAsync(int csoCount)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var targetSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var otherSystem = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };

        var targetType = new ConnectedSystemObjectType { Name = "jimPerson", ConnectedSystem = targetSystem, Selected = true };
        var mailAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = targetType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = targetType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        targetType.Attributes.Add(mailAttr);
        targetType.Attributes.Add(displayNameAttr);

        var otherType = new ConnectedSystemObjectType { Name = "jimPerson", ConnectedSystem = otherSystem, Selected = true };
        var otherAttr = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = otherType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        otherType.Attributes.Add(otherAttr);

        seed.AddRange(connectorDefinition, targetSystem, otherSystem, targetType, otherType);
        await seed.SaveChangesAsync();

        var pendingExportIdsByCsoId = new Dictionary<Guid, Guid>();
        var csoWithoutValueChangesId = Guid.Empty;

        for (var i = 0; i < csoCount; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Type = targetType,
                ConnectedSystem = targetSystem,
                Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = mailAttr.Id
            };
            seed.Add(cso);

            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                ConnectedSystemObject = cso,
                ChangeType = PendingExportChangeType.Create,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            var isLast = i == csoCount - 1;
            if (!isLast)
            {
                pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = mailAttr.Id,
                    StringValue = $"user{i}@example.com",
                    ChangeType = PendingExportAttributeChangeType.Update
                });
                pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = displayNameAttr.Id,
                    StringValue = $"User {i}",
                    ChangeType = PendingExportAttributeChangeType.Update
                });
            }
            else
            {
                csoWithoutValueChangesId = cso.Id;
            }

            seed.Add(pendingExport);
            pendingExportIdsByCsoId[cso.Id] = pendingExport.Id;
        }

        // A Pending Export with no CSO: whole-system reconciliation keys by CSO, so it must be excluded.
        seed.Add(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });

        // A Pending Export on another Connected System: must be excluded.
        var otherCso = new ConnectedSystemObject
        {
            Type = otherType,
            ConnectedSystem = otherSystem,
            Status = ConnectedSystemObjectStatus.Normal,
            ExternalIdAttributeId = otherAttr.Id
        };
        seed.Add(otherCso);
        seed.Add(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = otherSystem.Id,
            ConnectedSystemObject = otherCso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });

        await seed.SaveChangesAsync();

        return new PreloadSeed(targetSystem.Id, pendingExportIdsByCsoId, csoWithoutValueChangesId,
            mailAttr.Id, displayNameAttr.Id);
    }

    /// <summary>
    /// A chunk size smaller than the backlog forces multiple keyset pages; every Pending Export
    /// must still load exactly once with its value changes and stitched Attribute navigations,
    /// and out-of-scope rows (other system, no CSO) must be excluded.
    /// </summary>
    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemIdAsync_ChunkSizeSmallerThanBacklog_LoadsAllAcrossChunksAsync()
    {
        var preloadSeed = await SeedPendingExportsAsync(csoCount: 12);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Sync.GetPendingExportsLightweightByConnectedSystemIdAsync(
            preloadSeed.TargetSystemId, chunkSize: 5);

        Assert.That(result, Has.Count.EqualTo(12), "Every target-system Pending Export with a CSO must be loaded.");

        foreach (var (csoId, pendingExportId) in preloadSeed.PendingExportIdsByCsoId)
        {
            var found = result.TryGetValue(csoId, out var pe);
            Assert.That(found, $"CSO {csoId} must be present in the dictionary.");
            Assert.That(pe, Is.Not.Null);
            var export = pe!;
            Assert.That(export.Id, Is.EqualTo(pendingExportId), "The dictionary must map each CSO to its own Pending Export.");

            if (csoId == preloadSeed.CsoWithoutValueChangesId)
            {
                Assert.That(export.AttributeValueChanges, Is.Empty,
                    "A Pending Export without value changes must load with an empty collection.");
            }
            else
            {
                Assert.That(export.AttributeValueChanges, Has.Count.EqualTo(2),
                    "Each Pending Export must carry its two value changes.");
                foreach (var valueChange in export.AttributeValueChanges)
                {
                    Assert.That(valueChange.Attribute, Is.Not.Null,
                        "Attribute navigations must be stitched onto every value change.");
                    var expectedName = valueChange.AttributeId == preloadSeed.MailAttributeId ? "mail" : "displayName";
                    Assert.That(valueChange.Attribute!.Name, Is.EqualTo(expectedName),
                        "Each value change must be stitched to the correct Attribute.");
                }
            }
        }
    }

    /// <summary>
    /// Duplicate Pending Exports for the same CSO must self-heal (keep newest, delete stale) even
    /// when the duplicates land in different keyset chunks; chunk size 1 guarantees separation.
    /// </summary>
    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemIdAsync_DuplicatesAcrossChunks_SelfHealsKeepingNewestAsync()
    {
        var preloadSeed = await SeedPendingExportsAsync(csoCount: 2);

        // Add a second, newer Pending Export for the first CSO. The schema now prevents this with a
        // unique index; drop it to simulate legacy data from before the index existed, which is the
        // state the self-heal exists to repair (same approach as PendingExportSelfHealDatabaseTests).
        var duplicatedCsoId = preloadSeed.PendingExportIdsByCsoId.Keys.First();
        var staleId = preloadSeed.PendingExportIdsByCsoId[duplicatedCsoId];
        var newerId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            await seed.Database.ExecuteSqlRawAsync(@"DROP INDEX IF EXISTS ""IX_PendingExports_ConnectedSystemObjectId_Unique""");
            seed.Add(new PendingExport
            {
                Id = newerId,
                ConnectedSystemId = preloadSeed.TargetSystemId,
                ConnectedSystemObjectId = duplicatedCsoId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                CreatedAt = DateTime.UtcNow.AddMinutes(5)
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Sync.GetPendingExportsLightweightByConnectedSystemIdAsync(
            preloadSeed.TargetSystemId, chunkSize: 1);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[duplicatedCsoId].Id, Is.EqualTo(newerId), "The newest duplicate must be kept.");

        await using var verify = NewContext();
        Assert.That(await verify.PendingExports.AnyAsync(pe => pe.Id == staleId), Is.False,
            "The stale duplicate must be deleted from the database by the self-heal.");
    }

    /// <summary>
    /// The default chunk size path (no explicit chunk size) must behave identically for a backlog
    /// that fits in a single chunk.
    /// </summary>
    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemIdAsync_DefaultChunkSize_LoadsAllAsync()
    {
        var preloadSeed = await SeedPendingExportsAsync(csoCount: 4);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Sync.GetPendingExportsLightweightByConnectedSystemIdAsync(preloadSeed.TargetSystemId);

        Assert.That(result, Has.Count.EqualTo(4));
        Assert.That(result.Values.Sum(pe => pe.AttributeValueChanges.Count), Is.EqualTo(6),
            "Three of the four Pending Exports carry two value changes each.");
    }
}
