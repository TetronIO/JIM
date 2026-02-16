using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Exceptions;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for ActivityServer error handling behaviour to ensure operational exceptions
/// (expected, user-actionable errors) do not persist stack traces, while unexpected
/// exceptions (bugs) do persist stack traces for developer diagnosis.
/// </summary>
[TestFixture]
public class ActivityServerErrorHandlingTests
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

    private static Activity CreateTestActivity()
    {
        return new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow.AddSeconds(-5)
        };
    }

    /// <summary>
    /// Helper to trigger an exception with a real stack trace by throwing and catching it.
    /// </summary>
    private static T ThrowAndCatch<T>(T exception) where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (T caught)
        {
            return caught;
        }
    }

    #region FailActivityWithErrorAsync tests

    [Test]
    public async Task FailActivityWithErrorAsync_WithOperationalException_DoesNotSetStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(new OperationalException("Something expected went wrong."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorMessage, Is.EqualTo("Something expected went wrong."));
        Assert.That(capturedActivity.ErrorStackTrace, Is.Null);
        Assert.That(capturedActivity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithUnexpectedException_SetsStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(new InvalidOperationException("Unexpected bug occurred."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorMessage, Is.EqualTo("Unexpected bug occurred."));
        Assert.That(capturedActivity.ErrorStackTrace, Is.Not.Null);
        Assert.That(capturedActivity.ErrorStackTrace, Is.Not.Empty);
        Assert.That(capturedActivity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithCannotPerformDeltaImportException_DoesNotSetStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(
            new CannotPerformDeltaImportException("No persisted connector data available. Run a full import first to establish a baseline."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorMessage, Does.Contain("No persisted connector data available"));
        Assert.That(capturedActivity.ErrorStackTrace, Is.Null);
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithCsvParsingException_DoesNotSetStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(new CsvParsingException("CSV file is missing column headers."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorStackTrace, Is.Null);
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithLdapCommunicationException_DoesNotSetStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(
            new LdapCommunicationException("LDAP response was null when querying directory information."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorStackTrace, Is.Null);
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithInvalidSettingValuesException_DoesNotSetStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(
            new InvalidSettingValuesException("File Path setting is required for export operations."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorStackTrace, Is.Null);
    }

    [Test]
    public async Task FailActivityWithErrorAsync_WithNullReferenceException_SetsStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(new NullReferenceException("Object reference not set to an instance of an object."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorStackTrace, Is.Not.Null);
        Assert.That(capturedActivity.ErrorStackTrace, Is.Not.Empty);
    }

    #endregion

    #region CompleteActivityWithErrorAsync tests

    [Test]
    public async Task CompleteActivityWithErrorAsync_WithOperationalException_DoesNotSetStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(new OperationalException("Expected operational error."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.CompleteActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorMessage, Is.EqualTo("Expected operational error."));
        Assert.That(capturedActivity.ErrorStackTrace, Is.Null);
        Assert.That(capturedActivity.Status, Is.EqualTo(ActivityStatus.CompleteWithError));
    }

    [Test]
    public async Task CompleteActivityWithErrorAsync_WithUnexpectedException_SetsStackTraceAsync()
    {
        // Arrange
        var activity = CreateTestActivity();
        var exception = ThrowAndCatch(new InvalidOperationException("Unexpected error."));

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        // Act
        await _application.Activities.CompleteActivityWithErrorAsync(activity, exception);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ErrorStackTrace, Is.Not.Null);
        Assert.That(capturedActivity.ErrorStackTrace, Is.Not.Empty);
        Assert.That(capturedActivity.Status, Is.EqualTo(ActivityStatus.CompleteWithError));
    }

    #endregion
}
