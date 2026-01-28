using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests for GetPendingExportsByConnectedSystemObjectIdsAsync in ConnectedSystemRepository.
/// Validates that duplicate pending exports for the same CSO are detected as a data integrity violation.
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
    public async Task GetPendingExportsByConnectedSystemObjectIdsAsync_WithEmptyInput_ReturnsEmptyDictionaryAsync()
    {
        // Arrange
        var csoIds = Array.Empty<Guid>();

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportsByConnectedSystemObjectIdsAsync_WithNoPendingExports_ReturnsEmptyDictionaryAsync()
    {
        // Arrange - no pending exports in data
        var csoIds = new[] { _cso1.Id };

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportsByConnectedSystemObjectIdsAsync_WithSinglePendingExportPerCso_ReturnsDictionaryAsync()
    {
        // Arrange - one pending export per CSO (normal case)
        var pe1 = CreatePendingExport(_cso1);
        var pe2 = CreatePendingExport(_cso2);
        _pendingExportsData.AddRange(new[] { pe1, pe2 });
        SetUpMockContext(); // Rebuild mock with updated data

        var csoIds = new[] { _cso1.Id, _cso2.Id };

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.ContainsKey(_cso1.Id), Is.True);
        Assert.That(result.ContainsKey(_cso2.Id), Is.True);
        Assert.That(result[_cso1.Id].Id, Is.EqualTo(pe1.Id));
        Assert.That(result[_cso2.Id].Id, Is.EqualTo(pe2.Id));
    }

    [Test]
    public void GetPendingExportsByConnectedSystemObjectIdsAsync_WithDuplicatePendingExportsForSameCso_ThrowsDuplicatePendingExportException()
    {
        // Arrange - TWO pending exports for the SAME CSO (data integrity violation)
        var pe1 = CreatePendingExport(_cso1);
        var pe2 = CreatePendingExport(_cso1); // Duplicate for same CSO
        _pendingExportsData.AddRange(new[] { pe1, pe2 });
        SetUpMockContext(); // Rebuild mock with updated data

        var csoIds = new[] { _cso1.Id };

        // Act & Assert
        var exception = Assert.ThrowsAsync<DuplicatePendingExportException>(async () =>
            await _repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(csoIds));

        Assert.That(exception!.Message, Does.Contain(_cso1.Id.ToString()));
        Assert.That(exception.Message, Does.Contain(pe1.Id.ToString()));
        Assert.That(exception.Message, Does.Contain(pe2.Id.ToString()));
    }

    [Test]
    public void GetPendingExportsByConnectedSystemObjectIdsAsync_WithDuplicatesAmongMultipleCsos_ThrowsDuplicatePendingExportException()
    {
        // Arrange - CSO1 has duplicates, CSO2 is fine
        var pe1 = CreatePendingExport(_cso1);
        var pe2 = CreatePendingExport(_cso2); // Normal
        var pe3 = CreatePendingExport(_cso1); // Duplicate for CSO1
        _pendingExportsData.AddRange(new[] { pe1, pe2, pe3 });
        SetUpMockContext(); // Rebuild mock with updated data

        var csoIds = new[] { _cso1.Id, _cso2.Id };

        // Act & Assert - should throw even if only one CSO has duplicates
        var exception = Assert.ThrowsAsync<DuplicatePendingExportException>(async () =>
            await _repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(csoIds));

        Assert.That(exception!.Message, Does.Contain(_cso1.Id.ToString()));
    }

    [Test]
    public async Task GetPendingExportsByConnectedSystemObjectIdsAsync_WithPendingExportsForUnrequestedCsos_OnlyReturnsRequestedAsync()
    {
        // Arrange - pending exports exist for both CSOs, but we only request CSO1
        var pe1 = CreatePendingExport(_cso1);
        var pe2 = CreatePendingExport(_cso2);
        _pendingExportsData.AddRange(new[] { pe1, pe2 });
        SetUpMockContext(); // Rebuild mock with updated data

        var csoIds = new[] { _cso1.Id };

        // Act
        var result = await _repository.ConnectedSystems.GetPendingExportsByConnectedSystemObjectIdsAsync(csoIds);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey(_cso1.Id), Is.True);
        Assert.That(result.ContainsKey(_cso2.Id), Is.False);
    }

    #region Helper Methods

    private PendingExport CreatePendingExport(ConnectedSystemObject cso)
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
            SourceMetaverseObject = null
        };
    }

    #endregion
}
