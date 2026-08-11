// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that persisting a hierarchy against a <b>detached</b> Connected System inserts only
/// the new partitions and containers, and nothing else.
/// </summary>
/// <remarks>
/// Regression guard for the portal's "Retrieve Hierarchy" always failing. The portal loads the Connected System in
/// one scope and saves it in another, so the graph reaching the repository is detached; adding a newly discovered
/// partition with <c>DbSet.Add</c> made EF Core walk that partition's navigation back to the Connected System and
/// on to its Connector Definition, marking both for insertion. The save then failed on a duplicate Connector
/// Definition key, and no hierarchy could ever be retrieved from the portal. The REST endpoint was unaffected
/// because it loads the Connected System change-tracked in the same scope it saves from.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent. The EF Core in-memory provider enforces no key constraints and so cannot
/// reproduce this at all.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemHierarchyPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL hierarchy persistence tests.");

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

    /// <summary>
    /// Seeds a Connected System with a Connector Definition and one setting value, mirroring what an administrator
    /// has in place by the time they can ask for a hierarchy.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var setting = new ConnectorDefinitionSetting
        {
            Name = "Host",
            Type = ConnectedSystemSettingType.String
        };
        connectorDefinition.Settings.Add(setting);

        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition
        };
        system.SettingValues.Add(new ConnectedSystemSettingValue
        {
            ConnectedSystem = system,
            Setting = setting,
            StringValue = "directory.example.org"
        });

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();

        return system.Id;
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_DetachedSystemWithNewPartition_PersistsTheHierarchyAsync()
    {
        var systemId = await SeedAsync();

        // Load in one scope and let it go, exactly as the portal does: what the save scope receives is detached.
        ConnectedSystem detachedSystem;
        await using (var loadContext = NewContext())
        {
            var loadRepository = new PostgresDataRepository(loadContext);
            detachedSystem = (await loadRepository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
        }
        Assert.That(detachedSystem, Is.Not.Null);

        // What a hierarchy refresh produces: a newly discovered partition holding a newly discovered container,
        // both pointing back at the Connected System graph they were merged into.
        var newPartition = new ConnectedSystemPartition
        {
            ConnectedSystem = detachedSystem,
            ExternalId = "dc=example,dc=org",
            Name = "example.org",
            Containers = []
        };
        newPartition.Containers.Add(new ConnectedSystemContainer
        {
            Partition = newPartition,
            ExternalId = "ou=people,dc=example,dc=org",
            Name = "people"
        });
        detachedSystem.Partitions ??= [];
        detachedSystem.Partitions.Add(newPartition);

        await using (var saveContext = NewContext())
        {
            var saveRepository = new PostgresDataRepository(saveContext);
            await saveRepository.ConnectedSystems.UpdateConnectedSystemAsync(detachedSystem);
        }

        await using var verify = NewContext();
        var persistedPartitions = await verify.ConnectedSystemPartitions.ToListAsync();
        var persistedContainers = await verify.ConnectedSystemContainers.ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedPartitions, Has.Count.EqualTo(1), "The newly discovered partition must be persisted.");
            Assert.That(persistedPartitions[0].ExternalId, Is.EqualTo("dc=example,dc=org"));
            Assert.That(persistedContainers, Has.Count.EqualTo(1), "The partition's container must be persisted with it.");
            Assert.That(persistedContainers[0].ExternalId, Is.EqualTo("ou=people,dc=example,dc=org"));
            Assert.That(await verify.ConnectorDefinitions.CountAsync(), Is.EqualTo(1),
                "Saving a hierarchy must not insert a second copy of the Connector Definition.");
            Assert.That(await verify.ConnectedSystems.CountAsync(), Is.EqualTo(1),
                "Saving a hierarchy must not insert a second copy of the Connected System.");
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_DetachedSystemWithNewContainerUnderExistingPartition_PersistsTheContainerAsync()
    {
        var systemId = await SeedAsync();

        // Establish a persisted partition first, so the second refresh is the "new container only" case that the
        // export path also takes when it creates containers as needed.
        await using (var firstContext = NewContext())
        {
            var repository = new PostgresDataRepository(firstContext);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            system.Partitions ??= [];
            system.Partitions.Add(new ConnectedSystemPartition
            {
                ConnectedSystem = system,
                ExternalId = "dc=example,dc=org",
                Name = "example.org"
            });
            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        ConnectedSystem detachedSystem;
        await using (var loadContext = NewContext())
        {
            var loadRepository = new PostgresDataRepository(loadContext);
            detachedSystem = (await loadRepository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
        }

        var existingPartition = detachedSystem.Partitions!.Single();
        existingPartition.Containers ??= [];
        existingPartition.Containers.Add(new ConnectedSystemContainer
        {
            Partition = existingPartition,
            ExternalId = "ou=groups,dc=example,dc=org",
            Name = "groups"
        });

        await using (var saveContext = NewContext())
        {
            var saveRepository = new PostgresDataRepository(saveContext);
            await saveRepository.ConnectedSystems.UpdateConnectedSystemAsync(detachedSystem);
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemContainers.CountAsync(), Is.EqualTo(1));
            Assert.That(await verify.ConnectedSystemPartitions.CountAsync(), Is.EqualTo(1));
            Assert.That(await verify.ConnectorDefinitions.CountAsync(), Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A Container's scope statement survives a round trip through the database, including the exclusion added for
    /// #1255.
    /// </summary>
    /// <remarks>
    /// The in-memory provider would pass this whatever the EF model said, because it maps whatever the type has. A
    /// column silently absent from the real schema (a mapping annotation on the wrong property, a migration that
    /// never reached the model snapshot) shows up only here, as a value that reads back as its default. Reading the
    /// exclusion back as <c>false</c> is precisely the failure that would import a branch an administrator had
    /// deliberately carved out.
    /// </remarks>
    [Test]
    public async Task UpdateConnectedSystemAsync_ContainerWithAnExclusion_PersistsTheExclusionAndScopeAsync()
    {
        var systemId = await SeedAsync();

        await using (var firstContext = NewContext())
        {
            var repository = new PostgresDataRepository(firstContext);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            var partition = new ConnectedSystemPartition
            {
                ConnectedSystem = system,
                ExternalId = "dc=example,dc=org",
                Name = "example.org",
                Selected = true,
                Containers = []
            };
            partition.Containers.Add(new ConnectedSystemContainer
            {
                Partition = partition,
                ExternalId = "ou=corp,dc=example,dc=org",
                Name = "corp",
                Selected = true,
                Scope = ConnectedSystemContainerScope.Subtree
            });
            partition.Containers.Add(new ConnectedSystemContainer
            {
                Partition = partition,
                ExternalId = "ou=service accounts,ou=corp,dc=example,dc=org",
                Name = "service accounts",
                Excluded = true,
                Scope = ConnectedSystemContainerScope.OneLevel
            });
            system.Partitions ??= [];
            system.Partitions.Add(partition);

            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        await using var verify = NewContext();
        var corp = await verify.ConnectedSystemContainers.SingleAsync(c => c.Name == "corp");
        var serviceAccounts = await verify.ConnectedSystemContainers.SingleAsync(c => c.Name == "service accounts");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp.Selected, Is.True);
            Assert.That(corp.Excluded, Is.False);
            Assert.That(corp.Scope, Is.EqualTo(ConnectedSystemContainerScope.Subtree));
            Assert.That(serviceAccounts.Excluded, Is.True, "the exclusion must survive the round trip, or the branch is imported after all");
            Assert.That(serviceAccounts.Selected, Is.False);
            Assert.That(serviceAccounts.Scope, Is.EqualTo(ConnectedSystemContainerScope.OneLevel),
                "Scope says how far a Container's statement reaches, whether that statement is a selection or an exclusion");
        }
    }
}
