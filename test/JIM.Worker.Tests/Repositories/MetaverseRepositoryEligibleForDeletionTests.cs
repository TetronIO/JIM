using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for GetMetaverseObjectsEligibleForDeletionAsync in MetaverseRepository.
/// These tests verify that the housekeeping query correctly identifies MVOs that:
/// - Are projected (not internal admin accounts)
/// - Have deletion rule WhenLastConnectorDisconnected
/// - Have passed their grace period
/// - Have no remaining connected system objects
/// </summary>
[TestFixture]
public class MetaverseRepositoryEligibleForDeletionTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<MetaverseObject> _metaverseObjectsData = null!;
    private Mock<DbSet<MetaverseObject>> _mockDbSetMetaverseObjects = null!;
    private PostgresDataRepository _repository = null!;

    // Test MVO Types
    private MetaverseObjectType _personTypeWithDeletionRule = null!;
    private MetaverseObjectType _personTypeWithGracePeriod = null!;
    private MetaverseObjectType _personTypeWithManualDeletion = null!;
    private MetaverseObjectType _personTypeWithZeroGracePeriod = null!;

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        // Create MVO types with different deletion configurations
        _personTypeWithDeletionRule = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = null // No grace period
        };

        _personTypeWithGracePeriod = new MetaverseObjectType
        {
            Id = 2,
            Name = "PersonWithGrace",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        _personTypeWithManualDeletion = new MetaverseObjectType
        {
            Id = 3,
            Name = "ServiceAccount",
            DeletionRule = MetaverseObjectDeletionRule.Manual
        };

        _personTypeWithZeroGracePeriod = new MetaverseObjectType
        {
            Id = 4,
            Name = "PersonZeroGrace",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.Zero
        };

        // Initialise empty data - tests will populate as needed
        _metaverseObjectsData = new List<MetaverseObject>();
    }

    private void SetupMockDbContext()
    {
        _mockDbSetMetaverseObjects = _metaverseObjectsData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.MetaverseObjects).Returns(_mockDbSetMetaverseObjects.Object);
        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    #region Basic Eligibility Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithEligibleMvo_ReturnsMvoAsync()
    {
        // Arrange - MVO that meets all deletion criteria
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithNoEligibleMvos_ReturnsEmptyListAsync()
    {
        // Arrange - no MVOs
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Origin Protection Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithInternalOrigin_DoesNotReturnMvoAsync()
    {
        // Arrange - Internal MVO (admin account) should never be returned
        var mvo = CreateInternalMvo(_personTypeWithDeletionRule);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Internal MVOs are protected from automatic deletion
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithMixedOrigins_ReturnsOnlyProjectedAsync()
    {
        // Arrange
        var projectedMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        projectedMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);

        var internalMvo = CreateInternalMvo(_personTypeWithDeletionRule);
        internalMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);

        _metaverseObjectsData.AddRange(new[] { projectedMvo, internalMvo });
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Only projected MVO should be returned
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(projectedMvo.Id));
    }

    #endregion

    #region Deletion Rule Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithManualDeletionRule_DoesNotReturnMvoAsync()
    {
        // Arrange - MVO with Manual deletion rule
        var mvo = CreateProjectedMvo(_personTypeWithManualDeletion);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Manual deletion rule means no automatic deletion
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithNullType_DoesNotReturnMvoAsync()
    {
        // Arrange - MVO with null type
        var mvo = CreateProjectedMvo(null!);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Grace Period Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithNullGracePeriod_ReturnsImmediatelyAsync()
    {
        // Arrange - No grace period configured (null)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddSeconds(-1); // Just disconnected
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Should be eligible immediately with no grace period
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithZeroGracePeriod_ReturnsImmediatelyAsync()
    {
        // Arrange - Zero grace period configured
        var mvo = CreateProjectedMvo(_personTypeWithZeroGracePeriod);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddSeconds(-1); // Just disconnected
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Should be eligible immediately with zero grace period
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithGracePeriodNotExpired_DoesNotReturnMvoAsync()
    {
        // Arrange - 30 day grace period, disconnected 10 days ago
        var mvo = CreateProjectedMvo(_personTypeWithGracePeriod);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-10);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - 20 days remaining in grace period
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithGracePeriodExpired_ReturnsMvoAsync()
    {
        // Arrange - 30 day grace period, disconnected 31 days ago
        var mvo = CreateProjectedMvo(_personTypeWithGracePeriod);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-31);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Grace period has expired
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithGracePeriodExactlyExpired_ReturnsMvoAsync()
    {
        // Arrange - 30 day grace period, disconnected exactly 30 days ago
        var mvo = CreateProjectedMvo(_personTypeWithGracePeriod);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-30);
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Exactly at grace period boundary should be eligible
        Assert.That(result, Has.Count.EqualTo(1));
    }

    #endregion

    #region Connected System Object Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithRemainingCso_DoesNotReturnMvoAsync()
    {
        // Arrange - MVO still has a connected system object
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id
        });
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Cannot delete MVO that still has CSOs
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region Disconnection Date Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithNullDisconnectedDate_DoesNotReturnMvoAsync()
    {
        // Arrange - MVO was never disconnected
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.LastConnectorDisconnectedDate = null;
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - No disconnection date means not eligible
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region MaxResults and Ordering Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_RespectsMaxResultsAsync()
    {
        // Arrange - Create 10 eligible MVOs
        for (int i = 0; i < 10; i++)
        {
            var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
            mvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1 - i);
            _metaverseObjectsData.Add(mvo);
        }
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync(maxResults: 5);

        // Assert
        Assert.That(result, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_OrdersByDisconnectedDateAscendingAsync()
    {
        // Arrange - Create MVOs with different disconnection dates
        var oldestMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        oldestMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-30);

        var middleMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        middleMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-15);

        var newestMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        newestMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-5);

        // Add in random order
        _metaverseObjectsData.AddRange(new[] { middleMvo, newestMvo, oldestMvo });
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Should be ordered oldest first
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].Id, Is.EqualTo(oldestMvo.Id));
        Assert.That(result[1].Id, Is.EqualTo(middleMvo.Id));
        Assert.That(result[2].Id, Is.EqualTo(newestMvo.Id));
    }

    #endregion

    #region Complex Scenario Tests

    [Test]
    public async Task GetMetaverseObjectsEligibleForDeletionAsync_WithMixedScenarios_ReturnsOnlyEligibleAsync()
    {
        // Arrange - Various MVOs with different eligibility status

        // Eligible: Projected, disconnected 31 days ago, 30-day grace, no CSOs
        var eligibleMvo = CreateProjectedMvo(_personTypeWithGracePeriod);
        eligibleMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-31);

        // Not eligible: Internal origin
        var internalMvo = CreateInternalMvo(_personTypeWithDeletionRule);
        internalMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);

        // Not eligible: Manual deletion rule
        var manualMvo = CreateProjectedMvo(_personTypeWithManualDeletion);
        manualMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);

        // Not eligible: Grace period not expired
        var gracePeriodMvo = CreateProjectedMvo(_personTypeWithGracePeriod);
        gracePeriodMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-10);

        // Not eligible: Has remaining CSO
        var withCsoMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        withCsoMvo.LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-1);
        withCsoMvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            MetaverseObject = withCsoMvo,
            MetaverseObjectId = withCsoMvo.Id
        });

        // Not eligible: Never disconnected
        var neverDisconnectedMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        neverDisconnectedMvo.LastConnectorDisconnectedDate = null;

        _metaverseObjectsData.AddRange(new[]
        {
            eligibleMvo, internalMvo, manualMvo, gracePeriodMvo, withCsoMvo, neverDisconnectedMvo
        });
        SetupMockDbContext();

        // Act
        var result = await _repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync();

        // Assert - Only the first MVO should be eligible
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(eligibleMvo.Id));
    }

    #endregion

    #region Helper Methods

    private static MetaverseObject CreateProjectedMvo(MetaverseObjectType type)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            ConnectedSystemObjects = new List<ConnectedSystemObject>()
        };
    }

    private static MetaverseObject CreateInternalMvo(MetaverseObjectType type)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Internal,
            Type = type,
            ConnectedSystemObjects = new List<ConnectedSystemObject>()
        };
    }

    #endregion
}
