// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that the Synchronisation Rules an export evaluation loads carry everything JIM
/// needs to work out an object's class membership.
/// </summary>
/// <remarks>
/// Export evaluation computes the class membership attribute (objectClass on an RFC 4512 directory) from the
/// Object Type's tags, the auxiliary classes an administrator merged into it, and any structural carrier. A graph
/// that does not fetch those is one the computation cannot see: it would quietly plan nothing, exports would go out
/// without the classes their attributes need, and the directory would reject them one at a time.
///
/// The EF Core in-memory provider cannot make this assertion, because it populates navigations from its change
/// tracker whether the query asked for them or not. Opt in via the same <c>JIM_TEST_RESET_*</c> environment
/// variables as the other database tests.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleClassMembershipGraphDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Synchronisation Rule graph tests.");

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

    [Test]
    public async Task GetSyncRulesForAConnectedSystem_LoadTheGraphClassMembershipNeedsAsync()
    {
        var connectedSystemId = await SeedAsync();

        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        var syncRules = await repository.ConnectedSystems.GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules: true);

        var objectType = syncRules.Single().ConnectedSystemObjectType;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Tags.Any(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute), Is.True,
                "without the tag JIM does not know the Connected System has class membership at all, and plans nothing");
            Assert.That(objectType.Extensions, Is.Not.Empty,
                "without the administrator's selections JIM cannot know which auxiliary classes an object may belong to");
            Assert.That(objectType.Extensions[0].ExtensionObjectType, Is.Not.Null,
                "the selection is only useful if it can be resolved to the class it names");
            Assert.That(objectType.StructuralCarrierObjectType, Is.Not.Null,
                "an auxiliary-typed object cannot be created without the structural class that carries it");
        }
    }

    /// <summary>
    /// Seeds a Connected System shaped like the one this feature exists for: a structural type declaring a class
    /// membership attribute, an auxiliary class merged into it, and a structural carrier named.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test LDAP Connector", BuiltIn = true };
        var account = new ConnectedSystemObjectType { Name = "account" };
        var posixAccount = new ConnectedSystemObjectType { Name = "posixAccount" };
        var person = new ConnectedSystemObjectType
        {
            Name = "inetOrgPerson",
            StructuralCarrierObjectType = account,
            Tags =
            [
                new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural },
                new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = "objectClass" }
            ]
        };

        var connectedSystem = new ConnectedSystem
        {
            Name = "Yellowstone",
            ConnectorDefinition = connectorDefinition,
            ObjectTypes = [person, posixAccount, account]
        };

        var metaverseObjectType = new MetaverseObjectType { Name = "User", PluralName = "Users" };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(connectedSystem);
        seed.MetaverseObjectTypes.Add(metaverseObjectType);
        await seed.SaveChangesAsync();

        seed.ConnectedSystemObjectTypeExtensions.Add(new ConnectedSystemObjectTypeExtension
        {
            BaseObjectTypeId = person.Id,
            ExtensionObjectTypeId = posixAccount.Id
        });

        seed.SyncRules.Add(new SyncRule
        {
            Name = "Users to Yellowstone",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemObjectTypeId = person.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id
        });

        await seed.SaveChangesAsync();
        return connectedSystem.Id;
    }
}
