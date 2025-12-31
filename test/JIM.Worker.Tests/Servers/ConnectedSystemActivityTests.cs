using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
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
            Name = "Samba AD Primary"
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
        Assert.That(_capturedActivity!.TargetName, Is.EqualTo("Samba AD Primary"),
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
            "TargetName should be the run profile name");
        Assert.That(_capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemRunProfile));
        Assert.That(_capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_capturedActivity.ConnectedSystemRunProfileId, Is.EqualTo(1));
        Assert.That(_capturedActivity.ConnectedSystemId, Is.EqualTo(1));
    }

    #endregion
}
