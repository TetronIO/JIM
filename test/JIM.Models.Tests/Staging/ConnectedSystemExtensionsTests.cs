using System.Collections.Generic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class ConnectedSystemExtensionsTests
{
    #region GetMode Tests

    [Test]
    public void GetMode_WhenModeSettingExists_ReturnsValue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.GetMode();

        // Assert
        Assert.That(result, Is.EqualTo("Import Only"));
    }

    [Test]
    public void GetMode_WhenNoModeSetting_ReturnsNull()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.GetMode();

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region IsExportOnlyMode Tests

    [Test]
    public void IsExportOnlyMode_WhenModeIsExportOnly_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Export Only");

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsExportOnlyMode_WhenModeIsImportOnly_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsExportOnlyMode_WhenModeIsBidirectional_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Bidirectional");

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsExportOnlyMode_WhenNoModeSetting_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region SupportsImportMode Tests

    [Test]
    public void SupportsImportMode_WhenModeIsImportOnly_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsImportMode_WhenModeIsBidirectional_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Bidirectional");

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsImportMode_WhenModeIsExportOnly_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Export Only");

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void SupportsImportMode_WhenNoModeSetting_ReturnsTrue()
    {
        // Arrange - connector without Mode setting (e.g., LDAP)
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region SupportsExportMode Tests

    [Test]
    public void SupportsExportMode_WhenModeIsExportOnly_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Export Only");

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsExportMode_WhenModeIsBidirectional_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Bidirectional");

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsExportMode_WhenModeIsImportOnly_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void SupportsExportMode_WhenNoModeSetting_ReturnsTrue()
    {
        // Arrange - connector without Mode setting (e.g., LDAP)
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region HasPartitionsOrContainersSelected Tests

    [Test]
    public void HasPartitionsOrContainersSelected_WhenConnectorDoesNotSupportPartitions_ReturnsTrue()
    {
        // Arrange - File connector doesn't support partitions
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: false, supportsContainers: false);

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionsNullAndSupportsPartitions_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = null;

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionsEmptyAndSupportsPartitions_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>();

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionsExistButNoneSelected_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "Partition1", Selected = false },
            new() { Name = "Partition2", Selected = false }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionSelectedButNoContainersSupported_ReturnsTrue()
    {
        // Arrange - Connector supports partitions but not containers
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: false);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "Partition1", Selected = true }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionSelectedButContainersNull_ReturnsFalse()
    {
        // Arrange - LDAP connector with partition selected but no containers retrieved
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "Partition1", Selected = true, Containers = null }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionSelectedButContainersEmpty_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "Partition1", Selected = true, Containers = new HashSet<ConnectedSystemContainer>() }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionSelectedButNoContainersSelected_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "Partition1",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer>
                {
                    new() { Name = "Container1", Selected = false },
                    new() { Name = "Container2", Selected = false }
                }
            }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenPartitionAndContainerSelected_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "Partition1",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer>
                {
                    new() { Name = "Container1", Selected = true }
                }
            }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenNestedContainerSelected_ReturnsTrue()
    {
        // Arrange - Only a nested child container is selected
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);

        var childContainer = new ConnectedSystemContainer { Name = "ChildContainer", Selected = true };
        var parentContainer = new ConnectedSystemContainer { Name = "ParentContainer", Selected = false };
        parentContainer.AddChildContainer(childContainer);

        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "Partition1",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer> { parentContainer }
            }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenDeeplyNestedContainerSelected_ReturnsTrue()
    {
        // Arrange - Only a deeply nested container is selected
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);

        var deepChild = new ConnectedSystemContainer { Name = "DeepChild", Selected = true };
        var midChild = new ConnectedSystemContainer { Name = "MidChild", Selected = false };
        var topContainer = new ConnectedSystemContainer { Name = "TopContainer", Selected = false };

        midChild.AddChildContainer(deepChild);
        topContainer.AddChildContainer(midChild);

        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "Partition1",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer> { topContainer }
            }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenUnselectedPartitionHasSelectedContainer_ReturnsFalse()
    {
        // Arrange - Container is selected but its partition is not
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "Partition1",
                Selected = false, // Partition NOT selected
                Containers = new HashSet<ConnectedSystemContainer>
                {
                    new() { Name = "Container1", Selected = true } // Container IS selected
                }
            }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasPartitionsOrContainersSelected_WhenMultiplePartitionsAndOnlyOneValid_ReturnsTrue()
    {
        // Arrange - Multiple partitions, only one has valid selections
        var connectedSystem = CreateConnectedSystemWithPartitionSupport(supportsPartitions: true, supportsContainers: true);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "Partition1",
                Selected = false,
                Containers = new HashSet<ConnectedSystemContainer>
                {
                    new() { Name = "Container1", Selected = true }
                }
            },
            new()
            {
                Name = "Partition2",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer>
                {
                    new() { Name = "Container2", Selected = true }
                }
            }
        };

        // Act
        var result = connectedSystem.HasPartitionsOrContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region ConnectedSystemContainer Parent-Child Relationship Tests

    [Test]
    public void AddChildContainer_SetsParentContainerReference()
    {
        // Arrange
        var parent = new ConnectedSystemContainer { Name = "Parent", Selected = true };
        var child = new ConnectedSystemContainer { Name = "Child", Selected = false };

        // Act
        parent.AddChildContainer(child);

        // Assert
        Assert.That(child.ParentContainer, Is.SameAs(parent));
        Assert.That(parent.ChildContainers, Contains.Item(child));
    }

    [Test]
    public void ParentContainer_CanTraverseUpHierarchy()
    {
        // Arrange - Build a 3-level hierarchy
        var grandparent = new ConnectedSystemContainer { Name = "Grandparent", Selected = true };
        var parent = new ConnectedSystemContainer { Name = "Parent", Selected = false };
        var child = new ConnectedSystemContainer { Name = "Child", Selected = false };

        grandparent.AddChildContainer(parent);
        parent.AddChildContainer(child);

        // Act & Assert - Traverse from child to grandparent
        Assert.That(child.ParentContainer, Is.SameAs(parent));
        Assert.That(child.ParentContainer?.ParentContainer, Is.SameAs(grandparent));
        Assert.That(child.ParentContainer?.ParentContainer?.ParentContainer, Is.Null);
    }

    [Test]
    public void AreAnyChildContainersSelected_WhenChildSelected_ReturnsTrue()
    {
        // Arrange
        var parent = new ConnectedSystemContainer { Name = "Parent", Selected = false };
        var child = new ConnectedSystemContainer { Name = "Child", Selected = true };
        parent.AddChildContainer(child);

        // Act
        var result = parent.AreAnyChildContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreAnyChildContainersSelected_WhenNoChildrenSelected_ReturnsFalse()
    {
        // Arrange
        var parent = new ConnectedSystemContainer { Name = "Parent", Selected = true };
        var child = new ConnectedSystemContainer { Name = "Child", Selected = false };
        parent.AddChildContainer(child);

        // Act
        var result = parent.AreAnyChildContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void AreAnyChildContainersSelected_WhenGrandchildSelected_ReturnsTrue()
    {
        // Arrange - Parent is selected, child is not, grandchild is selected
        var parent = new ConnectedSystemContainer { Name = "Parent", Selected = true };
        var child = new ConnectedSystemContainer { Name = "Child", Selected = false };
        var grandchild = new ConnectedSystemContainer { Name = "Grandchild", Selected = true };

        parent.AddChildContainer(child);
        child.AddChildContainer(grandchild);

        // Act
        var result = parent.AreAnyChildContainersSelected();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void AreAnyChildContainersSelected_WhenNoChildren_ReturnsFalse()
    {
        // Arrange
        var container = new ConnectedSystemContainer { Name = "Container", Selected = true };

        // Act
        var result = container.AreAnyChildContainersSelected();

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region Helper Methods

    private static ConnectedSystem CreateConnectedSystemWithMode(string mode)
    {
        var modeSetting = new ConnectorDefinitionSetting { Name = "Mode" };
        var connectedSystem = new ConnectedSystem
        {
            SettingValues = new List<ConnectedSystemSettingValue>
            {
                new()
                {
                    Setting = modeSetting,
                    StringValue = mode
                }
            }
        };
        return connectedSystem;
    }

    private static ConnectedSystem CreateConnectedSystemWithPartitionSupport(bool supportsPartitions, bool supportsContainers)
    {
        return new ConnectedSystem
        {
            Name = "Test System",
            SettingValues = new List<ConnectedSystemSettingValue>(),
            ConnectorDefinition = new ConnectorDefinition
            {
                Name = "Test Connector",
                SupportsPartitions = supportsPartitions,
                SupportsPartitionContainers = supportsContainers
            }
        };
    }

    #endregion
}
