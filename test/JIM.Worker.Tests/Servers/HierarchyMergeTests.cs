using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for the hierarchy merge algorithm used when refreshing partition/container hierarchies.
/// These tests validate the merge behaviour by directly testing the static helper methods
/// where possible, or by testing the expected outcomes based on different input scenarios.
/// </summary>
[TestFixture]
public class HierarchyMergeTests
{
    #region HierarchyChangeItem Tests

    [Test]
    public void HierarchyChangeItem_CanStorePartitionData()
    {
        // Arrange & Act
        var item = new HierarchyChangeItem
        {
            ExternalId = "DC=test,DC=local",
            Name = "test.local",
            WasSelected = true,
            ItemType = HierarchyItemType.Partition
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(item.ExternalId, Is.EqualTo("DC=test,DC=local"));
            Assert.That(item.Name, Is.EqualTo("test.local"));
            Assert.That(item.WasSelected, Is.True);
            Assert.That(item.ItemType, Is.EqualTo(HierarchyItemType.Partition));
        });
    }

    [Test]
    public void HierarchyChangeItem_CanStoreContainerData()
    {
        // Arrange & Act
        var item = new HierarchyChangeItem
        {
            ExternalId = "OU=Users,DC=test,DC=local",
            Name = "Users",
            WasSelected = false,
            ItemType = HierarchyItemType.Container
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(item.ExternalId, Is.EqualTo("OU=Users,DC=test,DC=local"));
            Assert.That(item.Name, Is.EqualTo("Users"));
            Assert.That(item.WasSelected, Is.False);
            Assert.That(item.ItemType, Is.EqualTo(HierarchyItemType.Container));
        });
    }

    #endregion

    #region HierarchyRenameItem Tests

    [Test]
    public void HierarchyRenameItem_CanStoreRenameData()
    {
        // Arrange & Act
        var item = new HierarchyRenameItem
        {
            ExternalId = "OU=HR,DC=test,DC=local",
            OldName = "Human Resources",
            NewName = "People Operations",
            ItemType = HierarchyItemType.Container
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(item.ExternalId, Is.EqualTo("OU=HR,DC=test,DC=local"));
            Assert.That(item.OldName, Is.EqualTo("Human Resources"));
            Assert.That(item.NewName, Is.EqualTo("People Operations"));
            Assert.That(item.ItemType, Is.EqualTo(HierarchyItemType.Container));
        });
    }

    #endregion

    #region HierarchyMoveItem Tests

    [Test]
    public void HierarchyMoveItem_CanStoreMoveData()
    {
        // Arrange & Act
        var item = new HierarchyMoveItem
        {
            ExternalId = "OU=Contractors,DC=test,DC=local",
            Name = "Contractors",
            OldParentExternalId = "OU=Vendors,DC=test,DC=local",
            NewParentExternalId = "OU=Users,DC=test,DC=local"
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(item.ExternalId, Is.EqualTo("OU=Contractors,DC=test,DC=local"));
            Assert.That(item.Name, Is.EqualTo("Contractors"));
            Assert.That(item.OldParentExternalId, Is.EqualTo("OU=Vendors,DC=test,DC=local"));
            Assert.That(item.NewParentExternalId, Is.EqualTo("OU=Users,DC=test,DC=local"));
        });
    }

    [Test]
    public void HierarchyMoveItem_CanRepresentMoveFromRoot()
    {
        // Arrange & Act
        var item = new HierarchyMoveItem
        {
            ExternalId = "OU=Archive,DC=test,DC=local",
            Name = "Archive",
            OldParentExternalId = null, // Was at root
            NewParentExternalId = "OU=Legacy,DC=test,DC=local"
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(item.OldParentExternalId, Is.Null);
            Assert.That(item.NewParentExternalId, Is.Not.Null);
        });
    }

    [Test]
    public void HierarchyMoveItem_CanRepresentMoveToRoot()
    {
        // Arrange & Act
        var item = new HierarchyMoveItem
        {
            ExternalId = "OU=Promoted,DC=test,DC=local",
            Name = "Promoted",
            OldParentExternalId = "OU=Staging,DC=test,DC=local",
            NewParentExternalId = null // Now at root
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(item.OldParentExternalId, Is.Not.Null);
            Assert.That(item.NewParentExternalId, Is.Null);
        });
    }

    #endregion

    #region HierarchyRefreshResult Change Detection Tests

    [Test]
    public void HierarchyRefreshResult_TracksMultipleChanges()
    {
        // Arrange & Act
        var result = new HierarchyRefreshResult
        {
            Success = true,
            TotalPartitions = 2,
            TotalContainers = 10,
            AddedPartitions =
            {
                new HierarchyChangeItem { Name = "New Domain", ExternalId = "DC=new,DC=local", ItemType = HierarchyItemType.Partition }
            },
            RemovedContainers =
            {
                new HierarchyChangeItem { Name = "Old OU", ExternalId = "OU=Old,DC=test,DC=local", WasSelected = true, ItemType = HierarchyItemType.Container }
            },
            RenamedContainers =
            {
                new HierarchyRenameItem { ExternalId = "OU=HR,DC=test,DC=local", OldName = "HR", NewName = "People", ItemType = HierarchyItemType.Container }
            },
            MovedContainers =
            {
                new HierarchyMoveItem { ExternalId = "OU=M,DC=test,DC=local", Name = "M", OldParentExternalId = "OU=A", NewParentExternalId = "OU=B" }
            }
        };

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.HasChanges, Is.True);
            Assert.That(result.HasSelectedItemsRemoved, Is.True);
            Assert.That(result.AddedPartitions, Has.Count.EqualTo(1));
            Assert.That(result.RemovedContainers, Has.Count.EqualTo(1));
            Assert.That(result.RenamedContainers, Has.Count.EqualTo(1));
            Assert.That(result.MovedContainers, Has.Count.EqualTo(1));
        });

        // Verify summary includes all change types
        var summary = result.GetSummary();
        Assert.That(summary, Does.Contain("added"));
        Assert.That(summary, Does.Contain("removed"));
        Assert.That(summary, Does.Contain("updated"));
    }

    [Test]
    public void HierarchyRefreshResult_DistinguishesBetweenSelectedAndUnselectedRemovals()
    {
        // Arrange
        var resultWithSelectedRemoval = new HierarchyRefreshResult
        {
            Success = true,
            RemovedContainers =
            {
                new HierarchyChangeItem { Name = "Selected", ExternalId = "OU=S", WasSelected = true, ItemType = HierarchyItemType.Container }
            }
        };

        var resultWithUnselectedRemoval = new HierarchyRefreshResult
        {
            Success = true,
            RemovedContainers =
            {
                new HierarchyChangeItem { Name = "Unselected", ExternalId = "OU=U", WasSelected = false, ItemType = HierarchyItemType.Container }
            }
        };

        // Assert
        Assert.That(resultWithSelectedRemoval.HasSelectedItemsRemoved, Is.True);
        Assert.That(resultWithUnselectedRemoval.HasSelectedItemsRemoved, Is.False);
    }

    #endregion

    #region ConnectedSystemPartition and Container Model Tests

    [Test]
    public void ConnectedSystemPartition_DefaultsToUnselected()
    {
        // Arrange & Act
        var partition = new ConnectedSystemPartition
        {
            ExternalId = "DC=test,DC=local",
            Name = "test.local"
        };

        // Assert
        Assert.That(partition.Selected, Is.False);
    }

    [Test]
    public void ConnectedSystemContainer_DefaultsToUnselected()
    {
        // Arrange & Act
        var container = new ConnectedSystemContainer
        {
            ExternalId = "OU=Users,DC=test,DC=local",
            Name = "Users"
        };

        // Assert
        Assert.That(container.Selected, Is.False);
    }

    [Test]
    public void ConnectedSystemContainer_AddChildContainer_SetsParentRelationship()
    {
        // Arrange
        var parent = new ConnectedSystemContainer
        {
            ExternalId = "OU=Corp,DC=test,DC=local",
            Name = "Corp"
        };

        var child = new ConnectedSystemContainer
        {
            ExternalId = "OU=Users,OU=Corp,DC=test,DC=local",
            Name = "Users"
        };

        // Act
        parent.AddChildContainer(child);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(parent.ChildContainers, Contains.Item(child));
            Assert.That(child.ParentContainer, Is.SameAs(parent));
        });
    }

    [Test]
    public void ConnectedSystemContainer_AreAnyChildContainersSelected_ReturnsFalse_WhenNoChildren()
    {
        // Arrange
        var container = new ConnectedSystemContainer
        {
            ExternalId = "OU=Empty,DC=test,DC=local",
            Name = "Empty"
        };

        // Act & Assert
        Assert.That(container.AreAnyChildContainersSelected(), Is.False);
    }

    [Test]
    public void ConnectedSystemContainer_AreAnyChildContainersSelected_ReturnsFalse_WhenNoChildrenSelected()
    {
        // Arrange
        var parent = new ConnectedSystemContainer
        {
            ExternalId = "OU=Corp,DC=test,DC=local",
            Name = "Corp"
        };

        var child1 = new ConnectedSystemContainer
        {
            ExternalId = "OU=Users,OU=Corp,DC=test,DC=local",
            Name = "Users",
            Selected = false
        };

        var child2 = new ConnectedSystemContainer
        {
            ExternalId = "OU=Groups,OU=Corp,DC=test,DC=local",
            Name = "Groups",
            Selected = false
        };

        parent.AddChildContainer(child1);
        parent.AddChildContainer(child2);

        // Act & Assert
        Assert.That(parent.AreAnyChildContainersSelected(), Is.False);
    }

    [Test]
    public void ConnectedSystemContainer_AreAnyChildContainersSelected_ReturnsTrue_WhenChildSelected()
    {
        // Arrange
        var parent = new ConnectedSystemContainer
        {
            ExternalId = "OU=Corp,DC=test,DC=local",
            Name = "Corp"
        };

        var child = new ConnectedSystemContainer
        {
            ExternalId = "OU=Users,OU=Corp,DC=test,DC=local",
            Name = "Users",
            Selected = true
        };

        parent.AddChildContainer(child);

        // Act & Assert
        Assert.That(parent.AreAnyChildContainersSelected(), Is.True);
    }

    [Test]
    public void ConnectedSystemContainer_AreAnyChildContainersSelected_ReturnsTrue_WhenGrandchildSelected()
    {
        // Arrange
        var grandparent = new ConnectedSystemContainer
        {
            ExternalId = "OU=Corp,DC=test,DC=local",
            Name = "Corp"
        };

        var parent = new ConnectedSystemContainer
        {
            ExternalId = "OU=Users,OU=Corp,DC=test,DC=local",
            Name = "Users",
            Selected = false
        };

        var child = new ConnectedSystemContainer
        {
            ExternalId = "OU=Active,OU=Users,OU=Corp,DC=test,DC=local",
            Name = "Active",
            Selected = true
        };

        grandparent.AddChildContainer(parent);
        parent.AddChildContainer(child);

        // Act & Assert
        Assert.That(grandparent.AreAnyChildContainersSelected(), Is.True);
    }

    #endregion

    #region ExternalId Matching Tests (Case Insensitivity)

    [Test]
    public void ExternalId_ShouldBeCaseInsensitive_ForMatching()
    {
        // This test documents expected behaviour: ExternalIds from LDAP are DNS-like
        // and should be matched case-insensitively

        // Arrange
        var lookup = new Dictionary<string, ConnectedSystemContainer>(StringComparer.OrdinalIgnoreCase)
        {
            ["OU=Users,DC=test,DC=local"] = new ConnectedSystemContainer
            {
                ExternalId = "OU=Users,DC=test,DC=local",
                Name = "Users"
            }
        };

        // Act
        var found1 = lookup.TryGetValue("OU=Users,DC=test,DC=local", out var container1);
        var found2 = lookup.TryGetValue("ou=users,dc=test,dc=local", out var container2);
        var found3 = lookup.TryGetValue("OU=USERS,DC=TEST,DC=LOCAL", out var container3);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(found1, Is.True);
            Assert.That(found2, Is.True);
            Assert.That(found3, Is.True);
            Assert.That(container1, Is.SameAs(container2));
            Assert.That(container2, Is.SameAs(container3));
        });
    }

    #endregion
}
