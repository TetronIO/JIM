using JIM.Models.Staging;

namespace JIM.Utilities.Tests;

public class ConnectedSystemUtilitiesTests
{
    private static int _nextId = 1;

    [SetUp]
    public void Setup()
    {
        _nextId = 1;
    }

    #region GetAllSelectedContainers Tests

    [Test]
    public void GetAllSelectedContainers_WithNullPartition_ThrowsArgumentNullException()
    {
        // Arrange
        ConnectedSystemPartition? partition = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            ConnectedSystemUtilities.GetAllSelectedContainers(partition!));
    }

    [Test]
    public void GetAllSelectedContainers_WithNullContainers_ThrowsArgumentException()
    {
        // Arrange
        var partition = new ConnectedSystemPartition { Containers = null };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ConnectedSystemUtilities.GetAllSelectedContainers(partition));
    }

    [Test]
    public void GetAllSelectedContainers_WithNoSelectedContainers_ReturnsEmptyList()
    {
        // Arrange
        var partition = CreatePartitionWithContainers(
            CreateContainer("Root1", false),
            CreateContainer("Root2", false));

        // Act
        var result = ConnectedSystemUtilities.GetAllSelectedContainers(partition);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetAllSelectedContainers_WithSelectedRootContainer_ReturnsRootContainer()
    {
        // Arrange
        var partition = CreatePartitionWithContainers(
            CreateContainer("Root1", true),
            CreateContainer("Root2", false));

        // Act
        var result = ConnectedSystemUtilities.GetAllSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Root1"));
    }

    [Test]
    public void GetAllSelectedContainers_WithSelectedChildContainer_ReturnsChildContainer()
    {
        // Arrange
        var child1 = CreateContainer("Child1", true);
        var child2 = CreateContainer("Child2", false);
        var root = CreateContainer("Root", false, child1, child2);
        var partition = CreatePartitionWithContainers(root);

        // Act
        var result = ConnectedSystemUtilities.GetAllSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Child1"));
    }

    [Test]
    public void GetAllSelectedContainers_WithSelectedParentAndChild_ReturnsBoth()
    {
        // Arrange
        var child = CreateContainer("Child", true);
        var parent = CreateContainer("Parent", true, child);
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetAllSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Parent"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Child"));
    }

    [Test]
    public void GetAllSelectedContainers_WithDeepHierarchy_ReturnsAllSelected()
    {
        // Arrange - Create a 4-level hierarchy with some selected at each level
        var level4a = CreateContainer("Level4a", false);
        var level3a = CreateContainer("Level3a", true, level4a);
        var level2a = CreateContainer("Level2a", false, level3a);
        var level2b = CreateContainer("Level2b", true);
        var level1 = CreateContainer("Level1", true, level2a, level2b);
        var partition = CreatePartitionWithContainers(level1);

        // Act
        var result = ConnectedSystemUtilities.GetAllSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(c => c.Name), Contains.Item("Level1"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Level2b"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Level3a"));
    }

    #endregion

    #region GetTopLevelSelectedContainers Tests

    [Test]
    public void GetTopLevelSelectedContainers_WithNullPartition_ThrowsArgumentNullException()
    {
        // Arrange
        ConnectedSystemPartition? partition = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition!));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithNullContainers_ThrowsArgumentException()
    {
        // Arrange
        var partition = new ConnectedSystemPartition { Containers = null };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithNoSelectedContainers_ReturnsEmptyList()
    {
        // Arrange
        var partition = CreatePartitionWithContainers(
            CreateContainer("Root1", false),
            CreateContainer("Root2", false));

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSelectedRootOnly_ReturnsRootContainer()
    {
        // Arrange
        var partition = CreatePartitionWithContainers(
            CreateContainer("Root1", true),
            CreateContainer("Root2", false));

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Root1"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSelectedChildOnly_ReturnsChildContainer()
    {
        // Arrange
        var child1 = CreateContainer("Child1", true);
        var child2 = CreateContainer("Child2", false);
        var root = CreateContainer("Root", false, child1, child2);
        var partition = CreatePartitionWithContainers(root);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Child1"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSelectedParentAndChild_ReturnsOnlyParent()
    {
        // Arrange
        // This is the key test case - when both parent and child are selected,
        // only the parent should be returned (child is covered by subtree search)
        var child = CreateContainer("Child", true);
        var parent = CreateContainer("Parent", true, child);
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Parent"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSelectedParentAndMultipleChildren_ReturnsOnlyParent()
    {
        // Arrange - Simulates OU=Corp with selected children OU=Users and OU=Entitlements
        var users = CreateContainer("Users", true);
        var entitlements = CreateContainer("Entitlements", true);
        var corp = CreateContainer("Corp", true, users, entitlements);
        var partition = CreatePartitionWithContainers(corp);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Corp"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithUnselectedParentAndSelectedChildren_ReturnsChildren()
    {
        // Arrange - When parent is NOT selected, selected children should be returned
        var users = CreateContainer("Users", true);
        var entitlements = CreateContainer("Entitlements", true);
        var corp = CreateContainer("Corp", false, users, entitlements);
        var partition = CreatePartitionWithContainers(corp);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Users"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Entitlements"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSelectedGrandchild_ReturnsTopLevelAncestor()
    {
        // Arrange - If a grandparent is selected, grandchildren should not be returned
        var child = CreateContainer("Child", true);
        var parent = CreateContainer("Parent", true, child);
        var grandparent = CreateContainer("Grandparent", true, parent);
        var partition = CreatePartitionWithContainers(grandparent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Grandparent"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithMixedSelection_ReturnsOnlyTopLevels()
    {
        // Arrange - Complex hierarchy with mixed selection
        var child1a = CreateContainer("Child1a", true);
        var child1b = CreateContainer("Child1b", false);
        var root1 = CreateContainer("Root1", true, child1a, child1b);

        var grandchild2a = CreateContainer("Grandchild2a", true);
        var child2a = CreateContainer("Child2a", true, grandchild2a);
        var child2b = CreateContainer("Child2b", false);
        var root2 = CreateContainer("Root2", false, child2a, child2b);

        var partition = CreatePartitionWithContainers(root1, root2);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        // Should return: Root1 (covers Child1a), Child2a (covers Grandchild2a)
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Root1"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Child2a"));
        // Should NOT contain Child1a or Grandchild2a (covered by ancestors)
        Assert.That(result.Select(c => c.Name), Does.Not.Contain("Child1a"));
        Assert.That(result.Select(c => c.Name), Does.Not.Contain("Grandchild2a"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithMultipleIndependentBranches_ReturnsAllTopLevels()
    {
        // Arrange - Two independent selected branches
        var branchAChild = CreateContainer("BranchA_Child", true);
        var branchA = CreateContainer("BranchA", true, branchAChild);

        var branchBChild = CreateContainer("BranchB_Child", true);
        var branchB = CreateContainer("BranchB", true, branchBChild);

        var partition = CreatePartitionWithContainers(branchA, branchB);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("BranchA"));
        Assert.That(result.Select(c => c.Name), Contains.Item("BranchB"));
    }

    #endregion

    #region Helper Methods

    private static ConnectedSystemPartition CreatePartitionWithContainers(params ConnectedSystemContainer[] rootContainers)
    {
        var partition = new ConnectedSystemPartition
        {
            Id = _nextId++,
            Name = "Test Partition",
            Containers = new HashSet<ConnectedSystemContainer>(rootContainers)
        };

        return partition;
    }

    private static ConnectedSystemContainer CreateContainer(string name, bool selected, params ConnectedSystemContainer[] children)
    {
        var container = new ConnectedSystemContainer
        {
            Id = _nextId++,
            Name = name,
            Selected = selected,
            ExternalId = $"OU={name}"
        };

        foreach (var child in children)
        {
            container.AddChildContainer(child);
        }

        return container;
    }

    #endregion
}
