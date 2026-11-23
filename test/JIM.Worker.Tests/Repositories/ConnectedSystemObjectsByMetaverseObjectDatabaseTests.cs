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
/// Real-PostgreSQL coverage for <c>GetConnectedSystemObjectsByMetaverseObjectIdAsync</c>, the read the password channel
/// delivers through: it hands each account to its Connector, which may decide by the account's Object Type.
/// </summary>
/// <remarks>
/// Found by Scenario 036 against a real Entra ID tenant (#361): the read did not load the Object Type, so the Microsoft
/// Entra ID Connector, which sets a password only on a user, refused every Initial Password as not a user. The LDAP
/// Connector never looks at the type, and the in-memory provider fixes navigations up from tracked graphs, so nothing
/// else could see it.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectsByMetaverseObjectDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System Objects by Metaverse Object tests.");

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
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdAsync_JoinedAccount_LoadsItsObjectTypeAndExternalIdAsync()
    {
        Guid metaverseObjectId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Entra ID", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var idAttribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = "id", ConnectedSystemObjectType = userType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            userType.Attributes.Add(idAttribute);
            var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
            seed.AddRange(connectorDefinition, system, userType, mvType);
            await seed.SaveChangesAsync();

            var mvo = new MetaverseObject { Type = mvType };
            var cso = new ConnectedSystemObject
            {
                Type = userType,
                ConnectedSystem = system,
                Status = ConnectedSystemObjectStatus.Normal,
                MetaverseObject = mvo,
                JoinType = ConnectedSystemObjectJoinType.Provisioned,
                DateJoined = DateTime.UtcNow,
                ExternalIdAttributeId = idAttribute.Id
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = idAttribute, StringValue = "entra-object-id" });
            seed.AddRange(mvo, cso);
            await seed.SaveChangesAsync();
            metaverseObjectId = mvo.Id;
        }

        // A fresh context, as the password channel's is: nothing is fixed up from what the seed tracked.
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var accounts = await repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId);

        var account = accounts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Type, Is.Not.Null, "The Object Type is loaded, so a Connector can decide by it.");
            Assert.That(account.Type?.Name, Is.EqualTo("user"));
            Assert.That(account.ExternalIdAttributeValue?.StringValue, Is.EqualTo("entra-object-id"));
        }
    }
}
