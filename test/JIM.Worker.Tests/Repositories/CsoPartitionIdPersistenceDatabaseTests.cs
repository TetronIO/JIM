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
}
