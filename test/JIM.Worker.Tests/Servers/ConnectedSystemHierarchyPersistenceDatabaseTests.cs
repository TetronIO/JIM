// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Serilog;

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
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
    /// The regression guard for #1369, which fixed the fault but shipped without one. It goes through
    /// <see cref="ConnectedSystemServer.MergeHierarchy"/> rather than hand-building the graph, and that is the whole
    /// point: the tests above construct a new partition and its containers directly, so they never ran the merge's
    /// removal pass and stayed green while the real retrieval path was broken. Containers are recorded as matched
    /// while the merge walks each EXISTING partition, and a later pass deletes every container not in that set so a
    /// container moved between parents survives; a NEW partition took a different branch and never recorded its
    /// containers, so the removal pass deleted every one of them. The partitions saved, the containers vanished, and
    /// the refresh still reported them as added.
    ///
    /// What an administrator saw: add a Connected System, retrieve the hierarchy, and the partitions appear with no
    /// containers to select. A second retrieval populated them, because by then the partitions existed and the merge
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

    /// <summary>
    /// A Partition the directory no longer holds is removed by a hierarchy refresh, and its Delta Import watermark
    /// (#2715) goes with it by cascade; one the directory still holds keeps its watermark, because Partitions are
    /// matched by their external id, so a refresh does not replace them.
    /// </summary>
    /// <remarks>
    /// Loaded change-tracked in the scope it is saved from, as the REST endpoint does: removing a Partition from a
    /// tracked Connected System's collection is what deletes its row.
    /// </remarks>
    [Test]
    public async Task RefreshHierarchy_PartitionNoLongerInTheDirectory_RemovesItsWatermarkAsync()
    {
        var systemId = await SeedAsync();
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
            ConnectedSystemServer.MergeHierarchy(system,
            [
                new ConnectorPartition { Id = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Containers = [] },
                new ConnectorPartition { Id = "dc=glitterband,dc=local", Name = "dc=glitterband,dc=local", Containers = [] }
            ]);
            // A refresh discovers Partitions unselected; only a selected Partition is read, and so holds a watermark.
            foreach (var partition in system.Partitions!)
                partition.Selected = true;
            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        int yellowstoneId, glitterbandId;
        await using (var ctx = NewContext())
        {
            var partitions = await ctx.ConnectedSystemPartitions.ToListAsync();
            yellowstoneId = partitions.Single(p => p.ExternalId == "dc=yellowstone,dc=local").Id;
            glitterbandId = partitions.Single(p => p.ExternalId == "dc=glitterband,dc=local").Id;
            await new PostgresDataRepository(ctx).ConnectedSystems.RecordImportWatermarksAsync(systemId, null,
                new Dictionary<int, string> { [yellowstoneId] = "yellowstone-1", [glitterbandId] = "glitterband-1" }, [], DateTime.UtcNow);
        }

        // The directory no longer has Glitterband.
        var trackedOptions = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var ctx = new JimDbContext(trackedOptions))
        {
            var repository = new PostgresDataRepository(ctx);
            var system = (await repository.ConnectedSystems.GetConnectedSystemAsync(systemId, withChangeTracking: true))!;
            var result = ConnectedSystemServer.MergeHierarchy(system,
                [new ConnectorPartition { Id = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Containers = [] }]);
            Assert.That(result.RemovedPartitions.Select(p => p.ExternalId), Is.EqualTo(new[] { "dc=glitterband,dc=local" }), "precondition");
            await repository.ConnectedSystems.UpdateConnectedSystemAsync(system);
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemPartitions.AnyAsync(p => p.Id == glitterbandId), Is.False, "precondition: the refresh removed the Partition");
            Assert.That(await verify.ConnectedSystemPartitionWatermarks.Select(w => w.PartitionId).ToListAsync(), Is.EqualTo(new[] { yellowstoneId }),
                "the removed Partition's watermark goes with it, and the kept one's stays");
        }
    }

    /// <summary>
    /// A Partition the directory no longer holds is removed by a hierarchy refresh even while Connected System Objects
    /// are still stamped with it; the objects stay, with no Partition (#2723).
    /// </summary>
    /// <remarks>
    /// The test above seeds no objects, and that is the only reason it ever passed: the objects' foreign key to their
    /// Partition had no delete rule, so PostgreSQL refused to delete a Partition any object still referenced (23503)
    /// and the whole refresh failed. Clearing the objects' Partition is the right answer rather than deleting them:
    /// whether an object has gone from the directory is for deletion detection to decide, not for a hierarchy refresh.
    ///
    /// Driven through the server's own refresh, on a context configured as JIM.Web runs it.
    /// </remarks>
    [Test]
    public async Task RefreshHierarchy_PartitionHoldingConnectedSystemObjectsNoLongerInTheDirectory_RemovesItAndClearsTheirPartitionAsync()
    {
        int systemId, yellowstoneId, glitterbandId;
        Guid initiatorId, yellowstoneObjectId;
        Guid[] glitterbandObjectIds;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test LDAP Connector", SupportsPartitions = true };
            var setting = new ConnectorDefinitionSetting { Name = "Host", Type = ConnectedSystemSettingType.String };
            connectorDefinition.Settings.Add(setting);
            var system = new ConnectedSystem
            {
                Name = "Test Directory",
                ConnectorDefinition = connectorDefinition,
                SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "directory.example.local" }]
            };
            var yellowstone = new ConnectedSystemPartition
            {
                ConnectedSystem = system, ExternalId = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Selected = true
            };
            var glitterband = new ConnectedSystemPartition
            {
                ConnectedSystem = system, ExternalId = "dc=glitterband,dc=local", Name = "dc=glitterband,dc=local", Selected = true
            };
            var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = "entryUUID", ConnectedSystemObjectType = userType, Type = AttributeDataType.Guid, IsExternalId = true, Selected = true
            };
            userType.Attributes.Add(externalIdAttribute);

            // A hierarchy refresh is a configuration change, recorded against a security principal or not at all.
            var mvUserType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
            var initiator = new MetaverseObject { Type = mvUserType, CachedDisplayName = "Test Administrator" };

            seed.AddRange(connectorDefinition, system, yellowstone, glitterband, userType, mvUserType, initiator);
            await seed.SaveChangesAsync();

            ConnectedSystemObject NewObject(ConnectedSystemPartition partition) => new()
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = system.Id,
                TypeId = userType.Id,
                ExternalIdAttributeId = externalIdAttribute.Id,
                Status = ConnectedSystemObjectStatus.Normal,
                JoinType = ConnectedSystemObjectJoinType.NotJoined,
                Created = DateTime.UtcNow,
                PartitionId = partition.Id
            };
            var glitterbandObjects = new[] { NewObject(glitterband), NewObject(glitterband) };
            var yellowstoneObject = NewObject(yellowstone);
            seed.ConnectedSystemObjects.AddRange(glitterbandObjects);
            seed.ConnectedSystemObjects.Add(yellowstoneObject);
            await seed.SaveChangesAsync();

            (systemId, yellowstoneId, glitterbandId, initiatorId) = (system.Id, yellowstone.Id, glitterband.Id, initiator.Id);
            yellowstoneObjectId = yellowstoneObject.Id;
            glitterbandObjectIds = glitterbandObjects.Select(o => o.Id).ToArray();
        }

        // The directory no longer has Glitterband.
        var connector = new StubPartitionsConnector(
            [new ConnectorPartition { Id = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Containers = [] }]);
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx), connectorFactory: new StubConnectorFactory(connector));
            var initiator = await ctx.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);

            var result = await jim.ConnectedSystems.ImportConnectedSystemHierarchyAsync(systemId, initiator);

            Assert.That(result.RemovedPartitions.Select(p => p.ExternalId), Is.EqualTo(new[] { "dc=glitterband,dc=local" }), "precondition");
        }

        await using var verify = NewContext();
        var objects = await verify.ConnectedSystemObjects.ToDictionaryAsync(o => o.Id, o => o.PartitionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemPartitions.Select(p => p.Id).ToListAsync(), Is.EqualTo(new[] { yellowstoneId }),
                "the refresh removes the Partition the directory no longer holds, and keeps the other");
            Assert.That(objects.Keys, Is.EquivalentTo(glitterbandObjectIds.Append(yellowstoneObjectId)),
                "removing a Partition deletes no Connected System Object; that is deletion detection's decision");
            Assert.That(glitterbandObjectIds.Select(id => objects[id]), Is.All.Null,
                $"the objects in the removed Partition (id {glitterbandId}) are left in no Partition");
            Assert.That(objects[yellowstoneObjectId], Is.EqualTo(yellowstoneId), "an object in a kept Partition keeps it");
        }
    }

    /// <summary>
    /// A Partition the directory no longer holds is removed by a refresh started from the portal, along with its
    /// Containers (#2743).
    /// </summary>
    /// <remarks>
    /// The portal's call shape: the Connected System page loads its Connected System on one context and lets it go,
    /// and the Scope tab runs the refresh on a context of its own, both configured as JIM.Web runs them. The refresh
    /// used to take the page's detached graph and merge into it, and removing a Partition from a graph nothing tracks
    /// marks nothing deleted: the save attached only what was still reachable, so the Partition, its Containers and
    /// every Connected System Object stamped with it survived, while the refresh reported it removed. It now takes
    /// the Connected System's id and loads its own tracked copy, so there is no graph to hand over.
    ///
    /// The Containers are asserted as rows, not as a Partition's children. Even on a tracked graph, EF Core only
    /// clears a removed Partition's Containers' link to it, because that link is optional, which leaves the rows
    /// behind belonging to nothing; the refresh deletes them explicitly.
    /// </remarks>
    [Test]
    public async Task RefreshHierarchy_FromThePortal_PartitionNoLongerInTheDirectory_RemovesItAndItsContainersAsync()
    {
        var (systemId, initiatorId) = await SeedTwoPartitionDirectoryAsync();

        // The directory no longer has Glitterband; Yellowstone is unchanged.
        var connector = new StubPartitionsConnector([YellowstoneAsDiscovered(includeGroups: true)]);
        var result = await RefreshAsThePortalDoesAsync(systemId, initiatorId, connector);
        Assert.That(result.RemovedPartitions.Select(p => p.ExternalId), Is.EqualTo(new[] { "dc=glitterband,dc=local" }), "precondition");

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemPartitions.Select(p => p.ExternalId).ToListAsync(), Is.EqualTo(new[] { "dc=yellowstone,dc=local" }),
                "the Partition the directory no longer holds must be removed, as the refresh reported");
            Assert.That(await verify.ConnectedSystemContainers.Select(c => c.Name).ToListAsync(), Is.EquivalentTo(new[] { "People", "Groups", "Admins" }),
                "the removed Partition's Containers go with it, and the kept Partition's stay");
        }
    }

    /// <summary>
    /// A Container the directory no longer holds is removed by a refresh started from the portal, along with the
    /// Containers beneath it, while its Partition stays (#2743).
    /// </summary>
    /// <remarks>
    /// The same detached-graph fault as the Partition test above, one level down: removing a Container from a
    /// collection nothing tracks deletes nothing. A tracked graph alone is not enough here either, for the reason
    /// given above: a Container's links to its Partition and to its parent are both optional.
    /// </remarks>
    [Test]
    public async Task RefreshHierarchy_FromThePortal_ContainerNoLongerInTheDirectory_RemovesItAndItsDescendantsAsync()
    {
        var (systemId, initiatorId) = await SeedTwoPartitionDirectoryAsync();

        // The directory no longer has Groups (nor, with it, Admins); both Partitions are still there.
        var connector = new StubPartitionsConnector([YellowstoneAsDiscovered(includeGroups: false), GlitterbandAsDiscovered()]);
        var result = await RefreshAsThePortalDoesAsync(systemId, initiatorId, connector);
        Assert.That(result.RemovedContainers.Select(c => c.Name), Is.EquivalentTo(new[] { "Groups", "Admins" }), "precondition");

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemContainers.Select(c => c.Name).ToListAsync(), Is.EquivalentTo(new[] { "People", "Staff" }),
                "the Container the directory no longer holds, and the one beneath it, must be removed");
            Assert.That(await verify.ConnectedSystemPartitions.CountAsync(), Is.EqualTo(2), "both Partitions are still in the directory");
        }
    }

    /// <summary>
    /// A Container moved out of one the directory no longer holds is kept, in its new home and still selected, while
    /// the one it left is removed (#2743).
    /// </summary>
    /// <remarks>
    /// Guards the refresh's explicit deletions. Deleting a Container takes everything still beneath it, so the
    /// Container moved out of it must no longer count as beneath it, and must not be counted among the removed.
    /// </remarks>
    [Test]
    public async Task RefreshHierarchy_ContainerMovedOutOfOneNoLongerInTheDirectory_KeepsTheMovedContainerAsync()
    {
        var (systemId, initiatorId) = await SeedTwoPartitionDirectoryAsync();

        // Admins has moved from Groups to People, and Groups has gone.
        var yellowstone = YellowstoneAsDiscovered(includeGroups: false);
        yellowstone.Containers.Single(c => c.Name == "People").ChildContainers
            .Add(new ConnectorContainer("ou=Admins,ou=People,dc=yellowstone,dc=local", "Admins") { StableId = AdminsStableId });
        var connector = new StubPartitionsConnector([yellowstone, GlitterbandAsDiscovered()]);

        var result = await RefreshAsThePortalDoesAsync(systemId, initiatorId, connector);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MovedContainers.Select(c => c.Name), Is.EqualTo(new[] { "Admins" }), "precondition");
            Assert.That(result.RemovedContainers.Select(c => c.Name), Is.EqualTo(new[] { "Groups" }), "precondition");
        }

        await using var verify = NewContext();
        var containers = await verify.ConnectedSystemContainers.ToListAsync();
        var people = containers.Single(c => c.Name == "People");
        var admins = containers.SingleOrDefault(c => c.Name == "Admins");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(containers.Select(c => c.Name), Is.EquivalentTo(new[] { "People", "Admins", "Staff" }),
                "the Container the directory no longer holds is removed, and the one moved out of it is kept");
            Assert.That(admins?.ParentContainerId, Is.EqualTo(people.Id), "the moved Container sits under its new parent");
            Assert.That(admins?.Selected, Is.True, "and keeps its selection");
        }
    }

    /// <summary>
    /// A refresh whose save fails finishes its Activity as a failure carrying the reason, and the save's own
    /// exception reaches the caller (#2742).
    /// </summary>
    /// <remarks>
    /// The save is made to fail for a real reason: two refreshes of one Connected System at once. While this one reads
    /// the directory, another that started a moment later saves first, having found Groups gone and removed it. This
    /// one still holds Groups, so its update of that row matches nothing and the save fails.
    ///
    /// The failure handler used to record the failure on the same context whose save had just failed, which still
    /// held the changes that broke it, so its own save failed the same way: the Activity stayed In Progress with no
    /// message, and the caller was handed the handler's exception rather than the refresh's.
    /// </remarks>
    [Test]
    public async Task RefreshHierarchy_SaveFails_FailsTheActivityAndRethrowsTheSaveFailureAsync()
    {
        var (systemId, initiatorId) = await SeedTwoPartitionDirectoryAsync();

        // This refresh's directory read still has Groups, and adds Contractors beneath People. The other refresh
        // removes Groups (and, by cascade, Admins) while this one is reading.
        var yellowstone = YellowstoneAsDiscovered(includeGroups: true);
        yellowstone.Containers.Single(c => c.Name == "People").ChildContainers
            .Add(new ConnectorContainer("ou=Contractors,ou=People,dc=yellowstone,dc=local", "Contractors"));
        var connector = new StubPartitionsConnector([yellowstone, GlitterbandAsDiscovered()], whileReadingAsync: async () =>
        {
            await using var otherRefresh = NewContext();
            await otherRefresh.ConnectedSystemContainers.Where(c => c.Name == "Groups").ExecuteDeleteAsync();
        });

        DbUpdateException? thrown;
        await using (var ctx = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(ctx), connectorFactory: new StubConnectorFactory(connector));
            var initiator = await ctx.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);

            thrown = Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () =>
                await jim.ConnectedSystems.ImportConnectedSystemHierarchyAsync(systemId, initiator));
        }

        await using var verify = NewContext();
        var activity = await verify.Activities.SingleAsync(a => a.TargetOperationType == ActivityTargetOperationType.ImportHierarchy);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown?.StackTrace, Does.Not.Contain(nameof(ActivityServer.FailActivityWithErrorAsync)),
                "the caller must be handed the refresh's own failure, not one raised while recording it");
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError),
                "a refresh that failed must not leave its Activity In Progress for ever");
            Assert.That(activity.ErrorMessage, Is.Not.Null.And.Not.Empty, "the Activity must say why it failed");
            Assert.That(await verify.ConnectedSystemContainers.Select(c => c.Name).ToListAsync(), Is.EquivalentTo(new[] { "People", "Staff" }),
                "nothing of the failed refresh is saved: Contractors is not added, and only the other refresh's removal stands");
        }
    }

    /// <summary>
    /// Runs a refresh the way the portal's Scope tab does: the Connected System page loads its Connected System on one
    /// context and lets it go, and the tab refreshes on another, naming the page's Connected System by its id. Both
    /// contexts are configured as JIM.Web runs them.
    /// </summary>
    private async Task<HierarchyRefreshResult> RefreshAsThePortalDoesAsync(int systemId, Guid initiatorId, StubPartitionsConnector connector)
    {
        ConnectedSystem pageConnectedSystem;
        await using (var pageContext = NewContext())
        {
            var page = new JimApplication(new PostgresDataRepository(pageContext));
            pageConnectedSystem = (await page.ConnectedSystems.GetConnectedSystemAsync(systemId, withChangeTracking: true))!;
        }

        await using var tabContext = NewContext();
        var tab = new JimApplication(new PostgresDataRepository(tabContext), connectorFactory: new StubConnectorFactory(connector));
        var initiator = await tabContext.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);
        return await tab.ConnectedSystems.ImportConnectedSystemHierarchyAsync(pageConnectedSystem.Id, initiator);
    }

    /// <summary>
    /// The directory's own identifier for Admins, which survives a move.
    /// </summary>
    private const string AdminsStableId = "admins-stable-id";

    /// <summary>
    /// Seeds a directory holding two selected partitions: Yellowstone, with People, and Groups holding Admins; and
    /// Glitterband, with Staff. Returns the Connected System's id and the id of a security principal to attribute a
    /// refresh to.
    /// </summary>
    private async Task<(int SystemId, Guid InitiatorId)> SeedTwoPartitionDirectoryAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test LDAP Connector", SupportsPartitions = true, SupportsPartitionContainers = true };
        var setting = new ConnectorDefinitionSetting { Name = "Host", Type = ConnectedSystemSettingType.String };
        connectorDefinition.Settings.Add(setting);
        var system = new ConnectedSystem
        {
            Name = "Test Directory",
            ConnectorDefinition = connectorDefinition,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "directory.example.local" }]
        };

        var yellowstone = new ConnectedSystemPartition
        {
            ConnectedSystem = system, ExternalId = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Selected = true, Containers = []
        };
        yellowstone.Containers.Add(new ConnectedSystemContainer
        {
            Partition = yellowstone, ExternalId = "ou=People,dc=yellowstone,dc=local", Name = "People", Selected = true
        });
        var groups = new ConnectedSystemContainer
        {
            Partition = yellowstone, ExternalId = "ou=Groups,dc=yellowstone,dc=local", Name = "Groups", Selected = true
        };
        // Admins carries the directory's own identifier, so a refresh can recognise it wherever it moves.
        groups.AddChildContainer(new ConnectedSystemContainer
        {
            ExternalId = "ou=Admins,ou=Groups,dc=yellowstone,dc=local", Name = "Admins", StableId = AdminsStableId, Selected = true
        });
        yellowstone.Containers.Add(groups);

        var glitterband = new ConnectedSystemPartition
        {
            ConnectedSystem = system, ExternalId = "dc=glitterband,dc=local", Name = "dc=glitterband,dc=local", Selected = true, Containers = []
        };
        glitterband.Containers.Add(new ConnectedSystemContainer
        {
            Partition = glitterband, ExternalId = "ou=Staff,dc=glitterband,dc=local", Name = "Staff", Selected = true
        });

        // A hierarchy refresh is a configuration change, recorded against a security principal or not at all.
        var mvUserType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var initiator = new MetaverseObject { Type = mvUserType, CachedDisplayName = "Test Administrator" };

        seed.AddRange(connectorDefinition, system, yellowstone, glitterband, mvUserType, initiator);
        await seed.SaveChangesAsync();

        return (system.Id, initiator.Id);
    }

    /// <summary>
    /// Yellowstone as the directory reports it: People always, and Groups with Admins beneath it when asked for.
    /// </summary>
    private static ConnectorPartition YellowstoneAsDiscovered(bool includeGroups)
    {
        var containers = new List<ConnectorContainer> { new("ou=People,dc=yellowstone,dc=local", "People") };
        if (includeGroups)
        {
            var groups = new ConnectorContainer("ou=Groups,dc=yellowstone,dc=local", "Groups");
            groups.ChildContainers.Add(new ConnectorContainer("ou=Admins,ou=Groups,dc=yellowstone,dc=local", "Admins"));
            containers.Add(groups);
        }

        return new ConnectorPartition { Id = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Containers = containers };
    }

    /// <summary>
    /// Glitterband as the directory reports it: Staff.
    /// </summary>
    private static ConnectorPartition GlitterbandAsDiscovered() => new()
    {
        Id = "dc=glitterband,dc=local",
        Name = "dc=glitterband,dc=local",
        Containers = [new ConnectorContainer("ou=Staff,dc=glitterband,dc=local", "Staff")]
    };

    /// <summary>
    /// A Connector whose hierarchy is whatever the test says the directory holds, and which can run something else
    /// while it is being read, to stand in for whatever else happens meanwhile.
    /// </summary>
    private sealed class StubPartitionsConnector(List<ConnectorPartition> partitions, Func<Task>? whileReadingAsync = null) : IConnector, IConnectorPartitions
    {
        public string Name => "Stub Partitions Connector";
        public string? Description => null;
        public string? Url => null;

        public async Task<List<ConnectorPartition>> GetPartitionsAsync(List<ConnectedSystemSettingValue> settings, ILogger logger)
        {
            if (whileReadingAsync != null)
                await whileReadingAsync();

            return partitions;
        }
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}
