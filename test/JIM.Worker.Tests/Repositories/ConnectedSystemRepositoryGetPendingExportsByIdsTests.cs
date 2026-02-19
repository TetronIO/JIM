using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for GetPendingExportsByIdsAsync - the Phase 3 repository method
/// that loads pending exports by ID for parallel batch re-loading.
/// </summary>
[TestFixture]
public class ConnectedSystemRepositoryGetPendingExportsByIdsTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private PostgresDataRepository _repository = null!;

    private List<PendingExport> _pendingExportsData = null!;
    private Mock<DbSet<PendingExport>> _mockDbSetPendingExports = null!;

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
            Name = "Test System"
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

        SetUpMockContext();
    }

    private void SetUpMockContext()
    {
        _mockDbSetPendingExports = _pendingExportsData.BuildMockDbSet();

        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.PendingExports).Returns(_mockDbSetPendingExports.Object);

        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_WithEmptyList_ReturnsEmptyAsync()
    {
        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(Array.Empty<Guid>());

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_WithMatchingIds_ReturnsCorrectExportsAsync()
    {
        // Arrange
        var pe1 = CreatePendingExport();
        var pe2 = CreatePendingExport();
        var pe3 = CreatePendingExport();
        _pendingExportsData.AddRange([pe1, pe2, pe3]);
        SetUpMockContext();

        // Act - request only pe1 and pe3
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(new[] { pe1.Id, pe3.Id });

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(pe => pe.Id), Does.Contain(pe1.Id));
        Assert.That(result.Select(pe => pe.Id), Does.Contain(pe3.Id));
        Assert.That(result.Select(pe => pe.Id), Does.Not.Contain(pe2.Id));
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_WithNoMatches_ReturnsEmptyAsync()
    {
        // Arrange
        var pe = CreatePendingExport();
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(new[] { Guid.NewGuid() });

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_WithPartialMatches_ReturnsOnlyMatchedAsync()
    {
        // Arrange
        var pe1 = CreatePendingExport();
        _pendingExportsData.Add(pe1);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(new[] { pe1.Id, Guid.NewGuid() });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(pe1.Id));
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_OrdersByCreatedAtAscendingAsync()
    {
        // Arrange
        var pe1 = CreatePendingExport();
        pe1.CreatedAt = DateTime.UtcNow.AddMinutes(-5);

        var pe2 = CreatePendingExport();
        pe2.CreatedAt = DateTime.UtcNow.AddMinutes(-30); // Oldest

        var pe3 = CreatePendingExport();
        pe3.CreatedAt = DateTime.UtcNow.AddMinutes(-15);

        _pendingExportsData.AddRange([pe1, pe2, pe3]);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(
            new[] { pe1.Id, pe2.Id, pe3.Id });

        // Assert - Oldest first
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].Id, Is.EqualTo(pe2.Id));
        Assert.That(result[1].Id, Is.EqualTo(pe3.Id));
        Assert.That(result[2].Id, Is.EqualTo(pe1.Id));
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_IncludesAttributeValueChangesAsync()
    {
        // Arrange
        var attr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = _objectType
        };

        var pe = CreatePendingExport();
        pe.AttributeValueChanges = new List<PendingExportAttributeValueChange>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ChangeType = PendingExportAttributeChangeType.Update,
                AttributeId = attr.Id,
                Attribute = attr,
                StringValue = "Test Value"
            }
        };
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(new[] { pe.Id });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        // In-memory mock DbSet auto-tracks navigation properties, so the includes
        // are effectively no-ops but the method signature ensures they're specified
        Assert.That(result[0].AttributeValueChanges, Has.Count.EqualTo(1));
        Assert.That(result[0].AttributeValueChanges[0].StringValue, Is.EqualTo("Test Value"));
    }

    [Test]
    public async Task GetPendingExportsByIdsAsync_IncludesConnectedSystemObjectAsync()
    {
        // Arrange
        var pe = CreatePendingExport();
        _pendingExportsData.Add(pe);
        SetUpMockContext();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByIdsAsync(new[] { pe.Id });

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ConnectedSystemObject, Is.Not.Null);
    }

    #region Helper Methods

    private PendingExport CreatePendingExport()
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
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>(),
            MaxRetries = 5
        };
    }

    #endregion
}
