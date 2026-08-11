// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
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
    #region MergeHierarchy data-safety (#876)

    [Test]
    public void MergeHierarchy_WithZeroDiscoveredPartitions_LeavesExistingHierarchyIntact()
    {
        // Arrange - a Connected System with existing partitions (one selected). A connector returning zero
        // partitions almost always means a retrieval failure (connection/authentication/scope), not a directory
        // that genuinely has no partitions. Treating it as "all partitions removed" would destroy the configured
        // hierarchy and the user's selections (#876), so the merge must leave the existing hierarchy untouched.
        var connectedSystem = new ConnectedSystem
        {
            Partitions = new List<ConnectedSystemPartition>
            {
                new() { Name = "Partition One", ExternalId = "DC=one,DC=local", Selected = true },
                new() { Name = "Partition Two", ExternalId = "DC=two,DC=local", Selected = false }
            }
        };

        // Act
        var result = ConnectedSystemServer.MergeHierarchy(connectedSystem, new List<ConnectorPartition>());

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.Partitions, Has.Count.EqualTo(2), "existing partitions must survive an empty discovery");
            Assert.That(result.RemovedPartitions, Is.Empty, "nothing should be reported as removed");
            Assert.That(result.HasChanges, Is.False);
            Assert.That(result.HasSelectedItemsRemoved, Is.False);
        }
    }

    [Test]
    public void MergeHierarchy_WithDiscoveredPartitions_StillRemovesGenuinelyAbsentOnes()
    {
        // Arrange - a non-empty discovery that omits an existing partition is a genuine removal and must still
        // be honoured (guards against the data-safety fix over-reaching and disabling legitimate removals).
        var connectedSystem = new ConnectedSystem
        {
            Partitions = new List<ConnectedSystemPartition>
            {
                new() { Name = "Keep", ExternalId = "DC=keep,DC=local", Selected = false },
                new() { Name = "Gone", ExternalId = "DC=gone,DC=local", Selected = false }
            }
        };

        // Act - discovery contains only "Keep"
        var result = ConnectedSystemServer.MergeHierarchy(
            connectedSystem,
            new List<ConnectorPartition> { new() { Id = "DC=keep,DC=local", Name = "Keep" } });

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.Partitions.Select(p => p.ExternalId), Is.EquivalentTo(new[] { "DC=keep,DC=local" }));
            Assert.That(result.RemovedPartitions, Has.Count.EqualTo(1));
            Assert.That(result.RemovedPartitions[0].ExternalId, Is.EqualTo("DC=gone,DC=local"));
        }
    }

    [Test]
    public void MergeHierarchy_WhenAnExcludedContainerIsRenamed_KeepsTheExclusion()
    {
        // An exclusion is a statement about a Container, so it has to survive the Container being renamed or moved,
        // exactly as a selection does. Matching is on the Connected System's own immutable identifier for precisely
        // this reason: matching on the Distinguished Name would read a rename as a removal plus an addition, and the
        // re-added Container would arrive stating nothing, silently importing the branch that was carved out.
        var excluded = new ConnectedSystemContainer
        {
            Name = "Service Accounts",
            ExternalId = "OU=Service Accounts,OU=Corp,DC=example,DC=local",
            StableId = "11111111-1111-1111-1111-111111111111",
            Excluded = true
        };
        var corp = new ConnectedSystemContainer
        {
            Name = "Corp",
            ExternalId = "OU=Corp,DC=example,DC=local",
            StableId = "22222222-2222-2222-2222-222222222222",
            Selected = true
        };
        corp.AddChildContainer(excluded);

        var connectedSystem = new ConnectedSystem
        {
            Partitions =
            [
                new ConnectedSystemPartition
                {
                    Name = "example.local",
                    ExternalId = "DC=example,DC=local",
                    Selected = true,
                    Containers = [corp]
                }
            ]
        };

        // The same two Containers as the directory now reports them, with Service Accounts renamed in place.
        var discoveredCorp = new ConnectorContainer("OU=Corp,DC=example,DC=local", "Corp")
        {
            StableId = "22222222-2222-2222-2222-222222222222"
        };
        discoveredCorp.ChildContainers.Add(new ConnectorContainer("OU=Svc,OU=Corp,DC=example,DC=local", "Svc")
        {
            StableId = "11111111-1111-1111-1111-111111111111"
        });

        ConnectedSystemServer.MergeHierarchy(
            connectedSystem,
            [
                new ConnectorPartition
                {
                    Id = "DC=example,DC=local",
                    Name = "example.local",
                    Containers = [discoveredCorp]
                }
            ]);

        var merged = FlattenContainers(connectedSystem).Single(c => c.StableId == "11111111-1111-1111-1111-111111111111");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Excluded, Is.True, "the exclusion must survive a rename, or the branch is imported after all");
            Assert.That(merged.Selected, Is.False);
            Assert.That(merged.ExternalId, Is.EqualTo("OU=Svc,OU=Corp,DC=example,DC=local"), "the Container adopts its new Distinguished Name");
            Assert.That(merged.Name, Is.EqualTo("Svc"));
        }
    }

    #region Moved Containers (#1318)

    // A Container that moves to a different parent used to be deleted outright, taking its selection with it:
    // MergeContainersRecursive matched it by StableId at its new level but never reparented it, and the per-level
    // cleanup at its old level then removed it as absent from the directory. For a customer reorganising their
    // directory, container selections vanished and the objects beneath them were obsoleted on the next Full Import.

    [Test]
    public void MergeHierarchy_WhenASelectedContainerMovesToThePartitionRoot_KeepsItAndItsSelection()
    {
        var connectedSystem = SystemWithCorpAndSales(salesSelected: true);

        MergeWith(connectedSystem, DiscoveredCorp(), DiscoveredSales("OU=Sales,DC=example,DC=local"));

        var sales = FindByStableId(connectedSystem, SalesStableId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sales, Is.Not.Null, "the moved Container must still be in the hierarchy");
            Assert.That(sales!.Selected, Is.True, "and must still be selected");
            Assert.That(sales!.ExternalId, Is.EqualTo("OU=Sales,DC=example,DC=local"));
            Assert.That(connectedSystem.Partitions![0].Containers!, Does.Contain(sales), "it now sits at the partition root");
            Assert.That(sales!.ParentContainer, Is.Null);
        }
    }

    [Test]
    public void MergeHierarchy_WhenASelectedContainerMovesToAnotherParent_KeepsItAndItsSelection()
    {
        var connectedSystem = SystemWithCorpAndSales(salesSelected: true);
        var partners = new ConnectedSystemContainer
        {
            Name = "Partners",
            ExternalId = "OU=Partners,DC=example,DC=local",
            StableId = "33333333-3333-3333-3333-333333333333"
        };
        connectedSystem.Partitions![0].Containers!.Add(partners);

        var discoveredPartners = new ConnectorContainer("OU=Partners,DC=example,DC=local", "Partners")
        {
            StableId = "33333333-3333-3333-3333-333333333333"
        };
        discoveredPartners.ChildContainers.Add(new ConnectorContainer("OU=Sales,OU=Partners,DC=example,DC=local", "Sales")
        {
            StableId = SalesStableId
        });

        MergeWith(connectedSystem, DiscoveredCorp(), discoveredPartners);

        var sales = FindByStableId(connectedSystem, SalesStableId);
        var corp = FindByStableId(connectedSystem, CorpStableId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sales, Is.Not.Null);
            Assert.That(sales!.Selected, Is.True);
            Assert.That(sales!.ParentContainer?.Name, Is.EqualTo("Partners"));
            Assert.That(partners.ChildContainers, Does.Contain(sales));
            Assert.That(corp!.ChildContainers, Does.Not.Contain(sales), "and has left its old parent");
        }
    }

    [Test]
    public void MergeHierarchy_WhenAnExcludedContainerMoves_KeepsTheExclusion()
    {
        // The sibling of the selection case, and the reason #1255 depends on this fix: an exclusion is a statement
        // about a Container, so a move must not silently discard it and re-import the branch it carved out.
        var connectedSystem = SystemWithCorpAndSales(salesSelected: false);
        FindByStableId(connectedSystem, SalesStableId)!.Excluded = true;

        MergeWith(connectedSystem, DiscoveredCorp(), DiscoveredSales("OU=Sales,DC=example,DC=local"));

        var sales = FindByStableId(connectedSystem, SalesStableId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sales, Is.Not.Null);
            Assert.That(sales!.Excluded, Is.True);
        }
    }

    [Test]
    public void MergeHierarchy_WhenAContainerMoves_ItsChildrenMoveWithIt()
    {
        var connectedSystem = SystemWithCorpAndSales(salesSelected: true);
        var emea = new ConnectedSystemContainer
        {
            Name = "EMEA",
            ExternalId = "OU=EMEA,OU=Sales,OU=Corp,DC=example,DC=local",
            StableId = "44444444-4444-4444-4444-444444444444"
        };
        FindByStableId(connectedSystem, SalesStableId)!.AddChildContainer(emea);

        var discoveredSales = DiscoveredSales("OU=Sales,DC=example,DC=local");
        discoveredSales.ChildContainers.Add(new ConnectorContainer("OU=EMEA,OU=Sales,DC=example,DC=local", "EMEA")
        {
            StableId = "44444444-4444-4444-4444-444444444444"
        });

        MergeWith(connectedSystem, DiscoveredCorp(), discoveredSales);

        var sales = FindByStableId(connectedSystem, SalesStableId);
        var movedEmea = FindByStableId(connectedSystem, "44444444-4444-4444-4444-444444444444");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(movedEmea, Is.Not.Null, "a descendant of a moved Container travels with it");
            Assert.That(sales!.ChildContainers, Does.Contain(movedEmea));
        }
    }

    [Test]
    public void MergeHierarchy_WhenAContainerMoves_ReportsTheMove()
    {
        var connectedSystem = SystemWithCorpAndSales(salesSelected: true);

        var result = MergeWith(connectedSystem, DiscoveredCorp(), DiscoveredSales("OU=Sales,DC=example,DC=local"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MovedContainers, Has.Count.EqualTo(1));
            Assert.That(result.MovedContainers[0].Name, Is.EqualTo("Sales"));
            Assert.That(result.MovedContainers[0].OldParentExternalId, Is.EqualTo("OU=Corp,DC=example,DC=local"));
            Assert.That(result.MovedContainers[0].NewParentExternalId, Is.Null);
            Assert.That(result.RemovedContainers, Is.Empty, "a move is not a removal");
        }
    }

    [Test]
    public void MergeHierarchy_WhenAContainerGenuinelyDisappears_StillRemovesIt()
    {
        // Guards the fix against over-reaching: keeping moved Containers must not keep absent ones.
        var connectedSystem = SystemWithCorpAndSales(salesSelected: true);

        var result = MergeWith(connectedSystem, DiscoveredCorp());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FindByStableId(connectedSystem, SalesStableId), Is.Null);
            Assert.That(result.RemovedContainers, Has.Count.EqualTo(1));
            Assert.That(result.RemovedContainers[0].Name, Is.EqualTo("Sales"));
            Assert.That(result.HasSelectedItemsRemoved, Is.True, "the administrator must be told a selected Container went away");
        }
    }

    #endregion

    #region Moved-Container helpers

    private const string CorpStableId = "22222222-2222-2222-2222-222222222222";
    private const string SalesStableId = "11111111-1111-1111-1111-111111111111";

    /// <summary>OU=Sales, optionally selected, sitting inside OU=Corp in one selected partition.</summary>
    private static ConnectedSystem SystemWithCorpAndSales(bool salesSelected)
    {
        var sales = new ConnectedSystemContainer
        {
            Name = "Sales",
            ExternalId = "OU=Sales,OU=Corp,DC=example,DC=local",
            StableId = SalesStableId,
            Selected = salesSelected
        };
        var corp = new ConnectedSystemContainer
        {
            Name = "Corp",
            ExternalId = "OU=Corp,DC=example,DC=local",
            StableId = CorpStableId
        };
        corp.AddChildContainer(sales);

        var partition = new ConnectedSystemPartition
        {
            Name = "example.local",
            ExternalId = "DC=example,DC=local",
            Selected = true,
            Containers = [corp]
        };
        corp.Partition = partition;

        return new ConnectedSystem { Partitions = [partition] };
    }

    private static ConnectorContainer DiscoveredCorp() =>
        new("OU=Corp,DC=example,DC=local", "Corp") { StableId = CorpStableId };

    private static ConnectorContainer DiscoveredSales(string distinguishedName) =>
        new(distinguishedName, "Sales") { StableId = SalesStableId };

    private static HierarchyRefreshResult MergeWith(ConnectedSystem connectedSystem, params ConnectorContainer[] discoveredRootContainers) =>
        ConnectedSystemServer.MergeHierarchy(
            connectedSystem,
            [
                new ConnectorPartition
                {
                    Id = "DC=example,DC=local",
                    Name = "example.local",
                    Containers = [.. discoveredRootContainers]
                }
            ]);

    private static ConnectedSystemContainer? FindByStableId(ConnectedSystem connectedSystem, string stableId) =>
        FlattenContainers(connectedSystem).SingleOrDefault(c => c.StableId == stableId);

    #endregion

    private static IEnumerable<ConnectedSystemContainer> FlattenContainers(ConnectedSystem connectedSystem) =>
        (connectedSystem.Partitions ?? [])
            .SelectMany(p => p.Containers ?? [])
            .SelectMany(Flatten);

    private static IEnumerable<ConnectedSystemContainer> Flatten(ConnectedSystemContainer container) =>
        new[] { container }.Concat(container.ChildContainers.SelectMany(Flatten));

    #endregion

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ExternalId, Is.EqualTo("DC=test,DC=local"));
            Assert.That(item.Name, Is.EqualTo("test.local"));
            Assert.That(item.WasSelected, Is.True);
            Assert.That(item.ItemType, Is.EqualTo(HierarchyItemType.Partition));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ExternalId, Is.EqualTo("OU=Users,DC=test,DC=local"));
            Assert.That(item.Name, Is.EqualTo("Users"));
            Assert.That(item.WasSelected, Is.False);
            Assert.That(item.ItemType, Is.EqualTo(HierarchyItemType.Container));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ExternalId, Is.EqualTo("OU=HR,DC=test,DC=local"));
            Assert.That(item.OldName, Is.EqualTo("Human Resources"));
            Assert.That(item.NewName, Is.EqualTo("People Operations"));
            Assert.That(item.ItemType, Is.EqualTo(HierarchyItemType.Container));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ExternalId, Is.EqualTo("OU=Contractors,DC=test,DC=local"));
            Assert.That(item.Name, Is.EqualTo("Contractors"));
            Assert.That(item.OldParentExternalId, Is.EqualTo("OU=Vendors,DC=test,DC=local"));
            Assert.That(item.NewParentExternalId, Is.EqualTo("OU=Users,DC=test,DC=local"));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.OldParentExternalId, Is.Null);
            Assert.That(item.NewParentExternalId, Is.Not.Null);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.OldParentExternalId, Is.Not.Null);
            Assert.That(item.NewParentExternalId, Is.Null);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasChanges, Is.True);
            Assert.That(result.HasSelectedItemsRemoved, Is.True);
            Assert.That(result.AddedPartitions, Has.Count.EqualTo(1));
            Assert.That(result.RemovedContainers, Has.Count.EqualTo(1));
            Assert.That(result.RenamedContainers, Has.Count.EqualTo(1));
            Assert.That(result.MovedContainers, Has.Count.EqualTo(1));
        }

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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parent.ChildContainers, Contains.Item(child));
            Assert.That(child.ParentContainer, Is.SameAs(parent));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found1, Is.True);
            Assert.That(found2, Is.True);
            Assert.That(found3, Is.True);
            Assert.That(container1, Is.SameAs(container2));
            Assert.That(container2, Is.SameAs(container3));
        }
    }

    #endregion

    #region Container identity survives rename and move (#827)

    /// <summary>
    /// A container's External Id is its Distinguished Name, which every rename and every move changes. Matching on
    /// it alone meant a directory tidying an OU name presented to JIM as "the container you selected is gone, here
    /// is an unfamiliar one", silently narrowing import scope; the next whole-scope Full Import then obsoleted
    /// everything beneath it. Where the Connector can supply the directory's own immutable identifier, that is what
    /// identity is now keyed on.
    /// </summary>
    [Test]
    public void MergeHierarchy_ContainerRenamedInTheDirectory_KeepsItsSelectionAndIsReportedAsARename()
    {
        var connectedSystem = SystemWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            selected: true);

        var discovered = PartitionWithOneContainer(
            containerExternalId: "OU=Colleagues,DC=test,DC=local",
            containerName: "Colleagues",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff");

        var result = ConnectedSystemServer.MergeHierarchy(connectedSystem, [discovered]);

        var container = connectedSystem.Partitions![0].Containers!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Selected, Is.True, "a rename in the directory must not deselect a managed container");
            Assert.That(container.ExternalId, Is.EqualTo("OU=Colleagues,DC=test,DC=local"), "the new Distinguished Name must be adopted");
            Assert.That(container.Name, Is.EqualTo("Colleagues"));
            Assert.That(result.RenamedContainers, Has.Count.EqualTo(1));
            Assert.That(result.RemovedContainers, Is.Empty, "nothing left the directory");
            Assert.That(result.AddedContainers, Is.Empty, "nothing arrived in the directory");
            Assert.That(result.HasSelectedItemsRemoved, Is.False);
        }
    }

    [Test]
    public void MergeHierarchy_ContainerDistinguishedNameChangedByAnAncestorRename_KeepsItsSelection()
    {
        // Renaming an ancestor rewrites every descendant's Distinguished Name, so one tidy-up at the top of a
        // tree used to present as the wholesale removal of everything beneath it.
        var connectedSystem = SystemWithOneContainer(
            containerExternalId: "OU=Users,OU=Corp,DC=test,DC=local",
            containerName: "Users",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            selected: true);

        var discovered = PartitionWithOneContainer(
            containerExternalId: "OU=Users,OU=Retired,DC=test,DC=local",
            containerName: "Users",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff");

        var result = ConnectedSystemServer.MergeHierarchy(connectedSystem, [discovered]);

        var container = connectedSystem.Partitions![0].Containers!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Selected, Is.True);
            Assert.That(container.ExternalId, Is.EqualTo("OU=Users,OU=Retired,DC=test,DC=local"));
            Assert.That(result.RemovedContainers, Is.Empty);
            Assert.That(result.HasSelectedItemsRemoved, Is.False);
        }
    }

    [Test]
    public void MergeHierarchy_ContainerGenuinelyRemoved_IsStillReportedAsRemoved()
    {
        // Stable identity must not turn every disappearance into a rename: an identifier that no longer comes back
        // is a container that has gone, and a selected one going is exactly what the Partitions tab warns about.
        var connectedSystem = SystemWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            selected: true);

        var discovered = PartitionWithOneContainer(
            containerExternalId: "OU=Other,DC=test,DC=local",
            containerName: "Other",
            stableId: "11111111-2222-3333-4444-555555555555");

        var result = ConnectedSystemServer.MergeHierarchy(connectedSystem, [discovered]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RemovedContainers, Has.Count.EqualTo(1));
            Assert.That(result.RemovedContainers[0].ExternalId, Is.EqualTo("OU=Users,DC=test,DC=local"));
            Assert.That(result.HasSelectedItemsRemoved, Is.True);
        }
    }

    [Test]
    public void MergeHierarchy_ConnectorSuppliesNoStableId_StillMatchesOnDistinguishedName()
    {
        // Existing deployments carry no stable identifiers until their next hierarchy refresh, and a Connector may
        // have none to give. Distinguished Name matching remains the fallback so nothing regresses in the meantime.
        var connectedSystem = SystemWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: null,
            selected: true);

        var discovered = PartitionWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: null);

        var result = ConnectedSystemServer.MergeHierarchy(connectedSystem, [discovered]);

        var container = connectedSystem.Partitions![0].Containers!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Selected, Is.True);
            Assert.That(result.RemovedContainers, Is.Empty);
            Assert.That(result.AddedContainers, Is.Empty);
        }
    }

    [Test]
    public void MergeHierarchy_ContainerHasNoStoredStableIdButTheDirectorySuppliesOne_AdoptsIt()
    {
        // The upgrade path: containers selected before stable identifiers existed match on Distinguished Name once
        // more, and record the identifier as they do, so the very next rename is handled properly.
        var connectedSystem = SystemWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: null,
            selected: true);

        var discovered = PartitionWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff");

        ConnectedSystemServer.MergeHierarchy(connectedSystem, [discovered]);

        var container = connectedSystem.Partitions![0].Containers!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.StableId, Is.EqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            Assert.That(container.Selected, Is.True);
        }
    }

    /// <summary>
    /// A container created in the directory since the last refresh was added to the hierarchy and then deleted again
    /// by the same pass, because only matched containers were recorded as still present and a newly added one was
    /// recorded nowhere. The refresh reported it as added, and the Partitions tab never showed it, so a new OU could
    /// not be selected for management at all without re-creating the whole partition.
    /// </summary>
    [Test]
    public void MergeHierarchy_ContainerNewInTheDirectory_IsAddedAndKept()
    {
        var connectedSystem = SystemWithOneContainer(
            containerExternalId: "OU=Users,DC=test,DC=local",
            containerName: "Users",
            stableId: "6f9619ff-8b86-d011-b42d-00c04fc964ff",
            selected: true);

        var discovered = new ConnectorPartition { Id = "DC=test,DC=local", Name = "test.local" };
        discovered.Containers.Add(new ConnectorContainer("OU=Users,DC=test,DC=local", "Users") { StableId = "6f9619ff-8b86-d011-b42d-00c04fc964ff" });
        discovered.Containers.Add(new ConnectorContainer("OU=Contractors,DC=test,DC=local", "Contractors") { StableId = "22222222-3333-4444-5555-666666666666" });

        var result = ConnectedSystemServer.MergeHierarchy(connectedSystem, [discovered]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.Partitions![0].Containers!.Select(c => c.ExternalId),
                Is.EquivalentTo(new[] { "OU=Users,DC=test,DC=local", "OU=Contractors,DC=test,DC=local" }));
            Assert.That(result.AddedContainers, Has.Count.EqualTo(1));
            Assert.That(result.RemovedContainers, Is.Empty, "a container that was just discovered has not been removed from the directory");
        }
    }

    private static ConnectedSystem SystemWithOneContainer(string containerExternalId, string containerName, string? stableId, bool selected)
    {
        var container = new ConnectedSystemContainer
        {
            ExternalId = containerExternalId,
            Name = containerName,
            StableId = stableId,
            Selected = selected
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Partitions =
            [
                new ConnectedSystemPartition
                {
                    ExternalId = "DC=test,DC=local",
                    Name = "test.local",
                    Selected = true,
                    Containers = [container]
                }
            ]
        };
    }

    private static ConnectorPartition PartitionWithOneContainer(string containerExternalId, string containerName, string? stableId)
    {
        var partition = new ConnectorPartition { Id = "DC=test,DC=local", Name = "test.local" };
        partition.Containers.Add(new ConnectorContainer(containerExternalId, containerName) { StableId = stableId });
        return partition;
    }

    #endregion
}
