// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL check that the Metaverse Object detail load reports how many Connected System Objects are
/// joined (#1519): the Metaverse Object detail page badges its Connections tab from this count without
/// loading the objects.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MvoDetailConnectorCountDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object detail connector count tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task GetMetaverseObjectDetailAsync_TwoJoinedObjectsAndOneUnjoined_ReportsTwoConnectorsAsync()
    {
        // Arrange - one Metaverse Object with two joined objects across two systems, and an unjoined object that must not count
        Guid joinedMvoId;
        Guid lonelyMvoId;
        await using (var seed = NewContext())
        {
            var mvoType = new MetaverseObjectType { Name = $"Person-{Guid.NewGuid():N}", PluralName = "People" };
            var joinedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
            var lonelyMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
            seed.MetaverseObjects.AddRange(joinedMvo, lonelyMvo);

            var connectorDefinition = new ConnectorDefinition { Name = $"Connector-{Guid.NewGuid():N}", BuiltIn = false };
            var systemA = new ConnectedSystem { Name = $"A-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
            var systemB = new ConnectedSystem { Name = $"B-{Guid.NewGuid():N}", ConnectorDefinition = connectorDefinition };
            seed.ConnectedSystems.AddRange(systemA, systemB);
            await seed.SaveChangesAsync();

            var typeA = new ConnectedSystemObjectType { ConnectedSystemId = systemA.Id, Name = "user", Selected = true };
            var typeB = new ConnectedSystemObjectType { ConnectedSystemId = systemB.Id, Name = "user", Selected = true };
            seed.ConnectedSystemObjectTypes.AddRange(typeA, typeB);
            await seed.SaveChangesAsync();

            seed.ConnectedSystemObjects.AddRange(
                new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = systemA.Id, TypeId = typeA.Id, MetaverseObjectId = joinedMvo.Id, JoinType = ConnectedSystemObjectJoinType.Joined },
                new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = systemB.Id, TypeId = typeB.Id, MetaverseObjectId = joinedMvo.Id, JoinType = ConnectedSystemObjectJoinType.Provisioned },
                new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = systemA.Id, TypeId = typeA.Id, MetaverseObjectId = null, JoinType = ConnectedSystemObjectJoinType.NotJoined });
            await seed.SaveChangesAsync();

            joinedMvoId = joinedMvo.Id;
            lonelyMvoId = lonelyMvo.Id;
        }

        // Act
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var joinedDetail = await repository.Metaverse.GetMetaverseObjectDetailAsync(joinedMvoId, MvoAttributeLoadStrategy.CappedMva);
        var lonelyDetail = await repository.Metaverse.GetMetaverseObjectDetailAsync(lonelyMvoId, MvoAttributeLoadStrategy.CappedMva);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(joinedDetail, Is.Not.Null);
            Assert.That(joinedDetail!.ConnectorCount, Is.EqualTo(2), "Both joined objects count; the unjoined one does not");
            Assert.That(lonelyDetail, Is.Not.Null);
            Assert.That(lonelyDetail!.ConnectorCount, Is.Zero);
        }
    }
}
