// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for GetPendingExportsLightweightByConnectedSystemObjectIdsAsync in ConnectedSystemRepository:
/// batching, keying by the ConnectedSystemObjectId FK, and exclusion of unrequested CSOs.
/// The duplicate self-heal behaviour deletes rows via raw SQL, which a mocked DbContext cannot
/// execute, so it is covered by the RequiresPostgres fixture
/// <see cref="PendingExportSelfHealDatabaseTests"/> instead.
/// </summary>
[TestFixture]
public class ConnectedSystemRepositoryPendingExportTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<PendingExport> _pendingExportsData = null!;
    private Mock<DbSet<PendingExport>> _mockDbSetPendingExports = null!;
    private PostgresDataRepository _repository = null!;

    // Test data
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _objectType = null!;
    private ConnectedSystemObject _cso1 = null!;
    private ConnectedSystemObject _cso2 = null!;

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

        _cso1 = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            Type = _objectType,
            TypeId = _objectType.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        _cso2 = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            Type = _objectType,
            TypeId = _objectType.Id,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
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
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_WithEmptyInput_ReturnsEmptyDictionaryAsync()
    {
        // Arrange
        var csoIds = Array.Empty<Guid>();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_WithNoPendingExports_ReturnsEmptyDictionaryAsync()
    {
        // Arrange - no Pending Exports in data
        var csoIds = new[] { _cso1.Id };

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_WithSinglePendingExportPerCso_ReturnsDictionaryAsync()
    {
        // Arrange - one Pending Export per CSO (normal case)
        var pe1 = CreatePendingExport(_cso1);
        var pe2 = CreatePendingExport(_cso2);
        _pendingExportsData.AddRange(new[] { pe1, pe2 });
        SetUpMockContext(); // Rebuild mock with updated data

        var csoIds = new[] { _cso1.Id, _cso2.Id };

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.ContainsKey(_cso1.Id), Is.True);
        Assert.That(result.ContainsKey(_cso2.Id), Is.True);
        Assert.That(result[_cso1.Id].Id, Is.EqualTo(pe1.Id));
        Assert.That(result[_cso2.Id].Id, Is.EqualTo(pe2.Id));
    }

    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_WithPendingExportsForUnrequestedCsos_OnlyReturnsRequestedAsync()
    {
        // Arrange - Pending Exports exist for both CSOs, but we only request CSO1
        var pe1 = CreatePendingExport(_cso1);
        var pe2 = CreatePendingExport(_cso2);
        _pendingExportsData.AddRange(new[] { pe1, pe2 });
        SetUpMockContext(); // Rebuild mock with updated data

        var csoIds = new[] { _cso1.Id };

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey(_cso1.Id), Is.True);
        Assert.That(result.ContainsKey(_cso2.Id), Is.False);
    }

    #region Helper Methods

    private PendingExport CreatePendingExport(ConnectedSystemObject cso, DateTime? createdAt = null)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>(),
            SourceMetaverseObject = null,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
    }

    #endregion
}
