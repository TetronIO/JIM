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
}
