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
/// Real-PostgreSQL verification of the batch read behind connection criteria (#2021): one statement per page answers,
/// for each of the page's Metaverse Objects, which of the named Object Types it is joined to. An Obsolete object is not a
/// connection, so a leaver's scope moves on the synchronisation that obsoletes them, before the join itself is cleared.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectConnectionsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL connection read tests.");

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

    [Test]
    public async Task GetMetaverseObjectConnectionsAsync_PageOfObjects_ReadsJoinsToTheNamedTypesOnlyAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var panoply = new ConnectedSystem { Name = "PANOPLY AD", ConnectorDefinition = connectorDefinition };
        var resurgam = new ConnectedSystem { Name = "RESURGAM AD", ConnectorDefinition = connectorDefinition };
        var panoplyUser = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = panoply, Selected = true };
        var resurgamContact = new ConnectedSystemObjectType { Name = "contact", ConnectedSystem = resurgam, Selected = true };
        var person = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var ada = new MetaverseObject { Type = person };
        var ben = new MetaverseObject { Type = person };
        var cat = new MetaverseObject { Type = person };
        seed.AddRange(connectorDefinition, panoply, resurgam, panoplyUser, resurgamContact, person, ada, ben, cat);
        await seed.SaveChangesAsync();

        seed.AddRange(
            Joined(panoply, panoplyUser, ada, ConnectedSystemObjectStatus.Normal),
            Joined(resurgam, resurgamContact, ada, ConnectedSystemObjectStatus.PendingProvisioning),
            Joined(panoply, panoplyUser, ben, ConnectedSystemObjectStatus.Obsolete),
            new ConnectedSystemObject { ConnectedSystem = panoply, Type = panoplyUser, Status = ConnectedSystemObjectStatus.Normal });
        await seed.SaveChangesAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var connections = await repository.Sync.GetMetaverseObjectConnectionsAsync([ada.Id, ben.Id, cat.Id], [panoplyUser.Id]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connections.Has(ada.Id, panoplyUser.Id), Is.True);
            Assert.That(connections.Has(ben.Id, panoplyUser.Id), Is.False, "Ben's account is Obsolete: PANOPLY no longer holds it");
            Assert.That(connections.Has(cat.Id, panoplyUser.Id), Is.False, "Cat is joined to nothing");
            Assert.That(connections.Has(ada.Id, resurgamContact.Id), Is.Null, "the read was not asked about contacts, so it cannot say");
            Assert.That(connections.Has(Guid.NewGuid(), panoplyUser.Id), Is.Null, "an object outside the page was not read");
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionsAsync_NothingToRead_ReturnsAnEmptyAnswerWithoutQueryingAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var id = Guid.NewGuid();

        var noTypes = await repository.Sync.GetMetaverseObjectConnectionsAsync([id], []);
        var noObjects = await repository.Sync.GetMetaverseObjectConnectionsAsync([], [1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(noTypes.Has(id, 1), Is.Null);
            Assert.That(noObjects.Has(id, 1), Is.Null);
        }
    }

    private static ConnectedSystemObject Joined(ConnectedSystem system, ConnectedSystemObjectType type, MetaverseObject mvo, ConnectedSystemObjectStatus status) => new()
    {
        ConnectedSystem = system,
        Type = type,
        MetaverseObjectId = mvo.Id,
        JoinType = ConnectedSystemObjectJoinType.Joined,
        Status = status
    };
}
