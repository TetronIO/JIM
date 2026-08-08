// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    #region NewContainerNeedsSelecting Tests

    [Test]
    public void NewContainerNeedsSelecting_AtTheTopOfASelectedPartition_ReturnsTrue()
    {
        // A container with no parent is covered by nothing, so the partition's own selection decides.
        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(null, partitionSelected: true), Is.True);
    }

    [Test]
    public void NewContainerNeedsSelecting_AtTheTopOfAnUnselectedPartition_ReturnsFalse()
    {
        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(null, partitionSelected: false), Is.False);
    }

    [Test]
    public void NewContainerNeedsSelecting_BeneathASelectedSubtreeParent_ReturnsFalse()
    {
        // The parent's search already returns the new container's objects; selecting it too would import them twice.
        var parent = CreateContainer("Parent", true);

        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(parent, partitionSelected: true), Is.False);
    }

    [Test]
    public void NewContainerNeedsSelecting_BeneathASelectedOneLevelParent_ReturnsTrue()
    {
        // The parent's search stops at the objects held directly within it, so it never reaches the new container.
        var parent = CreateContainer("Parent", true, ConnectedSystemContainerScope.OneLevel);

        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(parent, partitionSelected: true), Is.True);
    }

    [Test]
    public void NewContainerNeedsSelecting_BeneathAnUnselectedParent_ReturnsFalse()
    {
        // Nothing beneath an unselected parent is in scope, so a new container there must not put itself in scope.
        var parent = CreateContainer("Parent", false);

        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(parent, partitionSelected: true), Is.False);
    }

    [Test]
    public void NewContainerNeedsSelecting_BeneathAOneLevelParentUnderASubtreeGrandparent_ReturnsFalse()
    {
        // Coverage is inherited from any Subtree ancestor, not only the immediate parent.
        var parent = CreateContainer("Parent", true, ConnectedSystemContainerScope.OneLevel);
        CreateContainer("Grandparent", true, ConnectedSystemContainerScope.Subtree, parent);

        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(parent, partitionSelected: true), Is.False);
    }

    [Test]
    public void NewContainerNeedsSelecting_BeneathAnUnselectedParentUnderASelectedOneLevelGrandparent_ReturnsFalse()
    {
        // The grandparent's OneLevel search does not reach the parent, so the whole branch is out of scope.
        var parent = CreateContainer("Parent", false);
        CreateContainer("Grandparent", true, ConnectedSystemContainerScope.OneLevel, parent);

        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(parent, partitionSelected: true), Is.False);
    }

    [Test]
    public void NewContainerNeedsSelecting_BeneathAnUnselectedParentUnderASelectedSubtreeGrandparent_ReturnsFalse()
    {
        // The grandparent's Subtree search reaches every level beneath it, including the new container.
        var parent = CreateContainer("Parent", false);
        CreateContainer("Grandparent", true, ConnectedSystemContainerScope.Subtree, parent);

        Assert.That(ConnectedSystemUtilities.NewContainerNeedsSelecting(parent, partitionSelected: true), Is.False);
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

    #region Container Scope Tests

    // Subtree is the scope every Container had before the option existed, and its pruning rule ("a selected
    // Container covers everything beneath it") is what stops duplicate import objects. OneLevel breaks that
    // rule deliberately: it covers only the objects held directly in the Container, so a selected descendant
    // is no longer redundant and has to come back as a search root of its own. Pruning it anyway would drop
    // those objects from the import in silence.

    [Test]
    public void GetTopLevelSelectedContainers_WithOneLevelParentAndSelectedChild_ReturnsBoth()
    {
        // Arrange
        var child = CreateContainer("Child", true);
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Parent"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Child"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithOneLevelParentAndSelectedGrandchild_ReturnsBoth()
    {
        // Arrange - the intervening Container is not selected, so the grandchild is outside the parent's one level
        var grandchild = CreateContainer("Grandchild", true);
        var child = CreateContainer("Child", false, grandchild);
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Parent"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Grandchild"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSubtreeParentAndSelectedOneLevelChild_ReturnsOnlyParent()
    {
        // Arrange - the parent's subtree search already covers the child, whatever scope the child carries
        var child = CreateContainer("Child", true);
        child.Scope = ConnectedSystemContainerScope.OneLevel;
        var parent = CreateContainer("Parent", true, child);
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Parent"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithOneLevelParentAndUnselectedDescendants_ReturnsOnlyParent()
    {
        // Arrange
        var grandchild = CreateContainer("Grandchild", false);
        var child = CreateContainer("Child", false, grandchild);
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Parent"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithNestedOneLevelContainers_ReturnsEachAsItsOwnRoot()
    {
        // Arrange - three levels, every one of them OneLevel and selected
        var grandchild = CreateContainer("Grandchild", true);
        grandchild.Scope = ConnectedSystemContainerScope.OneLevel;
        var child = CreateContainer("Child", true, grandchild);
        child.Scope = ConnectedSystemContainerScope.OneLevel;
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(c => c.Name), Contains.Item("Parent"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Child"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Grandchild"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithSubtreeDescendantOfOneLevelParent_StopsAtTheSubtreeDescendant()
    {
        // Arrange - Parent covers its own level; Child covers everything beneath itself, so Grandchild is redundant
        var grandchild = CreateContainer("Grandchild", true);
        var child = CreateContainer("Child", true, grandchild);
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Parent"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Child"));
        Assert.That(result.Select(c => c.Name), Does.Not.Contain("Grandchild"));
    }

    [Test]
    public void GetTopLevelSelectedContainers_WithOneLevelRootContainer_ReturnsSelectedDescendants()
    {
        // Arrange - the OneLevel Container is a root of the partition rather than a nested one
        var child = CreateContainer("Child", true);
        var root = CreateContainer("Root", true, child);
        root.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(root);

        // Act
        var result = ConnectedSystemUtilities.GetTopLevelSelectedContainers(partition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Contains.Item("Root"));
        Assert.That(result.Select(c => c.Name), Contains.Item("Child"));
    }

    #endregion

    #region ApplyContainerInclusion Tests

    // "Included" is the portal's way of saying "you do not need to select this, a selected ancestor already covers
    // it", and it is what makes a descendant's checkbox disabled. Only a Subtree ancestor covers anything below it;
    // beneath a OneLevel ancestor, the descendants are not imported at all, so they have to stay selectable. Getting
    // this wrong locks an administrator out of re-selecting exactly what they just excluded.

    [Test]
    public void ApplyContainerInclusion_WithSelectedSubtreeParent_MarksDescendantsIncluded()
    {
        // Arrange
        var grandchild = CreateContainer("Grandchild", false);
        var child = CreateContainer("Child", false, grandchild);
        var parent = CreateContainer("Parent", true, child);
        var partition = CreatePartitionWithContainers(parent);

        // Act
        ConnectedSystemUtilities.ApplyContainerInclusion(partition);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(parent.Included, Is.False, "a selected container is not included by anything above it");
            Assert.That(child.Included, Is.True);
            Assert.That(grandchild.Included, Is.True);
        });
    }

    [Test]
    public void ApplyContainerInclusion_WithSelectedOneLevelParent_LeavesDescendantsSelectable()
    {
        // Arrange
        var grandchild = CreateContainer("Grandchild", false);
        var child = CreateContainer("Child", false, grandchild);
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        ConnectedSystemUtilities.ApplyContainerInclusion(partition);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(child.Included, Is.False, "a one-level container does not import from the containers beneath it");
            Assert.That(grandchild.Included, Is.False);
        });
    }

    [Test]
    public void ApplyContainerInclusion_WithSubtreeContainerBeneathAOneLevelParent_MarksItsOwnDescendantsIncluded()
    {
        // Arrange
        var grandchild = CreateContainer("Grandchild", false);
        var child = CreateContainer("Child", true, grandchild);
        var parent = CreateContainer("Parent", true, child);
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        ConnectedSystemUtilities.ApplyContainerInclusion(partition);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(child.Included, Is.False, "selected in its own right, not covered by the one-level parent");
            Assert.That(grandchild.Included, Is.True, "covered by the child's own subtree search");
        });
    }

    [Test]
    public void ApplyContainerInclusion_WithNothingSelected_MarksNothingIncluded()
    {
        // Arrange
        var child = CreateContainer("Child", false);
        var parent = CreateContainer("Parent", false, child);
        var partition = CreatePartitionWithContainers(parent);

        // Act
        ConnectedSystemUtilities.ApplyContainerInclusion(partition);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(parent.Included, Is.False);
            Assert.That(child.Included, Is.False);
        });
    }

    [Test]
    public void ApplyContainerInclusion_WhenAContainerIsNarrowed_ClearsInclusionBeneathIt()
    {
        // Arrange - the state left behind by a previous pass, which narrowing must undo rather than leave stale
        var grandchild = CreateContainer("Grandchild", false);
        var child = CreateContainer("Child", false, grandchild);
        var parent = CreateContainer("Parent", true, child);
        child.Included = true;
        grandchild.Included = true;
        parent.Scope = ConnectedSystemContainerScope.OneLevel;
        var partition = CreatePartitionWithContainers(parent);

        // Act
        ConnectedSystemUtilities.ApplyContainerInclusion(partition);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(child.Included, Is.False);
            Assert.That(grandchild.Included, Is.False);
        });
    }

    #endregion

    #region Container Scope Tests (model defaults)

    [Test]
    public void ConnectedSystemContainer_ByDefault_IsSubtreeScoped()
    {
        // Arrange, Act
        var container = new ConnectedSystemContainer { Name = "Test", ExternalId = "OU=Test" };

        // Assert
        Assert.That(container.Scope, Is.EqualTo(ConnectedSystemContainerScope.Subtree));
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

    private static ConnectedSystemContainer CreateContainer(string name, bool selected, params ConnectedSystemContainer[] children) =>
        CreateContainer(name, selected, ConnectedSystemContainerScope.Subtree, children);

    private static ConnectedSystemContainer CreateContainer(string name, bool selected, ConnectedSystemContainerScope scope, params ConnectedSystemContainer[] children)
    {
        var container = new ConnectedSystemContainer
        {
            Id = _nextId++,
            Name = name,
            Selected = selected,
            Scope = scope,
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
