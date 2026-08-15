// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
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
    /// A Container moved to the partition root by a hierarchy refresh persists in its new home, keeping its
    /// selection (#1318).
    /// </summary>
    /// <remarks>
    /// The reparent has to clear the parent-container foreign key as it sets the partition one, and vice versa;
    /// only top-level Containers carry a partition and only nested ones carry a parent. A stale scalar left behind
    /// has the row claim two homes, which the in-memory provider is happy to accept and PostgreSQL is not. The
    /// merge is run against a graph loaded in one scope and saved in another, matching the portal.
    /// </remarks>
    [Test]
    public async Task MergeHierarchy_ContainerMovedToThePartitionRoot_PersistsItsNewParentageAsync()
    {
        var systemId = await SeedAsync();

        await using (var seedContext = NewContext())
        {
            var repository = new PostgresDataRepository(seedContext);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            var partition = new ConnectedSystemPartition
            {
                ConnectedSystem = system,
                ExternalId = "dc=example,dc=org",
                Name = "example.org",
                Selected = true,
                Containers = []
            };
            var corp = new ConnectedSystemContainer
            {
                Partition = partition,
                ExternalId = "ou=corp,dc=example,dc=org",
                Name = "corp",
                StableId = "corp-stable-id"
            };
            corp.AddChildContainer(new ConnectedSystemContainer
            {
                ExternalId = "ou=sales,ou=corp,dc=example,dc=org",
                Name = "sales",
                StableId = "sales-stable-id",
                Selected = true
            });
            partition.Containers.Add(corp);
            system.Partitions ??= [];
            system.Partitions.Add(partition);

            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        // Load detached, exactly as the portal does, then merge in a discovery that reports sales at the root.
        ConnectedSystem detachedSystem;
        await using (var loadContext = NewContext())
        {
            var loadRepository = new PostgresDataRepository(loadContext);
            detachedSystem = (await loadRepository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
        }

        ConnectedSystemServer.MergeHierarchy(
            detachedSystem,
            [
                new ConnectorPartition
                {
                    Id = "dc=example,dc=org",
                    Name = "example.org",
                    Containers =
                    [
                        new ConnectorContainer("ou=corp,dc=example,dc=org", "corp") { StableId = "corp-stable-id" },
                        new ConnectorContainer("ou=sales,dc=example,dc=org", "sales") { StableId = "sales-stable-id" }
                    ]
                }
            ]);

        await using (var saveContext = NewContext())
        {
            var saveRepository = new PostgresDataRepository(saveContext);
            await saveRepository.ConnectedSystems.UpdateConnectedSystemAsync(detachedSystem);
        }

        await using var verify = NewContext();
        var persistedPartition = await verify.ConnectedSystemPartitions.SingleAsync();
        var sales = await verify.ConnectedSystemContainers.SingleAsync(c => c.Name == "sales");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemContainers.CountAsync(), Is.EqualTo(2), "the moved Container must not have been deleted");
            Assert.That(sales.Selected, Is.True, "and must keep its selection");
            Assert.That(sales.ParentContainerId, Is.Null, "it no longer sits under corp");
            Assert.That(sales.PartitionId, Is.EqualTo(persistedPartition.Id), "it now sits at the partition root");
            Assert.That(sales.ExternalId, Is.EqualTo("ou=sales,dc=example,dc=org"));
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

    /// <summary>
    /// The discovered hierarchy for the merge-driven tests below: one naming context holding two containers, which
    /// is the shape an LDAP Connected System presents on its first import.
    /// </summary>
    private static List<ConnectorPartition> DiscoveredHierarchy() =>
    [
        new()
        {
            Id = "dc=yellowstone,dc=local",
            Name = "dc=yellowstone,dc=local",
            Containers =
            [
                new ConnectorContainer("ou=People,dc=yellowstone,dc=local", "People"),
                new ConnectorContainer("ou=Groups,dc=yellowstone,dc=local", "Groups")
            ]
        }
    ];

    /// <summary>
    /// The first import against a Connected System with no partitions yet must keep the containers it discovered.
    /// </summary>
    /// <remarks>
    /// This goes through <see cref="ConnectedSystemServer.MergeHierarchy"/> rather than hand-building the graph, and
    /// that is the whole point: the tests above construct a new partition and its containers directly, so they never
    /// ran the merge's removal pass and stayed green while the real import path was broken. Containers are recorded
    /// as matched while the merge walks each EXISTING partition, and a later pass deletes every container not in that
    /// set so a container moved between parents survives; a NEW partition took a different branch and never recorded
    /// its containers, so the removal pass deleted every one of them. The partitions saved, the containers vanished,
    /// and the refresh still reported them as added.
    ///
    /// What an administrator saw: add a Connected System, retrieve the hierarchy, and the partitions appear with no
    /// containers to select. A second import populated them, because by then the partitions existed and the merge
    /// took the matching branch instead.
    /// </remarks>
    [Test]
    public async Task MergeHierarchy_FirstImportOnSystemWithNoPartitions_KeepsTheDiscoveredContainersAsync()
    {
        var systemId = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            Assert.That(system.Partitions is null || system.Partitions.Count == 0, Is.True,
                "the seeded Connected System must start with no partitions, so this exercises the first-import path");

            ConnectedSystemServer.MergeHierarchy(system, DiscoveredHierarchy());
            Assert.That(system.Partitions!.Single().Containers, Has.Count.EqualTo(2),
                "the merge must leave the discovered containers attached to the new partition; a failure here is the merge losing them before persistence is even reached");

            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        await using var verify = NewContext();
        var containers = await verify.ConnectedSystemContainers.ToListAsync();
        Assert.That(containers.Select(c => c.Name), Is.EquivalentTo(new[] { "People", "Groups" }),
            "the containers discovered under a brand-new partition must persist on the FIRST import; without them an " +
            "administrator has nothing to select and has to import a second time");
    }

    /// <summary>
    /// Re-importing an unchanged hierarchy must be a no-op: the same containers, neither duplicated by the adding
    /// branch nor deleted by the removal pass.
    /// </summary>
    [Test]
    public async Task MergeHierarchy_RepeatImportOfUnchangedHierarchy_LeavesTheSameContainersAsync()
    {
        var systemId = await SeedAsync();

        for (var pass = 0; pass < 2; pass++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            ConnectedSystemServer.MergeHierarchy(system, DiscoveredHierarchy());
            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        await using var verify = NewContext();
        var containers = await verify.ConnectedSystemContainers.ToListAsync();
        Assert.That(containers.Select(c => c.Name), Is.EquivalentTo(new[] { "People", "Groups" }),
            "a repeat import of an unchanged hierarchy must leave exactly the same containers");
    }
}
