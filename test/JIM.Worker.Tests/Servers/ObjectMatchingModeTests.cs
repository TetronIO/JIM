using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ObjectMatchingModeTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IMetaverseRepository> _mockMvRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;
    private ConnectorDefinition _connectorDefinition = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockMvRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMvRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);

        // Setup activity repository to handle activity creation
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        // Create a valid connector definition for tests
        _connectorDefinition = new ConnectorDefinition
        {
            Id = 1,
            Name = "Test Connector",
            SupportsFullImport = true,
            SupportsDeltaImport = false,
            SupportsExport = true
        };

        _jim = new JimApplication(_mockRepository.Object);
        _initiatedBy = TestUtilities.GetInitiatedBy();
    }

    #region SwitchObjectMatchingModeAsync Tests

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToAdvancedMode_CopiesMatchingRulesToSyncRulesAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 1,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    CaseSensitive = false,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new()
                        {
                            Id = 1,
                            Order = 0,
                            ConnectedSystemAttributeId = 10
                        }
                    }
                }
            }
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ObjectTypes = new List<ConnectedSystemObjectType> { csObjectType },
            ConnectorDefinition = _connectorDefinition
        };

        var importSyncRule = new SyncRule
        {
            Id = 1,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>() // Empty - should receive copied rules
        };

        var exportSyncRule = new SyncRule
        {
            Id = 2,
            Name = "Export Users",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { importSyncRule, exportSyncRule });

        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.SyncRule, _initiatedBy);

        // Assert
        Assert.That(result, Is.EqualTo(1), "Should report 1 sync rule updated");
        Assert.That(importSyncRule.ObjectMatchingRules.Count, Is.EqualTo(1), "Import sync rule should have 1 matching rule");
        Assert.That(importSyncRule.ObjectMatchingRules[0].TargetMetaverseAttributeId, Is.EqualTo(100));
        Assert.That(importSyncRule.ObjectMatchingRules[0].CaseSensitive, Is.False);
        Assert.That(importSyncRule.ObjectMatchingRules[0].Sources.Count, Is.EqualTo(1));
        Assert.That(importSyncRule.ObjectMatchingRules[0].Sources[0].ConnectedSystemAttributeId, Is.EqualTo(10));
        Assert.That(exportSyncRule.ObjectMatchingRules.Count, Is.EqualTo(0), "Export sync rule should not receive matching rules");
        Assert.That(connectedSystem.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToAdvancedMode_SkipsSyncRulesWithExistingMatchingRulesAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 1,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Id = 1, Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                }
            }
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ObjectTypes = new List<ConnectedSystemObjectType> { csObjectType },
            ConnectorDefinition = _connectorDefinition
        };

        var syncRuleWithExistingRules = new SyncRule
        {
            Id = 1,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new() // Already has a matching rule
                {
                    Id = 99,
                    Order = 0,
                    TargetMetaverseAttributeId = 200,
                    Sources = new List<ObjectMatchingRuleSource>()
                }
            }
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { syncRuleWithExistingRules });

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.SyncRule, _initiatedBy);

        // Assert
        Assert.That(result, Is.EqualTo(0), "Should report 0 sync rules updated");
        Assert.That(syncRuleWithExistingRules.ObjectMatchingRules.Count, Is.EqualTo(1), "Should still have original rule");
        Assert.That(syncRuleWithExistingRules.ObjectMatchingRules[0].Id, Is.EqualTo(99), "Should keep original rule");

        // Verify UpdateSyncRuleAsync was NOT called
        _mockCsRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToSimpleMode_DoesNotCopyRulesAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
            ObjectTypes = new List<ConnectedSystemObjectType>(),
            ConnectorDefinition = _connectorDefinition
        };

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, _initiatedBy);

        // Assert
        Assert.That(result, Is.EqualTo(0), "Should report 0 sync rules updated when switching to Simple mode");
        Assert.That(connectedSystem.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));

        // Verify GetSyncRulesAsync was NOT called (no need to fetch sync rules for Simple mode)
        _mockCsRepo.Verify(r => r.GetSyncRulesAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_SameMode_ReturnsZeroAndDoesNothingAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ConnectorDefinition = _connectorDefinition
        };

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, _initiatedBy);

        // Assert
        Assert.That(result, Is.EqualTo(0));

        // Verify no repository methods were called
        _mockCsRepo.Verify(r => r.GetSyncRulesAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        _mockCsRepo.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToAdvancedMode_CopiesMultipleMatchingRulesAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 1,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    CaseSensitive = true,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Id = 1, Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                },
                new()
                {
                    Id = 2,
                    Order = 1,
                    TargetMetaverseAttributeId = 101,
                    CaseSensitive = false,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Id = 2, Order = 0, ConnectedSystemAttributeId = 11 }
                    }
                }
            }
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem,
            ObjectTypes = new List<ConnectedSystemObjectType> { csObjectType },
            ConnectorDefinition = _connectorDefinition
        };

        var importSyncRule = new SyncRule
        {
            Id = 1,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { importSyncRule });

        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.SyncRule, _initiatedBy);

        // Assert
        Assert.That(result, Is.EqualTo(1));
        Assert.That(importSyncRule.ObjectMatchingRules.Count, Is.EqualTo(2), "Should copy both matching rules");
        Assert.That(importSyncRule.ObjectMatchingRules[0].Order, Is.EqualTo(0));
        Assert.That(importSyncRule.ObjectMatchingRules[0].CaseSensitive, Is.True);
        Assert.That(importSyncRule.ObjectMatchingRules[1].Order, Is.EqualTo(1));
        Assert.That(importSyncRule.ObjectMatchingRules[1].CaseSensitive, Is.False);
    }

    [Test]
    public void SwitchObjectMatchingModeAsync_NullConnectedSystem_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(null!, ObjectMatchingRuleMode.SyncRule, _initiatedBy));
    }

    #endregion
}
