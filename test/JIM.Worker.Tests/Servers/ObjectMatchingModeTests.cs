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

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

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

    #region SwitchObjectMatchingModeAsync - To Advanced Mode Tests

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
            ObjectMatchingRules = new List<ObjectMatchingRule>()
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
        Assert.That(result.Success, Is.True);
        Assert.That(result.NewMode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
        Assert.That(result.SyncRulesUpdated, Is.EqualTo(1), "Should report 1 sync rule updated");
        Assert.That(importSyncRule.ObjectMatchingRules.Count, Is.EqualTo(1));
        Assert.That(importSyncRule.ObjectMatchingRules[0].TargetMetaverseAttributeId, Is.EqualTo(100));
        Assert.That(importSyncRule.ObjectMatchingRules[0].CaseSensitive, Is.False);
        Assert.That(exportSyncRule.ObjectMatchingRules.Count, Is.EqualTo(0));
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
                new()
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
        Assert.That(result.Success, Is.True);
        Assert.That(result.SyncRulesUpdated, Is.EqualTo(0), "Should report 0 sync rules updated");
        Assert.That(syncRuleWithExistingRules.ObjectMatchingRules.Count, Is.EqualTo(1));
        Assert.That(syncRuleWithExistingRules.ObjectMatchingRules[0].Id, Is.EqualTo(99));

        _mockCsRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_SameMode_ReturnsNoChangeAsync()
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
        Assert.That(result.Success, Is.True);
        Assert.That(result.SyncRulesUpdated, Is.EqualTo(0));
        Assert.That(result.ObjectTypesUpdated, Is.EqualTo(0));

        _mockCsRepo.Verify(r => r.GetSyncRulesAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        _mockCsRepo.Verify(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    [Test]
    public void SwitchObjectMatchingModeAsync_NullConnectedSystem_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(null!, ObjectMatchingRuleMode.SyncRule, _initiatedBy));
    }

    #endregion

    #region SwitchObjectMatchingModeAsync - To Simple Mode Tests

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToSimpleMode_MigratesRulesFromSyncRulesToObjectTypeAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>() // No rules yet
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule, // Advanced Mode
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
                }
            }
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { importSyncRule });

        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, _initiatedBy);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.NewMode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
        Assert.That(result.ObjectTypesUpdated, Is.EqualTo(1));
        Assert.That(result.Warnings.Count, Is.EqualTo(0), "Should have no warnings when rules don't diverge");

        // Object type should now have the matching rule
        Assert.That(csObjectType.ObjectMatchingRules.Count, Is.EqualTo(1));
        Assert.That(csObjectType.ObjectMatchingRules[0].TargetMetaverseAttributeId, Is.EqualTo(100));
        Assert.That(csObjectType.ObjectMatchingRules[0].CaseSensitive, Is.True);

        // Sync rule should have rules cleared
        Assert.That(importSyncRule.ObjectMatchingRules.Count, Is.EqualTo(0));

        Assert.That(connectedSystem.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToSimpleMode_SelectsMostCommonConfigurationWhenDivergingAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
            ObjectTypes = new List<ConnectedSystemObjectType> { csObjectType },
            ConnectorDefinition = _connectorDefinition
        };

        // Create 3 sync rules: 2 with config A, 1 with config B
        var syncRuleA1 = new SyncRule
        {
            Id = 1,
            Name = "Import Users A1",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 1,
                    Order = 0,
                    TargetMetaverseAttributeId = 100, // Config A
                    CaseSensitive = true,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Id = 1, Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                }
            }
        };

        var syncRuleA2 = new SyncRule
        {
            Id = 2,
            Name = "Import Users A2",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 2,
                    Order = 0,
                    TargetMetaverseAttributeId = 100, // Config A (same)
                    CaseSensitive = true,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Id = 2, Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                }
            }
        };

        var syncRuleB = new SyncRule
        {
            Id = 3,
            Name = "Import Users B",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 1,
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 3,
                    Order = 0,
                    TargetMetaverseAttributeId = 200, // Config B (different!)
                    CaseSensitive = false,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Id = 3, Order = 0, ConnectedSystemAttributeId = 20 }
                    }
                }
            }
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { syncRuleA1, syncRuleA2, syncRuleB });

        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, _initiatedBy);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ObjectTypesUpdated, Is.EqualTo(1));
        Assert.That(result.Warnings.Count, Is.EqualTo(1), "Should have 1 warning about diverging rules");
        Assert.That(result.Warnings[0], Does.Contain("2 different matching rule configurations"));

        // Object type should have config A (most common - 2 out of 3)
        Assert.That(csObjectType.ObjectMatchingRules.Count, Is.EqualTo(1));
        Assert.That(csObjectType.ObjectMatchingRules[0].TargetMetaverseAttributeId, Is.EqualTo(100));
        Assert.That(csObjectType.ObjectMatchingRules[0].CaseSensitive, Is.True);

        // Migration details
        Assert.That(result.ObjectTypeMigrations.Count, Is.EqualTo(1));
        var migration = result.ObjectTypeMigrations[0];
        Assert.That(migration.ObjectTypeName, Is.EqualTo("person"));
        Assert.That(migration.SyncRuleCount, Is.EqualTo(3));
        Assert.That(migration.SyncRulesWithMatchingRules, Is.EqualTo(3));
        Assert.That(migration.UniqueSyncRuleConfigurations, Is.EqualTo(2));
        Assert.That(migration.RulesDiverged, Is.True);
        Assert.That(migration.SyncRulesCleared, Is.EqualTo(3));
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToSimpleMode_ClearsSyncRuleRulesAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
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
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 1,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    Sources = new List<ObjectMatchingRuleSource>()
                }
            }
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { importSyncRule });

        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, _initiatedBy);

        // Assert - verify sync rule was updated (to clear its rules)
        _mockCsRepo.Verify(r => r.UpdateSyncRuleAsync(importSyncRule), Times.Once);
        Assert.That(importSyncRule.ObjectMatchingRules.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToSimpleMode_NoSyncRulesWithRules_DoesNotSetObjectTypeRulesAsync()
    {
        // Arrange
        var csObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ObjectMatchingRules = new List<ObjectMatchingRule>()
        };

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
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
            ObjectMatchingRules = new List<ObjectMatchingRule>() // No matching rules
        };

        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(1, true))
            .ReturnsAsync(new List<SyncRule> { importSyncRule });

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, _initiatedBy);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.ObjectTypesUpdated, Is.EqualTo(0));
        Assert.That(csObjectType.ObjectMatchingRules.Count, Is.EqualTo(0));
    }

    #endregion

    #region CreateOrUpdateSyncRuleAsync Simple Mode Validation Tests

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_SimpleMode_ClearsMatchingRulesFromSyncRuleAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem // Simple Mode
        };

        var syncRule = new SyncRule
        {
            Id = 0,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystem = connectedSystem,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" },
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 0,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                }
            }
        };

        _mockCsRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(syncRule.ObjectMatchingRules.Count, Is.EqualTo(0),
            "Matching rules should be cleared when Connected System is in Simple Mode");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_AdvancedMode_PreservesMatchingRulesOnSyncRuleAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule // Advanced Mode
        };

        var syncRule = new SyncRule
        {
            Id = 0,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 1,
            ConnectedSystem = connectedSystem,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" },
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 0,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                }
            }
        };

        _mockCsRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(syncRule.ObjectMatchingRules.Count, Is.EqualTo(1),
            "Matching rules should be preserved when Connected System is in Advanced Mode");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ExportRule_AlwaysClearsMatchingRulesAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule // Even in Advanced Mode
        };

        var syncRule = new SyncRule
        {
            Id = 0,
            Name = "Export Users",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = 1,
            ConnectedSystem = connectedSystem,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" },
            ObjectMatchingRules = new List<ObjectMatchingRule>
            {
                new()
                {
                    Id = 0,
                    Order = 0,
                    TargetMetaverseAttributeId = 100,
                    Sources = new List<ObjectMatchingRuleSource>
                    {
                        new() { Order = 0, ConnectedSystemAttributeId = 10 }
                    }
                }
            }
        };

        _mockCsRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(syncRule.ObjectMatchingRules.Count, Is.EqualTo(0),
            "Matching rules should always be cleared for export rules");
    }

    #endregion
}
