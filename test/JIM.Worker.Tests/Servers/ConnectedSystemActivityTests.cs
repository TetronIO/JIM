// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Connectors;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ConnectedSystemActivityTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;
    private Activity? _capturedActivity;

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
        _mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);

        // Nothing reads any attribute, and every attribute is stored unselected, unless a test says otherwise (#2265).

        _mockCsRepo.Setup(r => r.GetAttributeReaderCandidatesAsync()).ReturnsAsync(() => []);

        _mockCsRepo.Setup(r => r.GetAttributeSelectionStatesAsync(It.IsAny<int>())).ReturnsAsync(() => []);

        var attributeReaderExampleData = new Mock<IExampleDataRepository>();

        attributeReaderExampleData.Setup(r => r.GetAttributeReaderCandidatesAsync()).ReturnsAsync(() => []);

        _mockRepository.Setup(r => r.ExampleData).Returns(attributeReaderExampleData.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);

        // Capture the activity when CreateActivityAsync is called
        _capturedActivity = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _capturedActivity = a)
            .Returns(Task.CompletedTask);

        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);
        _initiatedBy = TestUtilities.GetInitiatedBy();
    }

    #region UpdateObjectTypeAsync Tests

    [Test]
    public async Task UpdateObjectTypeAsync_WithUserInitiator_CreatesActivityWithCorrectTargetNameAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test Connected System"
        };

        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            ConnectedSystemId = 1,
            ConnectedSystem = connectedSystem
        };

        _mockCsRepo.Setup(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateObjectTypeAsync(objectType, _initiatedBy);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("Test Connected System"),
            "TargetName should be the Connected System name, not the object type name");
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(1));
        Assert.That(_capturedActivity.Message, Is.EqualTo("Update object type: User"));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithApiKeyInitiator_CreatesActivityWithCorrectTargetNameAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 2,
            Name = "HR System"
        };

        var objectType = new ConnectedSystemObjectType
        {
            Id = 2,
            Name = "person",
            ConnectedSystemId = 2,
            ConnectedSystem = connectedSystem
        };

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test API Key"
        };

        _mockCsRepo.Setup(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateObjectTypeAsync(objectType, apiKey);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("HR System"),
            "TargetName should be the Connected System name, not the object type name");
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(2));
        Assert.That(_capturedActivity.Message, Is.EqualTo("Update object type: person"));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithNullConnectedSystem_UsesUnknownAsTargetNameAsync()
    {
        // Arrange
        var objectType = new ConnectedSystemObjectType
        {
            Id = 3,
            Name = "TestType",
            ConnectedSystemId = 3,
            ConnectedSystem = null! // Navigation property not loaded
        };

        _mockCsRepo.Setup(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateObjectTypeAsync(objectType, _initiatedBy);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("Unknown"),
            "TargetName should fall back to 'Unknown' when ConnectedSystem is not loaded");
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(3));
    }

    #endregion

    #region UpdateAttributeAsync Tests

    [Test]
    public async Task UpdateAttributeAsync_WithUserInitiator_CreatesActivityWithCorrectTargetNameAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Panoply AD"
        };

        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            ConnectedSystemId = 1,
            ConnectedSystem = connectedSystem
        };

        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "mail",
            ConnectedSystemObjectType = objectType
        };

        _mockCsRepo.Setup(r => r.UpdateAttributeAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateAttributeAsync(attribute, _initiatedBy);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("Panoply AD"),
            "TargetName should be the Connected System name, not the attribute or object type name");
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(1));
        Assert.That(_capturedActivity.Message, Is.EqualTo("Update attribute: User.mail"));
    }

    [Test]
    public async Task UpdateAttributeAsync_WithApiKeyInitiator_CreatesActivityWithCorrectTargetNameAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 2,
            Name = "HR CSV Source"
        };

        var objectType = new ConnectedSystemObjectType
        {
            Id = 2,
            Name = "person",
            ConnectedSystemId = 2,
            ConnectedSystem = connectedSystem
        };

        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "employeeId",
            ConnectedSystemObjectType = objectType
        };

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Integration API Key"
        };

        _mockCsRepo.Setup(r => r.UpdateAttributeAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateAttributeAsync(attribute, apiKey);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("HR CSV Source"),
            "TargetName should be the Connected System name");
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(2));
        Assert.That(_capturedActivity.Message, Is.EqualTo("Update attribute: person.employeeId"));
    }

    #endregion

    #region UpdateConnectedSystemAsync Tests

    [Test]
    public async Task UpdateConnectedSystemAsync_WithSettingValues_PassesSettingValuesToRepositoryAsync()
    {
        // Arrange
        var filePathSetting = new ConnectorDefinitionSetting { Name = "File Path", Required = true, Type = ConnectedSystemSettingType.File };
        var modeSetting = new ConnectorDefinitionSetting { Name = "Mode", Required = true, Type = ConnectedSystemSettingType.DropDown };
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test CS",
            ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName },
            SettingValues = new List<ConnectedSystemSettingValue>
            {
                new() { Id = 10, Setting = filePathSetting, StringValue = "/data/updated-file.csv" },
                new() { Id = 11, Setting = modeSetting, StringValue = "Import Only" }
            }
        };

        ConnectedSystem? capturedCs = null;
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Callback<ConnectedSystem>(cs => capturedCs = cs)
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, _initiatedBy);

        // Assert
        Assert.That(capturedCs, Is.Not.Null);
        Assert.That(capturedCs!.SettingValues, Is.Not.Null);
        Assert.That(capturedCs.SettingValues, Has.Count.EqualTo(2));
        Assert.That(capturedCs.SettingValues.First(sv => sv.Setting.Name == "File Path").StringValue,
            Is.EqualTo("/data/updated-file.csv"));
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_CreatesActivityWithCorrectTargetNameAsync()
    {
        // Arrange
        var filePathSetting = new ConnectorDefinitionSetting { Name = "File Path", Required = true, Type = ConnectedSystemSettingType.File };
        var modeSetting = new ConnectorDefinitionSetting { Name = "Mode", Required = true, Type = ConnectedSystemSettingType.DropDown };
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "HR System",
            ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName },
            SettingValues = new List<ConnectedSystemSettingValue>
            {
                new() { Id = 1, Setting = filePathSetting, StringValue = "/data/users.csv" },
                new() { Id = 2, Setting = modeSetting, StringValue = "Import Only" }
            }
        };

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, _initiatedBy);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("HR System"));
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(1));
    }

    #endregion

    #region UpdateConnectedSystemRunProfileAsync Tests

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_CreatesActivityWithTargetNameAsync()
    {
        // Arrange
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 1,
            Name = "Full Import",
            ConnectedSystemId = 1,
            RunType = ConnectedSystemRunType.FullImport
        };

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemRunProfileAsync(It.IsAny<ConnectedSystemRunProfile>()))
            .Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, _initiatedBy);

        // Assert
        Assert.That(_capturedActivity, Is.Not.Null);
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("Full Import"),
            "TargetName should be the Run Profile name");
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemRunProfile));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemRunProfileId, Is.EqualTo(1));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(1));
    }

    #endregion

    #region Allow Object Type Changes validation (#2049)

    // The portal calls the server directly rather than going through the REST API, so the Full-Import-only rule has
    // to hold here on its own: a refusal on every create and update overload, naming the setting, with nothing
    // persisted and no Activity recorded.

    private static ConnectedSystemRunProfile BuildRunProfileAllowingObjectTypeChanges(ConnectedSystemRunType runType) => new()
    {
        Id = 1,
        Name = "Object Type changes",
        ConnectedSystemId = 1,
        RunType = runType,
        AllowObjectTypeChanges = true
    };

    private void ArrangeConnectedSystemSupportingEveryRunType()
    {
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem
            {
                Id = 1,
                Name = "Contoso AD",
                ConnectorDefinition = new ConnectorDefinition
                {
                    Name = "Test Connector",
                    SupportsFullImport = true,
                    SupportsDeltaImport = true,
                    SupportsExport = true,
                    ExternalIdsUniqueAcrossObjectTypes = true
                }
            });
    }

    private static ApiKey BuildApiKey() => new() { Id = Guid.NewGuid(), Name = "Test API Key" };

    private void AssertNothingPersistedOrRecorded()
    {
        _mockCsRepo.Verify(r => r.CreateConnectedSystemRunProfileAsync(It.IsAny<ConnectedSystemRunProfile>()), Times.Never);
        _mockCsRepo.Verify(r => r.UpdateConnectedSystemRunProfileAsync(It.IsAny<ConnectedSystemRunProfile>()), Times.Never);
        Assert.That(_capturedActivity, Is.Null, "a refused Run Profile must not leave an Activity behind");
    }

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    [TestCase(ConnectedSystemRunType.Export)]
    public void CreateConnectedSystemRunProfileAsync_AllowObjectTypeChangesOnNonFullImportByUser_ThrowsNamingTheFieldAsync(ConnectedSystemRunType runType)
    {
        ArrangeConnectedSystemSupportingEveryRunType();

        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.ConnectedSystems.CreateConnectedSystemRunProfileAsync(BuildRunProfileAllowingObjectTypeChanges(runType), _initiatedBy));

        Assert.That(ex!.Message, Does.Contain(nameof(ConnectedSystemRunProfile.AllowObjectTypeChanges)));
        AssertNothingPersistedOrRecorded();
    }

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.Export)]
    public void CreateConnectedSystemRunProfileAsync_AllowObjectTypeChangesOnNonFullImportByApiKey_ThrowsNamingTheFieldAsync(ConnectedSystemRunType runType)
    {
        ArrangeConnectedSystemSupportingEveryRunType();

        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.ConnectedSystems.CreateConnectedSystemRunProfileAsync(BuildRunProfileAllowingObjectTypeChanges(runType), BuildApiKey()));

        Assert.That(ex!.Message, Does.Contain(nameof(ConnectedSystemRunProfile.AllowObjectTypeChanges)));
        AssertNothingPersistedOrRecorded();
    }

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    [TestCase(ConnectedSystemRunType.Export)]
    public void UpdateConnectedSystemRunProfileAsync_AllowObjectTypeChangesOnNonFullImportByUser_ThrowsNamingTheFieldAsync(ConnectedSystemRunType runType)
    {
        ArrangeConnectedSystemSupportingEveryRunType();

        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(BuildRunProfileAllowingObjectTypeChanges(runType), _initiatedBy));

        Assert.That(ex!.Message, Does.Contain(nameof(ConnectedSystemRunProfile.AllowObjectTypeChanges)));
        AssertNothingPersistedOrRecorded();
    }

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.Export)]
    public void UpdateConnectedSystemRunProfileAsync_AllowObjectTypeChangesOnNonFullImportByApiKey_ThrowsNamingTheFieldAsync(ConnectedSystemRunType runType)
    {
        ArrangeConnectedSystemSupportingEveryRunType();

        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(BuildRunProfileAllowingObjectTypeChanges(runType), BuildApiKey()));

        Assert.That(ex!.Message, Does.Contain(nameof(ConnectedSystemRunProfile.AllowObjectTypeChanges)));
        AssertNothingPersistedOrRecorded();
    }

    [Test]
    public async Task CreateConnectedSystemRunProfileAsync_AllowObjectTypeChangesOnFullImport_PersistsItAsync()
    {
        ArrangeConnectedSystemSupportingEveryRunType();

        await _jim.ConnectedSystems.CreateConnectedSystemRunProfileAsync(
            BuildRunProfileAllowingObjectTypeChanges(ConnectedSystemRunType.FullImport), _initiatedBy);

        _mockCsRepo.Verify(r => r.CreateConnectedSystemRunProfileAsync(
            It.Is<ConnectedSystemRunProfile>(rp => rp.AllowObjectTypeChanges && rp.RunType == ConnectedSystemRunType.FullImport)), Times.Once);
    }

    [Test]
    public async Task UpdateConnectedSystemRunProfileAsync_AllowObjectTypeChangesOnFullImport_PersistsItAsync()
    {
        ArrangeConnectedSystemSupportingEveryRunType();

        await _jim.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(
            BuildRunProfileAllowingObjectTypeChanges(ConnectedSystemRunType.FullImport), BuildApiKey());

        _mockCsRepo.Verify(r => r.UpdateConnectedSystemRunProfileAsync(
            It.Is<ConnectedSystemRunProfile>(rp => rp.AllowObjectTypeChanges)), Times.Once);
    }

    #endregion
}
