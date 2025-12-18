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
/// Tests for the orphan detection query logic in MetaverseRepository.
/// These tests verify that GetMvosOrphanedByConnectedSystemDeletionAsync correctly identifies:
/// - MVOs that will become orphaned when a Connected System is deleted
/// - MVOs that should NOT be orphaned (multiple connectors, internal origin, manual deletion rule)
/// </summary>
[TestFixture]
public class MetaverseRepositoryOrphanDetectionTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<MetaverseObject> _metaverseObjectsData = null!;
    private Mock<DbSet<MetaverseObject>> _mockDbSetMetaverseObjects = null!;
    private PostgresDataRepository _repository = null!;

    // Test Connected System IDs
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;

    // Test MVO Type with WhenLastConnectorDisconnected deletion rule
    private MetaverseObjectType _personTypeWithDeletionRule = null!;
    // Test MVO Type with Manual deletion rule
    private MetaverseObjectType _personTypeWithManualDeletion = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        // Create MVO types
        _personTypeWithDeletionRule = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriodDays = 30
        };

        _personTypeWithManualDeletion = new MetaverseObjectType
        {
            Id = 2,
            Name = "ServiceAccount",
            DeletionRule = MetaverseObjectDeletionRule.Manual
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

    #region Orphan Detection Tests

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoOnlyInDeletedSystem_ReturnsAsOrphanAsync()
    {
        // Arrange - MVO with CSO only in the HR system (being deleted)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should be orphaned
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoInMultipleSystems_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with CSOs in both HR (being deleted) and AD (remaining)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        mvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (has connector in AD)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithInternalOrigin_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - Internal MVO (like admin accounts) with CSO only in deleted system
        var mvo = CreateInternalMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (internal origin is protected)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithManualDeletionRule_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with Manual deletion rule (only applies to WhenLastConnectorDisconnected)
        var mvo = CreateProjectedMvo(_personTypeWithManualDeletion);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (Manual deletion rule)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithNoCsoInDeletedSystem_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with CSO only in AD (not the system being deleted)
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, mvo));
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be orphaned (no CSO in the deleted system)
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMultipleOrphanedMvos_ReturnsAllOrphansAsync()
    {
        // Arrange - Three MVOs with CSOs only in HR system
        var mvo1 = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo1.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo1));

        var mvo2 = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo2.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo2));

        var mvo3 = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo3.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo3));

        _metaverseObjectsData.AddRange(new[] { mvo1, mvo2, mvo3 });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - all three should be orphaned
        Assert.That(orphanedMvos, Has.Count.EqualTo(3));
        Assert.That(orphanedMvos.Select(m => m.Id), Is.EquivalentTo(new[] { mvo1.Id, mvo2.Id, mvo3.Id }));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMixedScenarios_ReturnsOnlyOrphansAsync()
    {
        // Arrange - Mix of orphaned and non-orphaned MVOs

        // Should be orphaned: Projected MVO with CSO only in HR
        var orphanedMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        orphanedMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, orphanedMvo));

        // Should NOT be orphaned: MVO with CSOs in both systems
        var multiConnectorMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        multiConnectorMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, multiConnectorMvo));
        multiConnectorMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, multiConnectorMvo));

        // Should NOT be orphaned: Internal origin
        var internalMvo = CreateInternalMvo(_personTypeWithDeletionRule);
        internalMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, internalMvo));

        // Should NOT be orphaned: Manual deletion rule
        var manualDeletionMvo = CreateProjectedMvo(_personTypeWithManualDeletion);
        manualDeletionMvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, manualDeletionMvo));

        // Should NOT be orphaned: No CSO in deleted system
        var otherSystemMvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        otherSystemMvo.ConnectedSystemObjects.Add(CreateCso(AdSystemId, otherSystemMvo));

        _metaverseObjectsData.AddRange(new[] { orphanedMvo, multiConnectorMvo, internalMvo, manualDeletionMvo, otherSystemMvo });
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - only the first MVO should be orphaned
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(orphanedMvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoWithMultipleCsosInSameSystem_ReturnsAsOrphanAsync()
    {
        // Arrange - MVO with multiple CSOs but all in the same system being deleted
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo)); // First CSO in HR
        mvo.ConnectedSystemObjects.Add(CreateCso(HrSystemId, mvo)); // Second CSO in HR (different partition perhaps)
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should be orphaned (all CSOs are in the deleted system)
        Assert.That(orphanedMvos, Has.Count.EqualTo(1));
        Assert.That(orphanedMvos[0].Id, Is.EqualTo(mvo.Id));
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithNoMvos_ReturnsEmptyListAsync()
    {
        // Arrange - no MVOs in the system
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert
        Assert.That(orphanedMvos, Is.Empty);
    }

    [Test]
    public async Task GetMvosOrphanedByConnectedSystemDeletionAsync_WithMvoWithNoCsos_DoesNotReturnAsOrphanAsync()
    {
        // Arrange - MVO with no CSOs at all
        var mvo = CreateProjectedMvo(_personTypeWithDeletionRule);
        // No CSOs added
        _metaverseObjectsData.Add(mvo);
        SetupMockDbContext();

        // Act
        var orphanedMvos = await _repository.Metaverse.GetMvosOrphanedByConnectedSystemDeletionAsync(HrSystemId);

        // Assert - should NOT be returned (no CSO in the deleted system)
        Assert.That(orphanedMvos, Is.Empty);
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

    private static ConnectedSystemObject CreateCso(int connectedSystemId, MetaverseObject mvo)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            DateJoined = DateTime.UtcNow
        };
    }

    #endregion
}
