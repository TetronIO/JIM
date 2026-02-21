using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for ChangeHistoryServer, specifically the GetLastCleanupTimeAsync method
/// which is used by the worker to determine whether the cleanup interval has elapsed
/// since the last run, preventing immediate re-execution after worker restarts.
/// </summary>
[TestFixture]
public class ChangeHistoryServerTests
{
    private const int CleanupIntervalHours = 6;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IChangeHistoryRepository> _mockChangeHistoryRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockChangeHistoryRepository = new Mock<IChangeHistoryRepository>();
        _mockRepository = new Mock<IRepository>();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ChangeHistory).Returns(_mockChangeHistoryRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetLastCleanupTimeAsync_WhenCleanupHasRun_ReturnsLastCleanupTimeAsync()
    {
        // Arrange
        var expectedTime = new DateTime(2026, 2, 21, 10, 30, 0, DateTimeKind.Utc);
        _mockActivityRepository
            .Setup(r => r.GetLastHistoryCleanupTimeAsync())
            .ReturnsAsync(expectedTime);

        // Act
        var result = await _application.ChangeHistory.GetLastCleanupTimeAsync();

        // Assert
        Assert.That(result, Is.EqualTo(expectedTime));
        _mockActivityRepository.Verify(r => r.GetLastHistoryCleanupTimeAsync(), Times.Once);
    }

    [Test]
    public async Task GetLastCleanupTimeAsync_WhenNoCleanupHasRun_ReturnsNullAsync()
    {
        // Arrange
        _mockActivityRepository
            .Setup(r => r.GetLastHistoryCleanupTimeAsync())
            .ReturnsAsync((DateTime?)null);

        // Act
        var result = await _application.ChangeHistory.GetLastCleanupTimeAsync();

        // Assert
        Assert.That(result, Is.Null);
        _mockActivityRepository.Verify(r => r.GetLastHistoryCleanupTimeAsync(), Times.Once);
    }

    /// <summary>
    /// Simulates the worker restart scenario: cleanup ran recently (within 6 hours),
    /// so the interval check should determine that cleanup should be skipped.
    /// This tests the same logic the worker uses in PerformHousekeepingAsync.
    /// </summary>
    [Test]
    public async Task CleanupIntervalCheck_WhenCleanupRanRecently_ShouldSkipCleanupAsync()
    {
        // Arrange - cleanup ran 2 hours ago
        var lastCleanupTime = DateTime.UtcNow.AddHours(-2);
        _mockActivityRepository
            .Setup(r => r.GetLastHistoryCleanupTimeAsync())
            .ReturnsAsync(lastCleanupTime);

        // Act - simulate what the worker does on startup
        var lastCleanup = await _application.ChangeHistory.GetLastCleanupTimeAsync() ?? DateTime.MinValue;
        var shouldRunCleanup = (DateTime.UtcNow - lastCleanup).TotalHours >= CleanupIntervalHours;

        // Assert - should NOT run because only 2 hours have passed
        Assert.That(shouldRunCleanup, Is.False,
            "Cleanup should be skipped when the last run was within the 6-hour interval");
    }

    /// <summary>
    /// Simulates the worker restart scenario: cleanup ran a long time ago (over 6 hours),
    /// so the interval check should determine that cleanup should run.
    /// </summary>
    [Test]
    public async Task CleanupIntervalCheck_WhenCleanupRanOverSixHoursAgo_ShouldRunCleanupAsync()
    {
        // Arrange - cleanup ran 7 hours ago
        var lastCleanupTime = DateTime.UtcNow.AddHours(-7);
        _mockActivityRepository
            .Setup(r => r.GetLastHistoryCleanupTimeAsync())
            .ReturnsAsync(lastCleanupTime);

        // Act - simulate what the worker does on startup
        var lastCleanup = await _application.ChangeHistory.GetLastCleanupTimeAsync() ?? DateTime.MinValue;
        var shouldRunCleanup = (DateTime.UtcNow - lastCleanup).TotalHours >= CleanupIntervalHours;

        // Assert - should run because 7 hours have passed
        Assert.That(shouldRunCleanup, Is.True,
            "Cleanup should run when the last run was over 6 hours ago");
    }

    /// <summary>
    /// Simulates the worker restart scenario: no cleanup has ever run (fresh deployment),
    /// so the interval check should determine that cleanup should run.
    /// </summary>
    [Test]
    public async Task CleanupIntervalCheck_WhenNoCleanupHasEverRun_ShouldRunCleanupAsync()
    {
        // Arrange - no cleanup activity exists
        _mockActivityRepository
            .Setup(r => r.GetLastHistoryCleanupTimeAsync())
            .ReturnsAsync((DateTime?)null);

        // Act - simulate what the worker does: null falls back to DateTime.MinValue
        var lastCleanup = await _application.ChangeHistory.GetLastCleanupTimeAsync() ?? DateTime.MinValue;
        var shouldRunCleanup = (DateTime.UtcNow - lastCleanup).TotalHours >= CleanupIntervalHours;

        // Assert - should run because no cleanup has ever been performed
        Assert.That(shouldRunCleanup, Is.True,
            "Cleanup should run on first execution when no previous cleanup exists");
    }

    /// <summary>
    /// Simulates the edge case where cleanup ran exactly at the interval boundary.
    /// </summary>
    [Test]
    public async Task CleanupIntervalCheck_WhenCleanupRanExactlySixHoursAgo_ShouldRunCleanupAsync()
    {
        // Arrange - cleanup ran exactly 6 hours ago
        var lastCleanupTime = DateTime.UtcNow.AddHours(-CleanupIntervalHours);
        _mockActivityRepository
            .Setup(r => r.GetLastHistoryCleanupTimeAsync())
            .ReturnsAsync(lastCleanupTime);

        // Act
        var lastCleanup = await _application.ChangeHistory.GetLastCleanupTimeAsync() ?? DateTime.MinValue;
        var shouldRunCleanup = (DateTime.UtcNow - lastCleanup).TotalHours >= CleanupIntervalHours;

        // Assert - should run because >= 6 hours have passed
        Assert.That(shouldRunCleanup, Is.True,
            "Cleanup should run when exactly 6 hours have passed (boundary condition)");
    }
}
