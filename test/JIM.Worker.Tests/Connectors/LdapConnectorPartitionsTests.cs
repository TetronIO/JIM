using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class LdapConnectorPartitionsTests
{
    private const string PartitionDn = "DC=contoso,DC=local";

    #region Empty and null input tests

    [Test]
    public void BuildContainerHierarchy_WithEmptyList_ReturnsEmptyList()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>();

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Single container tests

    [Test]
    public void BuildContainerHierarchy_WithSingleTopLevelContainer_ReturnsOneContainer()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo("OU=Users,DC=contoso,DC=local"));
        Assert.That(result[0].Name, Is.EqualTo("Users"));
        Assert.That(result[0].ChildContainers, Is.Empty);
    }

    #endregion

    #region Multiple top-level containers tests

    [Test]
    public void BuildContainerHierarchy_WithMultipleTopLevelContainers_ReturnsAllContainers()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("OU=Groups,DC=contoso,DC=local", "Groups"),
            new("OU=Computers,DC=contoso,DC=local", "Computers")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(c => c.Name), Is.EquivalentTo(new[] { "Users", "Groups", "Computers" }));
    }

    [Test]
    public void BuildContainerHierarchy_WithMultipleTopLevelContainers_SortsAlphabetically()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Zebra,DC=contoso,DC=local", "Zebra"),
            new("OU=Alpha,DC=contoso,DC=local", "Alpha"),
            new("OU=Middle,DC=contoso,DC=local", "Middle")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].Name, Is.EqualTo("Alpha"));
        Assert.That(result[1].Name, Is.EqualTo("Middle"));
        Assert.That(result[2].Name, Is.EqualTo("Zebra"));
    }

    #endregion

    #region Nested hierarchy tests

    [Test]
    public void BuildContainerHierarchy_WithTwoLevelHierarchy_BuildsCorrectTree()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("OU=Admins,OU=Users,DC=contoso,DC=local", "Admins"),
            new("OU=Standard,OU=Users,DC=contoso,DC=local", "Standard")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Users"));
        Assert.That(result[0].ChildContainers, Has.Count.EqualTo(2));
        Assert.That(result[0].ChildContainers.Select(c => c.Name), Is.EquivalentTo(new[] { "Admins", "Standard" }));
    }

    [Test]
    public void BuildContainerHierarchy_WithThreeLevelHierarchy_BuildsCorrectTree()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("OU=IT,OU=Users,DC=contoso,DC=local", "IT"),
            new("OU=Developers,OU=IT,OU=Users,DC=contoso,DC=local", "Developers")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        var users = result[0];
        Assert.That(users.Name, Is.EqualTo("Users"));
        Assert.That(users.ChildContainers, Has.Count.EqualTo(1));

        var it = users.ChildContainers[0];
        Assert.That(it.Name, Is.EqualTo("IT"));
        Assert.That(it.ChildContainers, Has.Count.EqualTo(1));

        var developers = it.ChildContainers[0];
        Assert.That(developers.Name, Is.EqualTo("Developers"));
        Assert.That(developers.ChildContainers, Is.Empty);
    }

    [Test]
    public void BuildContainerHierarchy_WithDeepHierarchy_BuildsCorrectTree()
    {
        // Arrange - 5 levels deep
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Level1,DC=contoso,DC=local", "Level1"),
            new("OU=Level2,OU=Level1,DC=contoso,DC=local", "Level2"),
            new("OU=Level3,OU=Level2,OU=Level1,DC=contoso,DC=local", "Level3"),
            new("OU=Level4,OU=Level3,OU=Level2,OU=Level1,DC=contoso,DC=local", "Level4"),
            new("OU=Level5,OU=Level4,OU=Level3,OU=Level2,OU=Level1,DC=contoso,DC=local", "Level5")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));

        var current = result[0];
        for (int i = 1; i <= 5; i++)
        {
            Assert.That(current.Name, Is.EqualTo($"Level{i}"));
            if (i < 5)
            {
                Assert.That(current.ChildContainers, Has.Count.EqualTo(1));
                current = current.ChildContainers[0];
            }
            else
            {
                Assert.That(current.ChildContainers, Is.Empty);
            }
        }
    }

    #endregion

    #region Sorting tests

    [Test]
    public void BuildContainerHierarchy_SortsChildContainersAlphabetically()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Parent,DC=contoso,DC=local", "Parent"),
            new("OU=Zebra,OU=Parent,DC=contoso,DC=local", "Zebra"),
            new("OU=Apple,OU=Parent,DC=contoso,DC=local", "Apple"),
            new("OU=Mango,OU=Parent,DC=contoso,DC=local", "Mango")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        var children = result[0].ChildContainers;
        Assert.That(children, Has.Count.EqualTo(3));
        Assert.That(children[0].Name, Is.EqualTo("Apple"));
        Assert.That(children[1].Name, Is.EqualTo("Mango"));
        Assert.That(children[2].Name, Is.EqualTo("Zebra"));
    }

    [Test]
    public void BuildContainerHierarchy_SortsNestedChildContainersAlphabetically()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Parent,DC=contoso,DC=local", "Parent"),
            new("OU=Child,OU=Parent,DC=contoso,DC=local", "Child"),
            new("OU=Zebra,OU=Child,OU=Parent,DC=contoso,DC=local", "Zebra"),
            new("OU=Apple,OU=Child,OU=Parent,DC=contoso,DC=local", "Apple")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        var grandchildren = result[0].ChildContainers[0].ChildContainers;
        Assert.That(grandchildren, Has.Count.EqualTo(2));
        Assert.That(grandchildren[0].Name, Is.EqualTo("Apple"));
        Assert.That(grandchildren[1].Name, Is.EqualTo("Zebra"));
    }

    #endregion

    #region Case insensitivity tests

    [Test]
    public void BuildContainerHierarchy_HandlesCaseInsensitiveDnMatching()
    {
        // Arrange - mixed case partition DN
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,dc=CONTOSO,dc=LOCAL", "Users"), // lowercase dc=, uppercase domain
            new("OU=Admins,OU=Users,DC=contoso,DC=local", "Admins") // uppercase DC=, lowercase domain
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, "DC=Contoso,DC=Local");

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Users"));
        Assert.That(result[0].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(result[0].ChildContainers[0].Name, Is.EqualTo("Admins"));
    }

    #endregion

    #region Complex hierarchy tests

    [Test]
    public void BuildContainerHierarchy_WithMultipleBranches_BuildsCorrectTree()
    {
        // Arrange - realistic AD structure
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            // Top level
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("OU=Groups,DC=contoso,DC=local", "Groups"),
            new("OU=Computers,DC=contoso,DC=local", "Computers"),

            // Users branch
            new("OU=IT,OU=Users,DC=contoso,DC=local", "IT"),
            new("OU=HR,OU=Users,DC=contoso,DC=local", "HR"),
            new("OU=Developers,OU=IT,OU=Users,DC=contoso,DC=local", "Developers"),
            new("OU=Operations,OU=IT,OU=Users,DC=contoso,DC=local", "Operations"),

            // Groups branch
            new("OU=Security,OU=Groups,DC=contoso,DC=local", "Security"),
            new("OU=Distribution,OU=Groups,DC=contoso,DC=local", "Distribution"),

            // Computers branch
            new("OU=Servers,OU=Computers,DC=contoso,DC=local", "Servers"),
            new("OU=Workstations,OU=Computers,DC=contoso,DC=local", "Workstations")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert - top level
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(c => c.Name), Is.EqualTo(new[] { "Computers", "Groups", "Users" })); // sorted

        // Assert - Users branch
        var users = result.First(c => c.Name == "Users");
        Assert.That(users.ChildContainers, Has.Count.EqualTo(2)); // HR, IT
        Assert.That(users.ChildContainers.Select(c => c.Name), Is.EqualTo(new[] { "HR", "IT" })); // sorted

        var it = users.ChildContainers.First(c => c.Name == "IT");
        Assert.That(it.ChildContainers, Has.Count.EqualTo(2)); // Developers, Operations
        Assert.That(it.ChildContainers.Select(c => c.Name), Is.EqualTo(new[] { "Developers", "Operations" })); // sorted

        // Assert - Groups branch
        var groups = result.First(c => c.Name == "Groups");
        Assert.That(groups.ChildContainers, Has.Count.EqualTo(2)); // Distribution, Security
        Assert.That(groups.ChildContainers.Select(c => c.Name), Is.EqualTo(new[] { "Distribution", "Security" })); // sorted

        // Assert - Computers branch
        var computers = result.First(c => c.Name == "Computers");
        Assert.That(computers.ChildContainers, Has.Count.EqualTo(2)); // Servers, Workstations
        Assert.That(computers.ChildContainers.Select(c => c.Name), Is.EqualTo(new[] { "Servers", "Workstations" })); // sorted
    }

    [Test]
    public void BuildContainerHierarchy_WithUnorderedInput_BuildsCorrectTree()
    {
        // Arrange - entries in random order (child before parent)
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Developers,OU=IT,OU=Users,DC=contoso,DC=local", "Developers"), // deepest first
            new("OU=Users,DC=contoso,DC=local", "Users"), // top level in middle
            new("OU=IT,OU=Users,DC=contoso,DC=local", "IT") // intermediate last
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Users"));
        Assert.That(result[0].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(result[0].ChildContainers[0].Name, Is.EqualTo("IT"));
        Assert.That(result[0].ChildContainers[0].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(result[0].ChildContainers[0].ChildContainers[0].Name, Is.EqualTo("Developers"));
    }

    #endregion

    #region Container types tests

    [Test]
    public void BuildContainerHierarchy_WithMixedContainerTypes_BuildsCorrectTree()
    {
        // Arrange - mix of OUs and CNs (containers)
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("CN=Builtin,DC=contoso,DC=local", "Builtin"),
            new("CN=Computers,DC=contoso,DC=local", "Computers"),
            new("OU=Admins,OU=Users,DC=contoso,DC=local", "Admins")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3)); // Builtin, Computers, Users
        Assert.That(result.Select(c => c.Name), Is.EqualTo(new[] { "Builtin", "Computers", "Users" }));

        var users = result.First(c => c.Name == "Users");
        Assert.That(users.ChildContainers, Has.Count.EqualTo(1));
        Assert.That(users.ChildContainers[0].Name, Is.EqualTo("Admins"));
    }

    #endregion

    #region Edge cases tests

    [Test]
    public void BuildContainerHierarchy_WithSpecialCharactersInName_HandlesCorrectly()
    {
        // Arrange - DN with escaped special characters
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users \\+ Groups,DC=contoso,DC=local", "Users + Groups"),
            new("OU=Sub\\,Unit,OU=Users \\+ Groups,DC=contoso,DC=local", "Sub,Unit")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Users + Groups"));
        Assert.That(result[0].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(result[0].ChildContainers[0].Name, Is.EqualTo("Sub,Unit"));
    }

    [Test]
    public void BuildContainerHierarchy_WithOrphanedContainer_TreatsAsTopLevel()
    {
        // Arrange - container whose parent is not in the list (but isn't the partition)
        // This can happen if the parent is not an OU/container (e.g., a domain controller CN)
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("OU=Orphan,CN=SomeOtherObject,DC=contoso,DC=local", "Orphan")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert - orphan should be treated as top-level since parent isn't a known container
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Is.EquivalentTo(new[] { "Users", "Orphan" }));
    }

    [Test]
    public void BuildContainerHierarchy_WithDuplicateContainerNames_PreservesAllContainers()
    {
        // Arrange - same name under different parents
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Parent1,DC=contoso,DC=local", "Parent1"),
            new("OU=Parent2,DC=contoso,DC=local", "Parent2"),
            new("OU=SameName,OU=Parent1,DC=contoso,DC=local", "SameName"),
            new("OU=SameName,OU=Parent2,DC=contoso,DC=local", "SameName")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(result[0].ChildContainers[0].Name, Is.EqualTo("SameName"));
        Assert.That(result[1].ChildContainers, Has.Count.EqualTo(1));
        Assert.That(result[1].ChildContainers[0].Name, Is.EqualTo("SameName"));

        // Verify they're different containers (different DNs)
        Assert.That(result[0].ChildContainers[0].Id, Is.Not.EqualTo(result[1].ChildContainers[0].Id));
    }

    #endregion

    #region Performance regression tests

    [Test]
    public void BuildContainerHierarchy_WithLargeHierarchy_CompletesInReasonableTime()
    {
        // Arrange - 500 containers to simulate a medium-sized AD
        var entries = new List<LdapConnectorPartitions.ContainerEntry>();

        // Create 10 top-level OUs
        for (int i = 0; i < 10; i++)
        {
            var topDn = $"OU=Dept{i:D2},DC=contoso,DC=local";
            entries.Add(new(topDn, $"Dept{i:D2}"));

            // Each top-level has 10 children
            for (int j = 0; j < 10; j++)
            {
                var childDn = $"OU=Team{j:D2},OU=Dept{i:D2},DC=contoso,DC=local";
                entries.Add(new(childDn, $"Team{j:D2}"));

                // Each child has 4 grandchildren
                for (int k = 0; k < 4; k++)
                {
                    var grandchildDn = $"OU=Unit{k:D2},OU=Team{j:D2},OU=Dept{i:D2},DC=contoso,DC=local";
                    entries.Add(new(grandchildDn, $"Unit{k:D2}"));
                }
            }
        }

        // Verify we have the expected count: 10 + (10*10) + (10*10*4) = 510
        Assert.That(entries, Has.Count.EqualTo(510));

        // Act & Assert - should complete quickly (the O(n²) version would be slow here)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);
        stopwatch.Stop();

        // Assert structure
        Assert.That(result, Has.Count.EqualTo(10)); // 10 top-level departments
        Assert.That(result.All(d => d.ChildContainers.Count == 10), Is.True); // each has 10 teams
        Assert.That(result.SelectMany(d => d.ChildContainers).All(t => t.ChildContainers.Count == 4), Is.True); // each team has 4 units

        // Assert performance - should complete in under 1 second even on slow machines
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000),
            $"Hierarchy building took {stopwatch.ElapsedMilliseconds}ms for 510 containers");
    }

    #endregion

    #region Partition DN matching tests

    [Test]
    public void BuildContainerHierarchy_WithSubdomainPartition_IdentifiesTopLevelCorrectly()
    {
        // Arrange - subdomain partition
        var subdomainPartition = "DC=child,DC=contoso,DC=local";
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=child,DC=contoso,DC=local", "Users"),
            new("OU=Groups,DC=child,DC=contoso,DC=local", "Groups")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, subdomainPartition);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(c => c.Name), Is.EquivalentTo(new[] { "Users", "Groups" }));
    }

    [Test]
    public void BuildContainerHierarchy_PreservesContainerIds()
    {
        // Arrange
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new("OU=Users,DC=contoso,DC=local", "Users"),
            new("OU=Admins,OU=Users,DC=contoso,DC=local", "Admins")
        };

        // Act
        var result = LdapConnectorPartitions.BuildContainerHierarchy(entries, PartitionDn);

        // Assert - IDs should be the full DN
        Assert.That(result[0].Id, Is.EqualTo("OU=Users,DC=contoso,DC=local"));
        Assert.That(result[0].ChildContainers[0].Id, Is.EqualTo("OU=Admins,OU=Users,DC=contoso,DC=local"));
    }

    #endregion
}
