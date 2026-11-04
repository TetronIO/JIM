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
/// Real-PostgreSQL verification that the raw-SQL CSO bulk insert and bulk update paths persist
/// PartitionId. The import processor assigns the Run Profile's partition to new CSOs at creation
/// and backfills it on matched existing CSOs specifically so partition-scoped deletion detection
/// can scope correctly, but both raw statements omitted the column, silently discarding the value
/// and leaving the objects invisible to their partition's obsoletion sweep forever. The in-memory
/// provider stores the object graph verbatim and cannot catch a missing column in hand-written SQL.
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoPartitionIdPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL CSO PartitionId persistence tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    /// <summary>
    /// Seeds the FK graph a CSO row needs (system, object type, external-id attribute, partition)
    /// and returns the ids required to build CSOs.
    /// </summary>
    private async Task<(int SystemId, int TypeId, int ExternalIdAttributeId, int PartitionId)> SeedConnectedSystemGraphAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = $"Test System {Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var idAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            IsExternalId = true, Selected = true
        };
        csType.Attributes.Add(idAttribute);
        var partition = new ConnectedSystemPartition
        {
            ConnectedSystem = system, Name = "DC=corp,DC=example", ExternalId = $"partition-{Guid.NewGuid():N}", Selected = true
        };
        seed.AddRange(connectorDefinition, system, csType, partition);
        await seed.SaveChangesAsync();
        return (system.Id, csType.Id, idAttribute.Id, partition.Id);
    }

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_CsoWithPartitionId_PersistsTheColumnAsync()
    {
        // Arrange
        var (systemId, typeId, externalIdAttributeId, partitionId) = await SeedConnectedSystemGraphAsync();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = systemId,
            TypeId = typeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow,
            PartitionId = partitionId
        };

        // Act: persist through the raw bulk insert path the import processor uses
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.CreateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso });
        }

        // Assert
        await using var readContext = NewContext();
        var persisted = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == cso.Id);
        Assert.That(persisted.PartitionId, Is.EqualTo(partitionId),
            "A newly imported CSO's PartitionId must be persisted or partition-scoped deletion detection can never see it.");
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_PartitionIdBackfilled_PersistsTheColumnAsync()
    {
        // Arrange: a CSO created before partition tracking (PartitionId null), then backfilled in
        // memory the way SyncImportTaskProcessor does on a matched existing object
        var (systemId, typeId, externalIdAttributeId, partitionId) = await SeedConnectedSystemGraphAsync();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = systemId,
            TypeId = typeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow
        };
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.Add(cso);
            await seed.SaveChangesAsync();
        }

        cso.PartitionId = partitionId;
        cso.LastUpdated = DateTime.UtcNow;

        // Act: persist through the raw bulk update path
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.ConnectedSystems.UpdateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso });
        }

        // Assert
        await using var readContext = NewContext();
        var persisted = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == cso.Id);
        Assert.That(persisted.PartitionId, Is.EqualTo(partitionId),
            "The import processor's PartitionId backfill must be persisted by the bulk update, not silently discarded.");
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_PartitionMovedWithNothingElseChanged_PersistsTheNewPartitionAsync()
    {
        // #2723: an import restamps an object whose placement moved from one Partition to another with nothing else about
        // it changed, so no attribute value is written; the row update alone must carry the new Partition.
        var (systemId, typeId, externalIdAttributeId, oldPartitionId) = await SeedConnectedSystemGraphAsync();
        int newPartitionId;
        await using (var seed = NewContext())
        {
            var system = await seed.ConnectedSystems.AsTracking().SingleAsync(cs => cs.Id == systemId);
            var partition = new ConnectedSystemPartition
            {
                ConnectedSystem = system, Name = "DC=partners,DC=example", ExternalId = $"partition-{Guid.NewGuid():N}", Selected = true
            };
            seed.ConnectedSystemPartitions.Add(partition);
            await seed.SaveChangesAsync();
            newPartitionId = partition.Id;
        }

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = systemId,
            TypeId = typeId,
            ExternalIdAttributeId = externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.NotJoined,
            Created = DateTime.UtcNow,
            PartitionId = oldPartitionId
        };
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.Add(cso);
            await seed.SaveChangesAsync();
        }

        cso.PartitionId = newPartitionId;

        // Act: the call the import's save phase makes, with no attribute value additions or removals
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.UpdateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { cso }, [], []);
        }

        // Assert
        await using var readContext = NewContext();
        var persisted = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == cso.Id);
        Assert.That(persisted.PartitionId, Is.EqualTo(newPartitionId));
    }

    [Test]
    public async Task UpdateConnectedSystemObjectPartitionsAsync_PlacedObject_WritesThePartitionAndNothingElseAsync()
    {
        // #2723: a successful export records where it wrote the object. Only the Partition is written: an object marked
        // for synchronisation, or stripped of its import state hash, would cost the next Delta Synchronisation and Full
        // Import work for a change neither reads.
        var (systemId, typeId, externalIdAttributeId, oldPartitionId) = await SeedConnectedSystemGraphAsync();
        var newPartitionId = await AddPartitionAsync(systemId);
        var lastUpdated = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var importStateHash = Guid.NewGuid();
        var placed = NewCso(systemId, typeId, externalIdAttributeId, oldPartitionId, lastUpdated, importStateHash);
        var bystander = NewCso(systemId, typeId, externalIdAttributeId, oldPartitionId, lastUpdated, importStateHash);
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.AddRange(placed, bystander);
            await seed.SaveChangesAsync();
        }

        int changed;
        int changedAgain;
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            changed = await repository.Sync.UpdateConnectedSystemObjectPartitionsAsync([(placed.Id, newPartitionId)]);
            changedAgain = await repository.Sync.UpdateConnectedSystemObjectPartitionsAsync([(placed.Id, newPartitionId)]);
        }

        await using var readContext = NewContext();
        var persisted = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == placed.Id);
        var persistedBystander = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == bystander.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.PartitionId, Is.EqualTo(newPartitionId));
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(changedAgain, Is.Zero, "only a Partition that differs is written");
            Assert.That(persisted.SynchronisationPending, Is.False, "a Partition-only change is not one for synchronisation to process");
            Assert.That(persisted.ImportStateHash, Is.EqualTo(importStateHash), "where an object is is no part of what it holds");
            Assert.That(persisted.LastUpdated, Is.EqualTo(lastUpdated));
            Assert.That(persisted.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(persistedBystander.PartitionId, Is.EqualTo(oldPartitionId));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemObjectPartitionsAsync_TrackedInstance_IsBroughtIntoLineSoALaterSaveKeepsTheNewPartitionAsync()
    {
        // The Worker's context lives for the whole run: a tracked instance left holding the old Partition would read
        // wrongly, and a whole-entity save of it would write the old Partition back over the new one.
        var (systemId, typeId, externalIdAttributeId, oldPartitionId) = await SeedConnectedSystemGraphAsync();
        var newPartitionId = await AddPartitionAsync(systemId);
        var cso = NewCso(systemId, typeId, externalIdAttributeId, oldPartitionId, lastUpdated: null, importStateHash: null);
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.Add(cso);
            await seed.SaveChangesAsync();
        }

        await using (var writeContext = NewContext())
        {
            var tracked = await writeContext.ConnectedSystemObjects.AsTracking().SingleAsync(o => o.Id == cso.Id);
            var repository = new PostgresDataRepository(writeContext);

            await repository.Sync.UpdateConnectedSystemObjectPartitionsAsync([(cso.Id, newPartitionId)]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracked.PartitionId, Is.EqualTo(newPartitionId));
                Assert.That(writeContext.Entry(tracked).Property(o => o.PartitionId).IsModified, Is.False, "it reads as persisted, not as a change to save");
            }

            writeContext.Entry(tracked).State = EntityState.Modified;
            tracked.LastUpdated = DateTime.UtcNow;
            await writeContext.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        var persisted = await readContext.ConnectedSystemObjects.AsNoTracking().SingleAsync(o => o.Id == cso.Id);
        Assert.That(persisted.PartitionId, Is.EqualTo(newPartitionId), "the later whole-entity save kept the new Partition");
    }

    private async Task<int> AddPartitionAsync(int systemId)
    {
        await using var seed = NewContext();
        var system = await seed.ConnectedSystems.AsTracking().SingleAsync(cs => cs.Id == systemId);
        var partition = new ConnectedSystemPartition
        {
            ConnectedSystem = system, Name = "DC=partners,DC=example", ExternalId = $"partition-{Guid.NewGuid():N}", Selected = true
        };
        seed.ConnectedSystemPartitions.Add(partition);
        await seed.SaveChangesAsync();
        return partition.Id;
    }

    private static ConnectedSystemObject NewCso(int systemId, int typeId, int externalIdAttributeId, int partitionId, DateTime? lastUpdated,
        Guid? importStateHash) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        TypeId = typeId,
        ExternalIdAttributeId = externalIdAttributeId,
        Status = ConnectedSystemObjectStatus.Normal,
        JoinType = ConnectedSystemObjectJoinType.NotJoined,
        Created = DateTime.UtcNow,
        LastUpdated = lastUpdated,
        ImportStateHash = importStateHash,
        PartitionId = partitionId
    };
}
