// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for <see cref="ConnectedSystemRepository.GetConnectedSystemCoreAsync"/> and the
/// flat container tree build helper introduced in issue #494.
/// </summary>
[TestFixture]
public class ConnectedSystemRepositoryCoreTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private PostgresDataRepository _repository = null!;

    private List<ConnectedSystem> _connectedSystemsData = null!;
    private List<ConnectedSystemRunProfile> _runProfilesData = null!;
    private List<ConnectedSystemObjectType> _objectTypesData = null!;
    private List<ConnectedSystemPartition> _partitionsData = null!;
    private List<ConnectedSystemContainer> _containersData = null!;
    private List<ObjectMatchingRule> _matchingRulesData = null!;

    private Mock<DbSet<ConnectedSystem>> _mockDbSetConnectedSystems = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> _mockDbSetRunProfiles = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> _mockDbSetObjectTypes = null!;
    private Mock<DbSet<ConnectedSystemPartition>> _mockDbSetPartitions = null!;
    private Mock<DbSet<ConnectedSystemContainer>> _mockDbSetContainers = null!;
    private Mock<DbSet<ObjectMatchingRule>> _mockDbSetMatchingRules = null!;

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _connectedSystemsData = new List<ConnectedSystem>();
        _runProfilesData = new List<ConnectedSystemRunProfile>();
        _objectTypesData = new List<ConnectedSystemObjectType>();
        _partitionsData = new List<ConnectedSystemPartition>();
        _containersData = new List<ConnectedSystemContainer>();
        _matchingRulesData = new List<ObjectMatchingRule>();
    }

    private void BuildMocks()
    {
        _mockDbSetConnectedSystems = _connectedSystemsData.BuildMockDbSet();
        _mockDbSetRunProfiles = _runProfilesData.BuildMockDbSet();
        _mockDbSetObjectTypes = _objectTypesData.BuildMockDbSet();
        _mockDbSetPartitions = _partitionsData.BuildMockDbSet();
        _mockDbSetContainers = _containersData.BuildMockDbSet();
        _mockDbSetMatchingRules = _matchingRulesData.BuildMockDbSet();

        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.ConnectedSystems).Returns(_mockDbSetConnectedSystems.Object);
        _mockDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(_mockDbSetRunProfiles.Object);
        _mockDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(_mockDbSetObjectTypes.Object);
        _mockDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(_mockDbSetPartitions.Object);
        _mockDbContext.Setup(m => m.ConnectedSystemContainers).Returns(_mockDbSetContainers.Object);
        _mockDbContext.Setup(m => m.ObjectMatchingRules).Returns(_mockDbSetMatchingRules.Object);

        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    private ConnectedSystem CreateConnectedSystem(int id = 1, string name = "Test System")
    {
        var connectorDefinition = new ConnectorDefinition
        {
            Id = 10,
            Name = "Test Connector"
        };
        return new ConnectedSystem
        {
            Id = id,
            Name = name,
            ConnectorDefinitionId = connectorDefinition.Id,
            ConnectorDefinition = connectorDefinition,
            SettingValues = new List<ConnectedSystemSettingValue>()
        };
    }

    #region GetConnectedSystemCoreAsync

    [Test]
    public async Task GetConnectedSystemCoreAsync_WithValidId_ReturnsSystemAsync()
    {
        // Arrange
        _connectedSystemsData.Add(CreateConnectedSystem(id: 1, name: "CS-One"));
        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemCoreAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(1));
        Assert.That(result.Name, Is.EqualTo("CS-One"));
    }

    [Test]
    public async Task GetConnectedSystemCoreAsync_WithInvalidId_ReturnsNullAsync()
    {
        // Arrange
        _connectedSystemsData.Add(CreateConnectedSystem(id: 1));
        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemCoreAsync(999);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemCoreAsync_IncludesConnectorDefinitionAsync()
    {
        // Arrange
        _connectedSystemsData.Add(CreateConnectedSystem(id: 1));
        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemCoreAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ConnectorDefinition, Is.Not.Null);
        Assert.That(result.ConnectorDefinition.Name, Is.EqualTo("Test Connector"));
    }

    [Test]
    public async Task GetConnectedSystemCoreAsync_DoesNotLoadObjectTypesAsync()
    {
        // Arrange: an object type exists in the data source, but Core should not populate it.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);
        _objectTypesData.Add(new ConnectedSystemObjectType
        {
            Id = 100,
            ConnectedSystemId = 1,
            Name = "User"
        });
        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemCoreAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        // Core is a lightweight variant: ObjectTypes should not be populated (left at the model's
        // default empty list since Core never queries the ObjectTypes DbSet).
        Assert.That(result!.ObjectTypes, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemCoreAsync_DoesNotLoadPartitionsAsync()
    {
        // Arrange
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);
        _partitionsData.Add(new ConnectedSystemPartition
        {
            Id = 200,
            ConnectedSystem = cs,
            ExternalId = "DC=example,DC=com",
            Name = "Root"
        });
        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemCoreAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        // Core leaves Partitions at the model default (null) — it never queries the partition DbSet.
        Assert.That(result!.Partitions, Is.Null);
    }

    #endregion

    #region GetConnectedSystemAsync — Object Matching Rule wire-up

    [Test]
    public async Task GetConnectedSystemAsync_WiresMatchingRulesOntoOwningObjectTypesAsync()
    {
        // Arrange: two object types, one with a matching rule, the other without.
        // Previously this graph was loaded via four repeated .Include(ot => ot.ObjectMatchingRules)
        // branches inside an .AsSplitQuery() — four separate SQL queries per call. The refactor
        // collapses that to a single query keyed by object type ids, and wires the rules up in
        // memory. This test guards against a regression in the wire-up.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);

        var userType = new ConnectedSystemObjectType
        {
            Id = 10,
            ConnectedSystemId = 1,
            Name = "User",
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };
        var groupType = new ConnectedSystemObjectType
        {
            Id = 11,
            ConnectedSystemId = 1,
            Name = "Group",
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };
        _objectTypesData.Add(userType);
        _objectTypesData.Add(groupType);

        var rule = new ObjectMatchingRule
        {
            Id = 100,
            Order = 0,
            ConnectedSystemObjectTypeId = 10,
            Sources = new List<ObjectMatchingRuleSource>()
        };
        _matchingRulesData.Add(rule);

        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert: the rule is attached to the User object type, and the Group type has no rules.
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ObjectTypes, Is.Not.Null);

        var loadedUser = result.ObjectTypes!.Single(t => t.Id == 10);
        var loadedGroup = result.ObjectTypes!.Single(t => t.Id == 11);

        Assert.That(loadedUser.ObjectMatchingRules, Has.Count.EqualTo(1));
        Assert.That(loadedUser.ObjectMatchingRules[0].Id, Is.EqualTo(100));
        Assert.That(loadedGroup.ObjectMatchingRules, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemAsync_WithNoObjectTypes_SkipsMatchingRulesQueryAsync()
    {
        // Arrange: a system with no object types at all. The matching-rules query must be
        // skipped entirely so we do not issue a SELECT ... WHERE id IN () against an empty set.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);
        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ObjectTypes, Is.Empty);
    }

    #endregion

    #region BuildContainerTree helper

    [Test]
    public void BuildContainerTree_WithEmptyList_YieldsNoRoots()
    {
        // Arrange
        var flat = new List<ConnectedSystemContainer>();

        // Act
        var roots = ConnectedSystemRepository.BuildContainerTree(flat);

        // Assert
        Assert.That(roots, Is.Empty);
    }

    [Test]
    public void BuildContainerTree_WithSingleRoot_ReturnsRoot()
    {
        // Arrange
        var flat = new List<ConnectedSystemContainer>
        {
            new() { Id = 1, Name = "Root", ExternalId = "OU=Root" }
        };

        // Act
        var roots = ConnectedSystemRepository.BuildContainerTree(flat);

        // Assert
        Assert.That(roots, Has.Count.EqualTo(1));
        Assert.That(roots[0].Id, Is.EqualTo(1));
        Assert.That(roots[0].ChildContainers, Is.Empty);
    }

    [Test]
    public void BuildContainerTree_WithDeepHierarchy_BuildsTreeBeyond11Levels()
    {
        // Arrange: construct a 15-level deep hierarchy (1 -> 2 -> 3 -> ... -> 15).
        // This proves the flat-load approach handles arbitrary depth, unlike the
        // previous 11-level hard-coded Include chain.
        var flat = new List<ConnectedSystemContainer>();
        ConnectedSystemContainer? previous = null;
        for (var i = 1; i <= 15; i++)
        {
            var container = new ConnectedSystemContainer
            {
                Id = i,
                Name = $"Level {i}",
                ExternalId = $"OU=Level{i}",
                ParentContainer = previous
            };
            flat.Add(container);
            previous = container;
        }

        // Act
        var roots = ConnectedSystemRepository.BuildContainerTree(flat);

        // Assert: only one root, and we can walk all 15 levels via ChildContainers.
        Assert.That(roots, Has.Count.EqualTo(1));
        var depth = 0;
        var cursor = roots[0];
        while (cursor != null)
        {
            depth++;
            cursor = cursor.ChildContainers.FirstOrDefault();
        }
        Assert.That(depth, Is.EqualTo(15));
    }

    [Test]
    public void BuildContainerTree_WithMultipleRoots_ReturnsAllRoots()
    {
        // Arrange
        var flat = new List<ConnectedSystemContainer>
        {
            new() { Id = 1, Name = "Root A", ExternalId = "OU=A" },
            new() { Id = 2, Name = "Root B", ExternalId = "OU=B" },
            new() { Id = 3, Name = "Child of A", ExternalId = "OU=A.Child", ParentContainer = null }
        };
        // Wire the child: has a parent reference (by object), but flat list lacks FK.
        // BuildContainerTree must rely on ParentContainer reference equality.
        flat[2].ParentContainer = flat[0];

        // Act
        var roots = ConnectedSystemRepository.BuildContainerTree(flat);

        // Assert: two roots, Root A has one child.
        Assert.That(roots, Has.Count.EqualTo(2));
        var rootA = roots.Single(r => r.Id == 1);
        var rootB = roots.Single(r => r.Id == 2);
        Assert.That(rootA.ChildContainers, Has.Count.EqualTo(1));
        Assert.That(rootA.ChildContainers.First().Id, Is.EqualTo(3));
        Assert.That(rootB.ChildContainers, Is.Empty);
    }

    [Test]
    public void BuildContainerTree_WireUpsChildrenViaParentId_WhenReferenceNotSet()
    {
        // Arrange: flat list where only PK/FK are present (no object reference).
        // Simulates the exact state after EF Core materialises rows without
        // navigation fixup — which is what happens when ChildContainers is not
        // Included and ParentContainer comes back null.
        //
        // The helper accepts an id-lookup strategy so it can still rebuild the tree.
        var root = new ConnectedSystemContainer { Id = 1, Name = "Root", ExternalId = "OU=R" };
        var child = new ConnectedSystemContainer { Id = 2, Name = "Child", ExternalId = "OU=C" };
        var flat = new List<ConnectedSystemContainer> { root, child };
        var parentIdByChildId = new Dictionary<int, int?> { { 1, null }, { 2, 1 } };

        // Act
        var roots = ConnectedSystemRepository.BuildContainerTree(flat, parentIdByChildId);

        // Assert
        Assert.That(roots, Has.Count.EqualTo(1));
        Assert.That(roots[0].Id, Is.EqualTo(1));
        Assert.That(roots[0].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(roots[0].ChildContainers.First().Id, Is.EqualTo(2));
        Assert.That(roots[0].ChildContainers.First().ParentContainer, Is.SameAs(roots[0]));
    }

    #endregion

    #region GetConnectedSystemAsync — nested container loading (issue #586)

    [Test]
    public async Task GetConnectedSystemAsync_LoadsNestedContainersBelowRootsAsync()
    {
        // Arrange: a Connected System with one partition, one root container ("Corp"), and three
        // nested descendants ("Users", "Groups", "Entitlements") whose PartitionId is null and
        // whose ParentContainerId points at the root. This mirrors the shape that Samba AD / AD
        // LDAP imports produce: only top-level containers carry a PartitionId FK; descendants are
        // linked via ParentContainerId alone.
        //
        // Before the #586 fix, GetConnectedSystemAsync's flat-load filter matched only rows with
        // PartitionId set, so the three nested rows were silently excluded. Callers received roots
        // with empty ChildContainers and had no way to reach nested containers through the API.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);

        var partition = new ConnectedSystemPartition
        {
            Id = 100,
            ConnectedSystem = cs,
            Name = "DC=resurgam,DC=local",
            ExternalId = "DC=resurgam,DC=local",
            Selected = true,
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        _partitionsData.Add(partition);

        var corp = new ConnectedSystemContainer
        {
            Id = 200,
            Name = "Corp",
            ExternalId = "OU=Corp,DC=resurgam,DC=local",
            PartitionId = partition.Id
        };
        var users = new ConnectedSystemContainer
        {
            Id = 201,
            Name = "Users",
            ExternalId = "OU=Users,OU=Corp,DC=resurgam,DC=local",
            ParentContainerId = corp.Id
        };
        var groups = new ConnectedSystemContainer
        {
            Id = 202,
            Name = "Groups",
            ExternalId = "OU=Groups,OU=Corp,DC=resurgam,DC=local",
            ParentContainerId = corp.Id
        };
        var entitlements = new ConnectedSystemContainer
        {
            Id = 203,
            Name = "Entitlements",
            ExternalId = "OU=Entitlements,OU=Corp,DC=resurgam,DC=local",
            ParentContainerId = corp.Id
        };
        _containersData.Add(corp);
        _containersData.Add(users);
        _containersData.Add(groups);
        _containersData.Add(entitlements);

        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert: Corp is attached to the partition, and its three nested children are reachable
        // through ChildContainers.
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Partitions, Is.Not.Null);

        var loadedPartition = result.Partitions!.Single(p => p.Id == partition.Id);
        Assert.That(loadedPartition.Containers, Has.Count.EqualTo(1));

        var loadedCorp = loadedPartition.Containers!.Single();
        Assert.That(loadedCorp.Id, Is.EqualTo(corp.Id));
        Assert.That(loadedCorp.ChildContainers, Has.Count.EqualTo(3), "Nested descendants must be loaded and stitched under their root");

        var childIds = loadedCorp.ChildContainers.Select(c => c.Id).OrderBy(id => id).ToList();
        Assert.That(childIds, Is.EquivalentTo(new[] { users.Id, groups.Id, entitlements.Id }));
    }

    [Test]
    public async Task GetConnectedSystemAsync_LoadsMultiLevelNestedContainersAsync()
    {
        // Arrange: three-level hierarchy — Root -> Mid -> Leaf — to prove the BFS walk continues
        // past the first layer of descendants.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);

        var partition = new ConnectedSystemPartition
        {
            Id = 100,
            ConnectedSystem = cs,
            Name = "DC=example,DC=local",
            ExternalId = "DC=example,DC=local",
            Selected = true,
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        _partitionsData.Add(partition);

        var root = new ConnectedSystemContainer { Id = 200, Name = "Root", ExternalId = "OU=Root", PartitionId = partition.Id };
        var mid = new ConnectedSystemContainer { Id = 201, Name = "Mid", ExternalId = "OU=Mid,OU=Root", ParentContainerId = root.Id };
        var leaf = new ConnectedSystemContainer { Id = 202, Name = "Leaf", ExternalId = "OU=Leaf,OU=Mid,OU=Root", ParentContainerId = mid.Id };
        _containersData.AddRange(new[] { root, mid, leaf });

        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        var loadedPartition = result!.Partitions!.Single(p => p.Id == partition.Id);
        var loadedRoot = loadedPartition.Containers!.Single();
        Assert.That(loadedRoot.Id, Is.EqualTo(root.Id));
        Assert.That(loadedRoot.ChildContainers, Has.Count.EqualTo(1));

        var loadedMid = loadedRoot.ChildContainers.Single();
        Assert.That(loadedMid.Id, Is.EqualTo(mid.Id));
        Assert.That(loadedMid.ChildContainers, Has.Count.EqualTo(1));
        Assert.That(loadedMid.ChildContainers.Single().Id, Is.EqualTo(leaf.Id));
    }

    [Test]
    public async Task GetConnectedSystemAsync_ExcludesContainersFromUnrelatedPartitionsAsync()
    {
        // Arrange: two Connected Systems (A and B), each with one partition and a Corp/Users tree.
        // When loading system A, we must see A's tree only — B's containers must not leak in.
        var systemA = CreateConnectedSystem(id: 1, name: "A");
        var systemB = CreateConnectedSystem(id: 2, name: "B");
        _connectedSystemsData.Add(systemA);
        _connectedSystemsData.Add(systemB);

        var partitionA = new ConnectedSystemPartition
        {
            Id = 100,
            ConnectedSystem = systemA,
            Name = "DC=a,DC=local",
            ExternalId = "DC=a,DC=local",
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        var partitionB = new ConnectedSystemPartition
        {
            Id = 101,
            ConnectedSystem = systemB,
            Name = "DC=b,DC=local",
            ExternalId = "DC=b,DC=local",
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        _partitionsData.Add(partitionA);
        _partitionsData.Add(partitionB);

        var corpA = new ConnectedSystemContainer { Id = 200, Name = "CorpA", ExternalId = "OU=Corp,DC=a", PartitionId = partitionA.Id };
        var usersA = new ConnectedSystemContainer { Id = 201, Name = "UsersA", ExternalId = "OU=Users,OU=Corp,DC=a", ParentContainerId = corpA.Id };
        var corpB = new ConnectedSystemContainer { Id = 300, Name = "CorpB", ExternalId = "OU=Corp,DC=b", PartitionId = partitionB.Id };
        var usersB = new ConnectedSystemContainer { Id = 301, Name = "UsersB", ExternalId = "OU=Users,OU=Corp,DC=b", ParentContainerId = corpB.Id };
        _containersData.AddRange(new[] { corpA, usersA, corpB, usersB });

        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        var loadedPartitions = result!.Partitions!.ToList();
        Assert.That(loadedPartitions, Has.Count.EqualTo(1));

        var loadedPartition = loadedPartitions[0];
        Assert.That(loadedPartition.Id, Is.EqualTo(partitionA.Id));

        var allContainerIds = CollectAllContainerIds(loadedPartition.Containers!);
        Assert.That(allContainerIds, Is.EquivalentTo(new[] { corpA.Id, usersA.Id }));
        Assert.That(allContainerIds, Does.Not.Contain(corpB.Id));
        Assert.That(allContainerIds, Does.Not.Contain(usersB.Id));
    }

    [Test]
    public async Task GetConnectedSystemPartitionsAsync_LoadsNestedContainersBelowRootsAsync()
    {
        // Arrange: same shape as the GetConnectedSystemAsync test — one partition, one root Corp, three
        // nested descendants. This endpoint powers the partitions-list API that scenario scripts use to
        // select containers for import, so missing descendants here caused Scenario 8's silent failure
        // even after GetConnectedSystemAsync was fixed (issue #586).
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);

        var partition = new ConnectedSystemPartition
        {
            Id = 100,
            ConnectedSystem = cs,
            Name = "DC=resurgam,DC=local",
            ExternalId = "DC=resurgam,DC=local",
            Selected = true,
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        _partitionsData.Add(partition);

        var corp = new ConnectedSystemContainer
        {
            Id = 200,
            Name = "Corp",
            ExternalId = "OU=Corp,DC=resurgam,DC=local",
            PartitionId = partition.Id
        };
        var users = new ConnectedSystemContainer
        {
            Id = 201,
            Name = "Users",
            ExternalId = "OU=Users,OU=Corp,DC=resurgam,DC=local",
            ParentContainerId = corp.Id
        };
        var groups = new ConnectedSystemContainer
        {
            Id = 202,
            Name = "Groups",
            ExternalId = "OU=Groups,OU=Corp,DC=resurgam,DC=local",
            ParentContainerId = corp.Id
        };
        _containersData.AddRange(new[] { corp, users, groups });

        BuildMocks();

        // Act
        var partitions = await _repository.ConnectedSystems.GetConnectedSystemPartitionsAsync(cs);

        // Assert
        Assert.That(partitions, Has.Count.EqualTo(1));
        var loadedPartition = partitions.Single();
        Assert.That(loadedPartition.Containers, Has.Count.EqualTo(1));

        var loadedCorp = loadedPartition.Containers!.Single();
        Assert.That(loadedCorp.Id, Is.EqualTo(corp.Id));
        Assert.That(loadedCorp.ChildContainers, Has.Count.EqualTo(2));

        var childIds = loadedCorp.ChildContainers.Select(c => c.Id).OrderBy(id => id).ToList();
        Assert.That(childIds, Is.EquivalentTo(new[] { users.Id, groups.Id }));
    }

    [Test]
    public async Task GetConnectedSystemContainerAsync_WalksParentChainForNestedContainerAsync()
    {
        // Arrange: a nested container (Users under Corp under partition P) where the leaf has
        // ParentContainerId set but no eager navigation. The controller's belongs-to check walks up
        // ParentContainer to reach the owning ConnectedSystem via either Partition or ConnectedSystem;
        // if the chain isn't hydrated, PUTs to nested containers 404. See issue #586.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);

        var partition = new ConnectedSystemPartition
        {
            Id = 100,
            ConnectedSystem = cs,
            Name = "DC=example,DC=local",
            ExternalId = "DC=example,DC=local",
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        _partitionsData.Add(partition);

        var corp = new ConnectedSystemContainer
        {
            Id = 200,
            Name = "Corp",
            ExternalId = "OU=Corp,DC=example,DC=local",
            PartitionId = partition.Id,
            Partition = partition
        };
        var users = new ConnectedSystemContainer
        {
            Id = 201,
            Name = "Users",
            ExternalId = "OU=Users,OU=Corp,DC=example,DC=local",
            ParentContainerId = corp.Id
        };
        _containersData.AddRange(new[] { corp, users });

        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemContainerAsync(users.Id);

        // Assert: the leaf is returned with its ParentContainer chain populated up to the root,
        // and the root's Partition -> ConnectedSystem is reachable so belongs-to checks pass.
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(users.Id));
        Assert.That(result.ParentContainer, Is.Not.Null);
        Assert.That(result.ParentContainer!.Id, Is.EqualTo(corp.Id));
        Assert.That(result.ParentContainer.Partition, Is.Not.Null);
        Assert.That(result.ParentContainer.Partition!.ConnectedSystem, Is.Not.Null);
        Assert.That(result.ParentContainer.Partition.ConnectedSystem!.Id, Is.EqualTo(cs.Id));
    }

    [Test]
    public async Task GetConnectedSystemPartitionAsync_LoadsNestedContainersBelowRootAsync()
    {
        // Arrange: same shape but via the single-partition endpoint used by the partition-update API.
        var cs = CreateConnectedSystem(id: 1);
        _connectedSystemsData.Add(cs);

        var partition = new ConnectedSystemPartition
        {
            Id = 100,
            ConnectedSystem = cs,
            Name = "DC=resurgam,DC=local",
            ExternalId = "DC=resurgam,DC=local",
            Containers = new HashSet<ConnectedSystemContainer>()
        };
        _partitionsData.Add(partition);

        var corp = new ConnectedSystemContainer
        {
            Id = 200,
            Name = "Corp",
            ExternalId = "OU=Corp,DC=resurgam,DC=local",
            PartitionId = partition.Id
        };
        var users = new ConnectedSystemContainer
        {
            Id = 201,
            Name = "Users",
            ExternalId = "OU=Users,OU=Corp,DC=resurgam,DC=local",
            ParentContainerId = corp.Id
        };
        _containersData.AddRange(new[] { corp, users });

        BuildMocks();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemPartitionAsync(partition.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Containers, Has.Count.EqualTo(1));

        var loadedCorp = result.Containers!.Single();
        Assert.That(loadedCorp.ChildContainers, Has.Count.EqualTo(1));
        Assert.That(loadedCorp.ChildContainers.Single().Id, Is.EqualTo(users.Id));
    }

    private static List<int> CollectAllContainerIds(IEnumerable<ConnectedSystemContainer> containers)
    {
        var ids = new List<int>();
        foreach (var container in containers)
        {
            ids.Add(container.Id);
            ids.AddRange(CollectAllContainerIds(container.ChildContainers));
        }
        return ids;
    }

    #endregion
}
