using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for export performance optimisation repository methods (Phase 1).
/// Validates GetExecutableExportsAsync, GetConnectedSystemObjectsByMetaverseObjectIdsAsync,
/// and GetAttributesByIdsAsync.
/// </summary>
[TestFixture]
public class ConnectedSystemRepositoryExportOptimisationTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private PostgresDataRepository _repository = null!;

    // Pending export data
    private List<PendingExport> _pendingExportsData = null!;
    private Mock<DbSet<PendingExport>> _mockDbSetPendingExports = null!;

    // CSO data
    private List<ConnectedSystemObject> _connectedSystemObjectsData = null!;
    private Mock<DbSet<ConnectedSystemObject>> _mockDbSetConnectedSystemObjects = null!;

    // Attribute data
    private List<ConnectedSystemObjectTypeAttribute> _attributesData = null!;
    private Mock<DbSet<ConnectedSystemObjectTypeAttribute>> _mockDbSetAttributes = null!;

    // Test entities
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _objectType = null!;

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test LDAP System"
        };

        _objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            Selected = true,
            Attributes = new List<ConnectedSystemObjectTypeAttribute>()
        };

        _pendingExportsData = new List<PendingExport>();
        _connectedSystemObjectsData = new List<ConnectedSystemObject>();
        _attributesData = new List<ConnectedSystemObjectTypeAttribute>();

        SetUpMockContext();
    }

    private void SetUpMockContext()
    {
        _mockDbSetPendingExports = _pendingExportsData.BuildMockDbSet();
        _mockDbSetConnectedSystemObjects = _connectedSystemObjectsData.BuildMockDbSet();
        _mockDbSetAttributes = _attributesData.BuildMockDbSet();

        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.PendingExports).Returns(_mockDbSetPendingExports.Object);
        _mockDbContext.Setup(m => m.ConnectedSystemObjects).Returns(_mockDbSetConnectedSystemObjects.Object);
        _mockDbContext.Setup(m => m.ConnectedSystemAttributes).Returns(_mockDbSetAttributes.Object);

        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    #region GetExecutableExportsAsync

    [Test]
    public async Task GetExecutableExportsAsync_WithNoExports_ReturnsEmptyListAsync()
    {
        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithPendingExport_ReturnsItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Pending);
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(pe.Id));
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithExportedStatus_ReturnsItAsync()
    {
        // Arrange - Exported status is eligible (may have attribute changes needing retry)
        var pe = CreatePendingExport(PendingExportStatus.Exported);
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithExportNotConfirmedStatus_ReturnsItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.ExportNotConfirmed);
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithFailedStatus_ExcludesItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Failed);
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithExecutingStatus_ExcludesItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Executing);
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithFutureRetryTime_ExcludesItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Pending);
        pe.NextRetryAt = DateTime.UtcNow.AddMinutes(30);
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithPastRetryTime_ReturnsItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Pending);
        pe.NextRetryAt = DateTime.UtcNow.AddMinutes(-5);
        pe.ErrorCount = 1;
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithMaxRetriesExceeded_ExcludesItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Pending);
        pe.ErrorCount = 3;
        pe.MaxRetries = 3;
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetExecutableExportsAsync_WithErrorCountBelowMaxRetries_ReturnsItAsync()
    {
        // Arrange
        var pe = CreatePendingExport(PendingExportStatus.Pending);
        pe.ErrorCount = 2;
        pe.MaxRetries = 3;
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetExecutableExportsAsync_OrdersByCreatedAtAscendingAsync()
    {
        // Arrange
        var pe1 = CreatePendingExport(PendingExportStatus.Pending);
        pe1.CreatedAt = DateTime.UtcNow.AddMinutes(-10);

        var pe2 = CreatePendingExport(PendingExportStatus.Pending);
        pe2.CreatedAt = DateTime.UtcNow.AddMinutes(-30); // Older

        var pe3 = CreatePendingExport(PendingExportStatus.Pending);
        pe3.CreatedAt = DateTime.UtcNow.AddMinutes(-20);

        _pendingExportsData.AddRange([pe1, pe2, pe3]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert - Oldest first
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].Id, Is.EqualTo(pe2.Id));
        Assert.That(result[1].Id, Is.EqualTo(pe3.Id));
        Assert.That(result[2].Id, Is.EqualTo(pe1.Id));
    }

    [Test]
    public async Task GetExecutableExportsAsync_FiltersToCorrectConnectedSystemAsync()
    {
        // Arrange
        var pe1 = CreatePendingExport(PendingExportStatus.Pending);

        var peOtherSystem = CreatePendingExport(PendingExportStatus.Pending);
        peOtherSystem.ConnectedSystemId = 999; // Different system

        _pendingExportsData.AddRange([pe1, peOtherSystem]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(pe1.Id));
    }

    [Test]
    public async Task GetExecutableExportsAsync_MixedEligibility_ReturnsOnlyEligibleAsync()
    {
        // Arrange
        var eligible = CreatePendingExport(PendingExportStatus.Pending);

        var failed = CreatePendingExport(PendingExportStatus.Failed);

        var futureRetry = CreatePendingExport(PendingExportStatus.Pending);
        futureRetry.NextRetryAt = DateTime.UtcNow.AddHours(1);

        var maxRetries = CreatePendingExport(PendingExportStatus.Pending);
        maxRetries.ErrorCount = 5;
        maxRetries.MaxRetries = 5;

        _pendingExportsData.AddRange([eligible, failed, futureRetry, maxRetries]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetExecutableExportsAsync(_connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(eligible.Id));
    }

    #endregion

    #region GetConnectedSystemObjectsByMetaverseObjectIdsAsync

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_WithEmptyInput_ReturnsEmptyDictionaryAsync()
    {
        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
            Array.Empty<Guid>(), _connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_WithMatchingCsos_ReturnsDictionaryAsync()
    {
        // Arrange
        var mvoId1 = Guid.NewGuid();
        var mvoId2 = Guid.NewGuid();

        var cso1 = CreateCso(mvoId1);
        var cso2 = CreateCso(mvoId2);
        _connectedSystemObjectsData.AddRange([cso1, cso2]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
            new[] { mvoId1, mvoId2 }, _connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.ContainsKey(mvoId1), Is.True);
        Assert.That(result.ContainsKey(mvoId2), Is.True);
        Assert.That(result[mvoId1].Id, Is.EqualTo(cso1.Id));
        Assert.That(result[mvoId2].Id, Is.EqualTo(cso2.Id));
    }

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_FiltersToCorrectSystemAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();

        var csoCorrectSystem = CreateCso(mvoId);
        var csoOtherSystem = CreateCso(mvoId);
        csoOtherSystem.ConnectedSystemId = 999; // Different system

        _connectedSystemObjectsData.AddRange([csoCorrectSystem, csoOtherSystem]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
            new[] { mvoId }, _connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[mvoId].Id, Is.EqualTo(csoCorrectSystem.Id));
    }

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_WithNoMatches_ReturnsEmptyDictionaryAsync()
    {
        // Arrange
        var nonExistentMvoId = Guid.NewGuid();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
            new[] { nonExistentMvoId }, _connectedSystem.Id);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemObjectsByMetaverseObjectIdsAsync_WithPartialMatches_ReturnsOnlyMatchedAsync()
    {
        // Arrange
        var mvoId1 = Guid.NewGuid();
        var mvoId2 = Guid.NewGuid(); // This one won't have a CSO

        var cso1 = CreateCso(mvoId1);
        _connectedSystemObjectsData.Add(cso1);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
            new[] { mvoId1, mvoId2 }, _connectedSystem.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey(mvoId1), Is.True);
        Assert.That(result.ContainsKey(mvoId2), Is.False);
    }

    #endregion

    #region GetAttributesByIdsAsync

    [Test]
    public async Task GetAttributesByIdsAsync_WithEmptyInput_ReturnsEmptyDictionaryAsync()
    {
        // Act
        var result = await _repository.ConnectedSystems.GetAttributesByIdsAsync(Array.Empty<int>());

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetAttributesByIdsAsync_WithMatchingAttributes_ReturnsDictionaryAsync()
    {
        // Arrange
        var attr1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "ObjectGuid",
            Type = AttributeDataType.Guid,
            ConnectedSystemObjectType = _objectType
        };
        var attr2 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = _objectType
        };

        _attributesData.AddRange([attr1, attr2]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetAttributesByIdsAsync(new[] { 1, 2 });

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.ContainsKey(1), Is.True);
        Assert.That(result.ContainsKey(2), Is.True);
        Assert.That(result[1].Name, Is.EqualTo("ObjectGuid"));
        Assert.That(result[2].Name, Is.EqualTo("DisplayName"));
    }

    [Test]
    public async Task GetAttributesByIdsAsync_WithPartialMatches_ReturnsOnlyMatchedAsync()
    {
        // Arrange
        var attr1 = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "ObjectGuid",
            Type = AttributeDataType.Guid,
            ConnectedSystemObjectType = _objectType
        };

        _attributesData.Add(attr1);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetAttributesByIdsAsync(new[] { 1, 999 });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey(1), Is.True);
        Assert.That(result.ContainsKey(999), Is.False);
    }

    #endregion

    #region Helper Methods

    private PendingExport CreatePendingExport(PendingExportStatus status)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            Type = _objectType,
            TypeId = _objectType.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>(),
            MaxRetries = 5
        };
    }

    private ConnectedSystemObject CreateCso(Guid metaverseObjectId)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            Type = _objectType,
            TypeId = _objectType.Id,
            MetaverseObjectId = metaverseObjectId,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
    }

    #endregion
}
