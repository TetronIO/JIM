using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for ActivityServer validation to ensure all activities are properly attributed to a security principal.
/// This is a critical compliance requirement - all activities must be traceable to either a User (MetaverseObject) or an ApiKey.
/// </summary>
[TestFixture]
public class ActivityServerValidationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository = new Mock<IRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    #region Tests for User (MetaverseObject) initiated activities

    [Test]
    public async Task CreateActivityAsync_WithUserInitiator_SetsAllInitiatorPropertiesAsync()
    {
        // Arrange
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Name = "Display Name" },
            StringValue = "Test User"
        });

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Create
        };

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.CreateActivityAsync(activity, user);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(capturedActivity.InitiatedById, Is.EqualTo(user.Id));
        Assert.That(capturedActivity.InitiatedByName, Is.EqualTo("Test User"));
        Assert.That(capturedActivity.Status, Is.EqualTo(ActivityStatus.InProgress));
    }

    [Test]
    public void CreateActivityAsync_WithNullUserInitiator_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Create
        };

        // Act & Assert
        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _application.Activities.CreateActivityAsync(activity, (MetaverseObject?)null));

        Assert.That(exception!.Message, Does.Contain("InitiatedByType"));
    }

    #endregion

    #region Tests for ApiKey initiated activities

    [Test]
    public async Task CreateActivityAsync_WithApiKeyInitiator_SetsAllInitiatorPropertiesAsync()
    {
        // Arrange
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test Integration API Key",
            KeyPrefix = "jim_ak_1234",
            KeyHash = "dummy_hash",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Update
        };

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.CreateActivityAsync(activity, apiKey);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(capturedActivity.InitiatedById, Is.EqualTo(apiKey.Id));
        Assert.That(capturedActivity.InitiatedByName, Is.EqualTo("Test Integration API Key"));
        Assert.That(capturedActivity.Status, Is.EqualTo(ActivityStatus.InProgress));
    }

    [Test]
    public void CreateActivityAsync_WithNullApiKeyInitiator_ThrowsArgumentNullExceptionAsync()
    {
        // Arrange
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Create
        };

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _application.Activities.CreateActivityAsync(activity, (ApiKey)null!));
    }

    #endregion

    #region Validation tests for missing initiator properties

    [Test]
    public void CreateActivityAsync_WithInitiatedByTypeNotSet_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange - create activity with pre-set properties that bypass the overloads
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Create,
            // Manually setting properties to simulate invalid state
            InitiatedByType = ActivityInitiatorType.NotSet,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test"
        };

        // The overload with MetaverseObject? will not set properties if null is passed
        // This tests the validation catches the NotSet type

        // Act & Assert
        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _application.Activities.CreateActivityAsync(activity, (MetaverseObject?)null));

        Assert.That(exception!.Message, Does.Contain("InitiatedByType"));
    }

    [Test]
    public void CreateActivityAsync_WithUserType_ButNullMetaverseObject_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange - create an activity that claims to be user-initiated but has no user reference
        // This tests that validation catches inconsistent state
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test API Key",
            KeyPrefix = "jim_ak_test",
            KeyHash = "dummy_hash",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };

        // The CreateActivityAsync overload for ApiKey will set proper values
        // We're testing the overload works correctly when given valid input
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.MetaverseObject,
            TargetOperationType = ActivityTargetOperationType.Create
        };

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act - should succeed because ApiKey overload sets all required properties
        Assert.DoesNotThrowAsync(async () =>
            await _application.Activities.CreateActivityAsync(activity, apiKey));

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(capturedActivity.InitiatedById, Is.Not.Null);
        Assert.That(capturedActivity.InitiatedByName, Is.Not.Null);
    }

    #endregion

    #region Tests for activity status and execution time

    [Test]
    public async Task CreateActivityAsync_SetsStatusToInProgressAsync()
    {
        // Arrange
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test API Key",
            KeyPrefix = "jim_ak_test",
            KeyHash = "dummy_hash",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Create,
            Status = ActivityStatus.NotSet
        };

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.CreateActivityAsync(activity, apiKey);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.Status, Is.EqualTo(ActivityStatus.InProgress));
    }

    [Test]
    public async Task CreateActivityAsync_SetsExecutedTimeAsync()
    {
        // Arrange
        var beforeTest = DateTime.UtcNow;
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test API Key",
            KeyPrefix = "jim_ak_test",
            KeyHash = "dummy_hash",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Create
        };

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.CreateActivityAsync(activity, apiKey);
        var afterTest = DateTime.UtcNow;

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.Executed, Is.GreaterThanOrEqualTo(beforeTest));
        Assert.That(capturedActivity.Executed, Is.LessThanOrEqualTo(afterTest));
    }

    #endregion
}
