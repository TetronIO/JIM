// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the counts on a Connected System header: objects, joined objects and Pending Exports.
/// <para>
/// The headers feed the Connected Systems list, which loads every header at once. The counts are aggregated in one
/// grouped pass per table rather than by a correlated count per system, because the correlated form read every
/// Connected System Object twice and took the best part of a second at 1.5 million objects. These tests pin what the
/// counts must be, so the shape of the query can change without the figures changing. The in-memory provider cannot
/// stand in for them: it never translates the grouped aggregate to SQL at all.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemHeaderCountsDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System header count tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    /// <summary>
    /// Seeds two Connected Systems behind one Connector Definition: "Busy" with three objects (two joined to a
    /// Metaverse Object) and one Pending Export, and "Quiet" with nothing in its Connector Space.
    /// </summary>
    private async Task<(int BusyId, int QuietId, int DefinitionId)> SeedAsync()
    {
        await using var seed = NewContext();

        var definition = new ConnectorDefinition { Name = "Header Count Connector" };
        // Named so the sort order is the reverse of creation order, which proves the headers are ordered by name.
        var busy = new ConnectedSystem { Name = "B Busy", ConnectorDefinition = definition };
        var quiet = new ConnectedSystem { Name = "A Quiet", ConnectorDefinition = definition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = busy, Selected = true };
        var mvType = new MetaverseObjectType { Name = "Header Count User", PluralName = "Header Count Users" };
        seed.AddRange(definition, busy, quiet, csType, mvType);
        await seed.SaveChangesAsync();

        var joinedOne = new MetaverseObject { Type = mvType };
        var joinedTwo = new MetaverseObject { Type = mvType };
        seed.AddRange(joinedOne, joinedTwo);
        seed.AddRange(
            new ConnectedSystemObject { Type = csType, ConnectedSystem = busy, MetaverseObject = joinedOne, JoinType = ConnectedSystemObjectJoinType.Joined, DateJoined = DateTime.UtcNow },
            new ConnectedSystemObject { Type = csType, ConnectedSystem = busy, MetaverseObject = joinedTwo, JoinType = ConnectedSystemObjectJoinType.Joined, DateJoined = DateTime.UtcNow },
            new ConnectedSystemObject { Type = csType, ConnectedSystem = busy });
        await seed.SaveChangesAsync();

        seed.Add(new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = busy.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        });
        await seed.SaveChangesAsync();

        return (busy.Id, quiet.Id, definition.Id);
    }

    [Test]
    public async Task GetConnectedSystemHeadersAsync_SystemsWithAndWithoutObjects_CountsEachSystemAndOrdersByNameAsync()
    {
        var (busyId, quietId, definitionId) = await SeedAsync();

        await using var ctx = NewContext();
        var headers = await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemHeadersAsync();

        Assert.That(headers.Select(h => h.Id), Is.EqualTo(new[] { quietId, busyId }), "Headers must be ordered by name.");
        var quiet = headers[0];
        var busy = headers[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(busy.Name, Is.EqualTo("B Busy"));
            Assert.That(busy.ObjectCount, Is.EqualTo(3));
            Assert.That(busy.ConnectorsCount, Is.EqualTo(2));
            Assert.That(busy.PendingExportObjectsCount, Is.EqualTo(1));
            Assert.That(busy.ConnectorName, Is.EqualTo("Header Count Connector"));
            Assert.That(busy.ConnectorId, Is.EqualTo(definitionId));

            Assert.That(quiet.ObjectCount, Is.Zero);
            Assert.That(quiet.ConnectorsCount, Is.Zero);
            Assert.That(quiet.PendingExportObjectsCount, Is.Zero);
            Assert.That(quiet.ConnectorName, Is.EqualTo("Header Count Connector"));
        }
    }

    [Test]
    public async Task GetConnectedSystemHeaderAsync_SystemWithObjects_CountsOnlyThatSystemAsync()
    {
        var (busyId, quietId, _) = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;
        var busy = await repository.GetConnectedSystemHeaderAsync(busyId);
        var quiet = await repository.GetConnectedSystemHeaderAsync(quietId);
        var missing = await repository.GetConnectedSystemHeaderAsync(int.MaxValue);

        Assert.That(busy, Is.Not.Null);
        Assert.That(quiet, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(busy!.ObjectCount, Is.EqualTo(3));
            Assert.That(busy!.ConnectorsCount, Is.EqualTo(2));
            Assert.That(busy!.PendingExportObjectsCount, Is.EqualTo(1));
            Assert.That(quiet!.ObjectCount, Is.Zero);
            Assert.That(quiet!.PendingExportObjectsCount, Is.Zero);
            Assert.That(missing, Is.Null);
        }
    }
}
