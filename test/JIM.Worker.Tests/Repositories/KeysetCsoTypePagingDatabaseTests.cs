// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the synchronisation page loaders' per-Object-Type keyset paging and creation floor
/// (#2492): a type-ordered run walks each type's keyset on its own, then sweeps for objects created mid-run.
/// </summary>
/// <remarks>
/// The keyset cursor is only correct when the engine that ordered the page also compares the cursor, so this is
/// asserted against PostgreSQL. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class KeysetCsoTypePagingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL per-type keyset CSO paging tests.");

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

    private sealed record Seeded(int SystemId, int PersonTypeId, int GroupTypeId, HashSet<Guid> PersonIds, HashSet<Guid> GroupIds);

    private async Task<Seeded> SeedAsync(int people, int groups)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone", ConnectorDefinition = connectorDefinition };
        var person = NewType(system, "person");
        var group = NewType(system, "group");
        seed.AddRange(connectorDefinition, system, person, group);
        await seed.SaveChangesAsync();

        var personIds = AddObjects(seed, system, person, people);
        var groupIds = AddObjects(seed, system, group, groups);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, person.Id, group.Id, personIds, groupIds);
    }

    private static ConnectedSystemObjectType NewType(ConnectedSystem system, string name)
    {
        var type = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
        type.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "id", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        });
        return type;
    }

    private static HashSet<Guid> AddObjects(JimDbContext context, ConnectedSystem system, ConnectedSystemObjectType type, int count, DateTime? created = null)
    {
        var externalId = type.Attributes[0];
        var ids = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var cso = new ConnectedSystemObject
            {
                Type = type, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = externalId.Id
            };
            if (created.HasValue)
                cso.Created = created.Value;
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Attribute = externalId, AttributeId = externalId.Id, StringValue = $"{type.Name}{Guid.NewGuid():N}"
            });
            context.Add(cso);
            ids.Add(cso.Id);
        }
        return ids;
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_KeysetCursorWithinType_EnumeratesOnlyThatTypeOnceAsync()
    {
        var seeded = await SeedAsync(people: 8, groups: 5);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var personPages = await WalkAsync(cursor => repository.Sync.GetConnectedSystemObjectsAsync(
            seeded.SystemId, 1, pageSize: 3, knownTotalCount: 13, afterId: cursor, objectTypeId: seeded.PersonTypeId));
        var groupPages = await WalkAsync(cursor => repository.Sync.GetConnectedSystemObjectsAsync(
            seeded.SystemId, 1, pageSize: 3, knownTotalCount: 13, afterId: cursor, objectTypeId: seeded.GroupTypeId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(personPages, Is.Unique);
            Assert.That(personPages, Is.EquivalentTo(seeded.PersonIds));
            Assert.That(groupPages, Is.Unique);
            Assert.That(groupPages, Is.EquivalentTo(seeded.GroupIds));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_KeysetCursorWithinType_EnumeratesOnlyThatTypeOnceAsync()
    {
        var modifiedSince = DateTime.UtcNow.AddHours(-1);
        var seeded = await SeedAsync(people: 7, groups: 4);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var groupPages = await WalkAsync(cursor => repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seeded.SystemId, modifiedSince, 1, pageSize: 3, knownTotalCount: 11, afterId: cursor, objectTypeId: seeded.GroupTypeId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groupPages, Is.Unique);
            Assert.That(groupPages, Is.EquivalentTo(seeded.GroupIds));
        }
    }

    /// <summary>
    /// The closing sweep: once every type has been paged, an object of an earlier type created while the run was in
    /// flight sits behind a cursor that has moved on to another type. The creation floor reaches it, across types.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsAsync_CreatedOnOrAfter_ReachesObjectsCreatedMidRunOfEveryTypeAsync()
    {
        var seeded = await SeedAsync(people: 6, groups: 3);
        var runStartedAt = DateTime.UtcNow.AddSeconds(1);

        HashSet<Guid> lateIds;
        await using (var insert = NewContext())
        {
            var system = await insert.ConnectedSystems.SingleAsync(cs => cs.Id == seeded.SystemId);
            var person = await insert.ConnectedSystemObjectTypes.Include(t => t.Attributes).SingleAsync(t => t.Id == seeded.PersonTypeId);
            var group = await insert.ConnectedSystemObjectTypes.Include(t => t.Attributes).SingleAsync(t => t.Id == seeded.GroupTypeId);
            lateIds = AddObjects(insert, system, person, 2, runStartedAt.AddSeconds(5));
            lateIds.UnionWith(AddObjects(insert, system, group, 2, runStartedAt.AddSeconds(5)));
            await insert.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var swept = await WalkAsync(cursor => repository.Sync.GetConnectedSystemObjectsAsync(
            seeded.SystemId, 1, pageSize: 3, knownTotalCount: 13, afterId: cursor, createdOnOrAfter: runStartedAt));

        Assert.That(swept, Is.EquivalentTo(lateIds));
    }

    private static async Task<List<Guid>> WalkAsync(Func<Guid, Task<JIM.Models.Utility.PagedResultSet<ConnectedSystemObject>>> loadPage)
    {
        var seen = new List<Guid>();
        var cursor = Guid.Empty;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = await loadPage(cursor);
            if (page.Results.Count == 0)
                return seen;
            seen.AddRange(page.Results.Select(cso => cso.Id));
            cursor = page.Results[^1].Id;
        }

        Assert.Fail("Keyset walk did not terminate");
        return seen;
    }
}
