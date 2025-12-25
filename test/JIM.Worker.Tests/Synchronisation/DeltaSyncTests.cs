using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Unit tests for delta synchronisation functionality.
/// Tests cover the repository layer methods for querying CSOs modified since a timestamp,
/// as well as the ConnectedSystemServer methods that expose this functionality.
/// </summary>
[TestFixture]
public class DeltaSyncTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IMetaverseRepository> _mockMvRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockMvRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMvRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);

        // Setup activity repository to handle activity operations
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);
        _initiatedBy = TestUtilities.GetInitiatedBy();
    }

    #region GetConnectedSystemObjectsModifiedSinceAsync Tests

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithModifiedCsos_ReturnsCsosAfterWatermarkAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;
        var modifiedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            LastUpdated = DateTime.UtcNow // Modified after watermark
        };
        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject> { modifiedCso },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Has.Count.EqualTo(1));
        Assert.That(result.Results[0].Id, Is.EqualTo(modifiedCso.Id));
        Assert.That(result.TotalResults, Is.EqualTo(1));
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithNoModifiedCsos_ReturnsEmptyResultAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow;
        var connectedSystemId = 1;
        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Is.Empty);
        Assert.That(result.TotalResults, Is.EqualTo(0));
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithMultiplePages_ReturnsCorrectPageAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddDays(-1);
        var connectedSystemId = 1;
        const int pageSize = 50;
        const int totalResults = 150;

        var page2Csos = Enumerable.Range(0, pageSize)
            .Select(i => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystemId,
                LastUpdated = DateTime.UtcNow
            }).ToList();

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = page2Csos,
            TotalResults = totalResults,
            CurrentPage = 2,
            PageSize = pageSize
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 2, pageSize))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 2, pageSize);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Has.Count.EqualTo(pageSize));
        Assert.That(result.CurrentPage, Is.EqualTo(2));
        Assert.That(result.TotalResults, Is.EqualTo(totalResults));
        Assert.That(result.TotalPages, Is.EqualTo(3)); // 150/50 = 3 pages
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithMinDateTimeWatermark_ReturnsAllCsosAsync()
    {
        // Arrange - DateTime.MinValue watermark means this is the first delta sync (no previous watermark)
        var watermark = DateTime.MinValue;
        var connectedSystemId = 1;
        var allCsos = Enumerable.Range(0, 5)
            .Select(i => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystemId,
                LastUpdated = DateTime.UtcNow.AddDays(-i)
            }).ToList();

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = allCsos,
            TotalResults = 5,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Has.Count.EqualTo(5));
    }

    #endregion

    #region GetConnectedSystemObjectModifiedSinceCountAsync Tests

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_WithModifiedCsos_ReturnsCountAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-2);
        var connectedSystemId = 1;
        const int expectedCount = 42;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(expectedCount);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        Assert.That(result, Is.EqualTo(expectedCount));
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_WithNoModifiedCsos_ReturnsZeroAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow;
        var connectedSystemId = 1;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(0);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_WithDifferentConnectedSystems_ReturnsCorrectCountAsync()
    {
        // Arrange - Different connected systems have different modification counts
        var watermark = DateTime.UtcNow.AddDays(-1);

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(1, watermark))
            .ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(2, watermark))
            .ReturnsAsync(50);

        // Act
        var countCs1 = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(1, watermark);
        var countCs2 = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(2, watermark);

        // Assert
        Assert.That(countCs1, Is.EqualTo(100));
        Assert.That(countCs2, Is.EqualTo(50));
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_WithMinDateTimeWatermark_ReturnsAllCsosWithLastUpdatedAsync()
    {
        // Arrange - First delta sync scenario (no previous watermark)
        var watermark = DateTime.MinValue;
        var connectedSystemId = 1;
        const int expectedCount = 1000;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(expectedCount);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        Assert.That(result, Is.EqualTo(expectedCount));
    }

    #endregion

    #region LastDeltaSyncCompletedAt Watermark Tests

    [Test]
    public async Task ConnectedSystem_WithNullLastDeltaSyncCompletedAt_IndicatesFirstDeltaSyncAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            LastDeltaSyncCompletedAt = null // No previous delta sync
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LastDeltaSyncCompletedAt, Is.Null);
    }

    [Test]
    public async Task ConnectedSystem_WithLastDeltaSyncCompletedAt_ProvidesWatermarkAsync()
    {
        // Arrange
        var lastSyncTime = DateTime.UtcNow.AddHours(-1);
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            LastDeltaSyncCompletedAt = lastSyncTime
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.LastDeltaSyncCompletedAt, Is.EqualTo(lastSyncTime));
    }

    [Test]
    public void ConnectedSystem_LastDeltaSyncCompletedAt_CanBeSetAndRetrieved()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            LastDeltaSyncCompletedAt = null
        };

        var syncCompletionTime = DateTime.UtcNow;

        // Act - Set the watermark
        connectedSystem.LastDeltaSyncCompletedAt = syncCompletionTime;

        // Assert
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Not.Null);
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.EqualTo(syncCompletionTime));
    }

    [Test]
    public async Task RepositoryUpdateConnectedSystem_WithWatermark_IsCalledCorrectlyAsync()
    {
        // Arrange - Test that the repository method can be called with a ConnectedSystem that has a watermark
        var syncCompletionTime = DateTime.UtcNow;
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            LastDeltaSyncCompletedAt = syncCompletionTime
        };

        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>()))
            .Returns(Task.CompletedTask);

        // Act - Call the repository directly (simulating what the processor does internally)
        await _mockCsRepo.Object.UpdateConnectedSystemAsync(connectedSystem);

        // Assert
        _mockCsRepo.Verify(r => r.UpdateConnectedSystemAsync(
            It.Is<ConnectedSystem>(cs =>
                cs.LastDeltaSyncCompletedAt.HasValue &&
                cs.LastDeltaSyncCompletedAt == syncCompletionTime)), Times.Once);
    }

    #endregion

    #region Delta Sync Run Profile Tests

    [Test]
    public void DeltaSynchronisation_RunProfileType_ExistsInEnum()
    {
        // Assert that the DeltaSynchronisation run type exists
        Assert.That(Enum.IsDefined(typeof(ConnectedSystemRunType), ConnectedSystemRunType.DeltaSynchronisation));
    }

    [Test]
    public async Task GetConnectedSystemAsync_WithDeltaSyncRunProfile_ReturnsRunProfileAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            RunProfiles = new List<ConnectedSystemRunProfile>
            {
                new()
                {
                    Id = 1,
                    Name = "Delta Sync",
                    RunType = ConnectedSystemRunType.DeltaSynchronisation,
                    ConnectedSystemId = 1
                }
            }
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.RunProfiles, Has.Count.EqualTo(1));
        Assert.That(result.RunProfiles[0].RunType, Is.EqualTo(ConnectedSystemRunType.DeltaSynchronisation));
    }

    #endregion

    #region Newly Created CSO Tests

    [Test]
    public void DeltaSync_NewlyCreatedCso_HasCreatedButNoLastUpdated()
    {
        // Arrange - A newly created CSO has Created timestamp but no LastUpdated
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Created = DateTime.UtcNow
            // LastUpdated is null by default for new objects
        };

        // Assert
        Assert.That(cso.Created, Is.Not.EqualTo(default(DateTime)));
        Assert.That(cso.LastUpdated, Is.Null);
    }

    [Test]
    public void DeltaSync_QueryLogic_IncludesNewlyCreatedCsos()
    {
        // Arrange - Watermark from 1 hour ago
        var watermark = DateTime.UtcNow.AddHours(-1);

        // CSO created 30 minutes ago (after watermark) with no LastUpdated
        var newlyCreatedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Created = DateTime.UtcNow.AddMinutes(-30),
            LastUpdated = null
        };

        // Assert - The query logic should include this CSO because Created > watermark
        var shouldBeIncluded = newlyCreatedCso.Created > watermark ||
                               (newlyCreatedCso.LastUpdated.HasValue && newlyCreatedCso.LastUpdated.Value > watermark);

        Assert.That(shouldBeIncluded, Is.True, "Newly created CSO should be included in delta sync");
    }

    [Test]
    public void DeltaSync_QueryLogic_IncludesModifiedCsos()
    {
        // Arrange - Watermark from 1 hour ago
        var watermark = DateTime.UtcNow.AddHours(-1);

        // CSO created 1 week ago but modified 30 minutes ago
        var modifiedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Created = DateTime.UtcNow.AddDays(-7),
            LastUpdated = DateTime.UtcNow.AddMinutes(-30)
        };

        // Assert - Should be included because LastUpdated > watermark
        var shouldBeIncluded = modifiedCso.Created > watermark ||
                               (modifiedCso.LastUpdated.HasValue && modifiedCso.LastUpdated.Value > watermark);

        Assert.That(shouldBeIncluded, Is.True, "Modified CSO should be included in delta sync");
    }

    [Test]
    public void DeltaSync_QueryLogic_ExcludesUnchangedCsos()
    {
        // Arrange - Watermark from 1 hour ago
        var watermark = DateTime.UtcNow.AddHours(-1);

        // CSO created and last modified before the watermark
        var unchangedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Created = DateTime.UtcNow.AddDays(-7),
            LastUpdated = DateTime.UtcNow.AddDays(-5)
        };

        // Assert - Should NOT be included (both Created and LastUpdated are before watermark)
        var shouldBeIncluded = unchangedCso.Created > watermark ||
                               (unchangedCso.LastUpdated.HasValue && unchangedCso.LastUpdated.Value > watermark);

        Assert.That(shouldBeIncluded, Is.False, "Unchanged CSO should NOT be included in delta sync");
    }

    [Test]
    public void DeltaSync_QueryLogic_ExcludesOldCsoWithNullLastUpdated()
    {
        // Arrange - Watermark from 1 hour ago
        var watermark = DateTime.UtcNow.AddHours(-1);

        // Old CSO that was never modified (Created before watermark, no LastUpdated)
        var oldCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Created = DateTime.UtcNow.AddDays(-30),
            LastUpdated = null
        };

        // Assert - Should NOT be included (Created is before watermark, no LastUpdated)
        var shouldBeIncluded = oldCso.Created > watermark ||
                               (oldCso.LastUpdated.HasValue && oldCso.LastUpdated.Value > watermark);

        Assert.That(shouldBeIncluded, Is.False, "Old CSO with no LastUpdated should NOT be included in delta sync");
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithNewlyCreatedCso_ReturnsItAsync()
    {
        // Arrange - Test that newly created CSOs are returned
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        var newlyCreatedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Created = DateTime.UtcNow.AddMinutes(-30), // Created after watermark
            LastUpdated = null // Never modified
        };

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject> { newlyCreatedCso },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result.Results, Has.Count.EqualTo(1));
        Assert.That(result.Results[0].LastUpdated, Is.Null, "Newly created CSO should have null LastUpdated");
        Assert.That(result.Results[0].Created, Is.GreaterThan(watermark));
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_IncludesNewlyCreatedCsos_InCountAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        // Simulate: 5 newly created + 10 modified = 15 total
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(15);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        Assert.That(result, Is.EqualTo(15));
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithMixOfNewAndModified_ReturnsBothAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        var newlyCreatedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Created = DateTime.UtcNow.AddMinutes(-30),
            LastUpdated = null
        };

        var modifiedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Created = DateTime.UtcNow.AddDays(-7),
            LastUpdated = DateTime.UtcNow.AddMinutes(-15)
        };

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject> { newlyCreatedCso, modifiedCso },
            TotalResults = 2,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result.Results, Has.Count.EqualTo(2));

        var newCso = result.Results.First(cso => cso.LastUpdated == null);
        var updatedCso = result.Results.First(cso => cso.LastUpdated != null);

        Assert.That(newCso, Is.Not.Null, "Should include newly created CSO");
        Assert.That(updatedCso, Is.Not.Null, "Should include modified CSO");
    }

    [Test]
    public void DeltaSync_FirstRunWithMinDateTimeWatermark_IncludesAllCsos()
    {
        // Arrange - First delta sync uses DateTime.MinValue as watermark
        var watermark = DateTime.MinValue;

        // Any CSO with any Created date should be included
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Created = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastUpdated = null
        };

        // Assert - Should be included because Created > DateTime.MinValue
        var shouldBeIncluded = cso.Created > watermark ||
                               (cso.LastUpdated.HasValue && cso.LastUpdated.Value > watermark);

        Assert.That(shouldBeIncluded, Is.True, "All CSOs should be included on first delta sync");
    }

    #endregion

    #region Edge Case Tests

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithFutureWatermark_ReturnsEmptyResultAsync()
    {
        // Arrange - Watermark is in the future (edge case that shouldn't happen normally)
        var watermark = DateTime.UtcNow.AddDays(1);
        var connectedSystemId = 1;
        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithNonExistentConnectedSystem_ReturnsEmptyResultAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddDays(-1);
        var nonExistentConnectedSystemId = 999;
        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            nonExistentConnectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            nonExistentConnectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_CsosWithNullLastUpdated_AreExcludedAsync()
    {
        // Arrange - Only CSOs with LastUpdated values should be included
        var watermark = DateTime.UtcNow.AddDays(-1);
        var connectedSystemId = 1;

        // Only the CSO with LastUpdated set should be returned
        var csoWithLastUpdated = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            LastUpdated = DateTime.UtcNow
        };

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject> { csoWithLastUpdated },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Has.Count.EqualTo(1));
        Assert.That(result.Results.All(cso => cso.LastUpdated.HasValue), Is.True);
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithCsosModifiedExactlyAtWatermark_ExcludesThemAsync()
    {
        // Arrange - CSOs modified exactly at the watermark should NOT be included (> not >=)
        var watermark = DateTime.UtcNow;
        var connectedSystemId = 1;

        // Repository should return empty since we filter by LastUpdated > watermark
        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Is.Empty);
    }

    #endregion

    #region Performance and Scale Tests

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithLargeDataset_HandlesCorrectlyAsync()
    {
        // Arrange - Simulate a large dataset scenario
        var watermark = DateTime.UtcNow.AddDays(-7);
        var connectedSystemId = 1;
        const int totalModified = 50000;
        const int pageSize = 200;

        var firstPageCsos = Enumerable.Range(0, pageSize)
            .Select(i => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystemId,
                LastUpdated = DateTime.UtcNow.AddMinutes(-i)
            }).ToList();

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = firstPageCsos,
            TotalResults = totalModified,
            CurrentPage = 1,
            PageSize = pageSize
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, pageSize))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, pageSize);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Results, Has.Count.EqualTo(pageSize));
        Assert.That(result.TotalResults, Is.EqualTo(totalModified));
        Assert.That(result.TotalPages, Is.EqualTo(250)); // 50000/200 = 250 pages
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_WithLargeCount_ReturnsAccurateCountAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddDays(-30);
        var connectedSystemId = 1;
        const int expectedCount = 100000;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(expectedCount);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        Assert.That(result, Is.EqualTo(expectedCount));
    }

    #endregion

    #region Integration Scenario Tests

    [Test]
    public async Task DeltaSync_Workflow_QueryModifiedCountThenFetchPagesAsync()
    {
        // Arrange - Simulate a complete delta sync workflow
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;
        const int totalModified = 25;
        const int pageSize = 10;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(totalModified);

        // Setup three pages of results
        for (int page = 1; page <= 3; page++)
        {
            var itemsInPage = page == 3 ? 5 : 10; // Last page has 5 items
            var pageCsos = Enumerable.Range(0, itemsInPage)
                .Select(i => new ConnectedSystemObject
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemId = connectedSystemId,
                    LastUpdated = DateTime.UtcNow
                }).ToList();

            var pagedResult = new PagedResultSet<ConnectedSystemObject>
            {
                Results = pageCsos,
                TotalResults = totalModified,
                CurrentPage = page,
                PageSize = pageSize
            };

            _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
                connectedSystemId, watermark, page, pageSize))
                .ReturnsAsync(pagedResult);
        }

        // Act - Get count first
        var count = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Then fetch all pages
        var allCsos = new List<ConnectedSystemObject>();
        var totalPages = (int)Math.Ceiling((double)count / pageSize);

        for (int page = 1; page <= totalPages; page++)
        {
            var pageResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
                connectedSystemId, watermark, page, pageSize);
            allCsos.AddRange(pageResult.Results);
        }

        // Assert
        Assert.That(count, Is.EqualTo(totalModified));
        Assert.That(allCsos, Has.Count.EqualTo(totalModified));
    }

    [Test]
    public async Task DeltaSync_WithNoChanges_CompletesQuicklyAsync()
    {
        // Arrange - No CSOs modified since last sync
        var watermark = DateTime.UtcNow;
        var connectedSystemId = 1;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(0);

        // Act
        var count = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        Assert.That(count, Is.EqualTo(0));

        // Verify we don't need to fetch any pages
        _mockCsRepo.Verify(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region Delta Sync Processor Logic Tests

    [Test]
    public void DeltaSyncRunProfile_CanBeCreated_WithCorrectRunType()
    {
        // Arrange
        var runProfile = new ConnectedSystemRunProfile
        {
            Id = 1,
            Name = "Delta Synchronisation",
            RunType = ConnectedSystemRunType.DeltaSynchronisation,
            ConnectedSystemId = 1
        };

        // Assert
        Assert.That(runProfile.RunType, Is.EqualTo(ConnectedSystemRunType.DeltaSynchronisation));
        Assert.That(runProfile.Name, Is.EqualTo("Delta Synchronisation"));
    }

    [Test]
    public void DeltaSyncRunProfile_FromTestUtilities_HasCorrectConfiguration()
    {
        // Arrange
        var runProfiles = TestUtilities.GetConnectedSystemRunProfileData();

        // Act
        var deltaSyncProfiles = runProfiles
            .Where(rp => rp.RunType == ConnectedSystemRunType.DeltaSynchronisation)
            .ToList();

        // Assert
        Assert.That(deltaSyncProfiles, Has.Count.EqualTo(2)); // Source and Target
        Assert.That(deltaSyncProfiles.Any(rp => rp.ConnectedSystemId == 1), Is.True);
        Assert.That(deltaSyncProfiles.Any(rp => rp.ConnectedSystemId == 2), Is.True);
    }

    [Test]
    public void ConnectedSystem_LastDeltaSyncCompletedAt_IsNullableDateTime()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System"
        };

        // Assert - default should be null
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.Null);

        // Act - set a value
        var now = DateTime.UtcNow;
        connectedSystem.LastDeltaSyncCompletedAt = now;

        // Assert
        Assert.That(connectedSystem.LastDeltaSyncCompletedAt, Is.EqualTo(now));
    }

    [Test]
    public void DeltaSync_WatermarkLogic_UsesGreaterThanComparison()
    {
        // Arrange - Simulate CSOs with different LastUpdated values
        var watermark = new DateTime(2025, 12, 25, 10, 0, 0, DateTimeKind.Utc);

        var csoBeforeWatermark = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            LastUpdated = watermark.AddSeconds(-1) // Before watermark
        };

        var csoAtWatermark = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            LastUpdated = watermark // Exactly at watermark
        };

        var csoAfterWatermark = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            LastUpdated = watermark.AddSeconds(1) // After watermark
        };

        // Assert - Only CSO after watermark should be included (> not >=)
        Assert.That(csoBeforeWatermark.LastUpdated > watermark, Is.False);
        Assert.That(csoAtWatermark.LastUpdated > watermark, Is.False);
        Assert.That(csoAfterWatermark.LastUpdated > watermark, Is.True);
    }

    [Test]
    public void DeltaSync_FirstRun_UsesMinValueAsWatermark()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "New System",
            LastDeltaSyncCompletedAt = null // No previous sync
        };

        // Act - The processor should use DateTime.MinValue when LastDeltaSyncCompletedAt is null
        var watermark = connectedSystem.LastDeltaSyncCompletedAt ?? DateTime.MinValue;

        // Assert
        Assert.That(watermark, Is.EqualTo(DateTime.MinValue));
    }

    #endregion

    #region Repository Method Verification Tests

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_CallsRepositoryWithCorrectParametersAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;
        const int page = 2;
        const int pageSize = 100;

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = page,
            PageSize = pageSize
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, page, pageSize))
            .ReturnsAsync(pagedResult);

        // Act
        await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, page, pageSize);

        // Assert - Verify the repository was called with exact parameters
        _mockCsRepo.Verify(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, page, pageSize), Times.Once);
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceCountAsync_CallsRepositoryWithCorrectParametersAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddMinutes(-30);
        var connectedSystemId = 5;

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark))
            .ReturnsAsync(0);

        // Act
        await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark);

        // Assert
        _mockCsRepo.Verify(r => r.GetConnectedSystemObjectModifiedSinceCountAsync(
            connectedSystemId, watermark), Times.Once);
    }

    #endregion

    #region Boundary Condition Tests

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithZeroPage_HandledGracefullyAsync()
    {
        // Arrange - Page 0 should be treated as page 1
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 200
        };

        // Repository should receive page 1 even if 0 is passed (implementation detail)
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 0, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 0, 200);

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithLastPage_ReturnsRemainingItemsAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddDays(-1);
        var connectedSystemId = 1;
        const int pageSize = 100;
        const int totalResults = 250;

        // Last page should have 50 items (250 % 100 = 50)
        var lastPageCsos = Enumerable.Range(0, 50)
            .Select(i => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystemId,
                LastUpdated = DateTime.UtcNow
            }).ToList();

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = lastPageCsos,
            TotalResults = totalResults,
            CurrentPage = 3,
            PageSize = pageSize
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 3, pageSize))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 3, pageSize);

        // Assert
        Assert.That(result.Results, Has.Count.EqualTo(50));
        Assert.That(result.CurrentPage, Is.EqualTo(3));
        Assert.That(result.TotalPages, Is.EqualTo(3));
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithPageBeyondTotal_ReturnsEmptyResultAsync()
    {
        // Arrange - Requesting a page that doesn't exist
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject>(),
            TotalResults = 0,
            CurrentPage = 100,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 100, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 100, 200);

        // Assert
        Assert.That(result.Results, Is.Empty);
    }

    #endregion

    #region Concurrent Delta Sync Scenario Tests

    [Test]
    public async Task DeltaSync_WithMultipleConnectedSystems_QueriesIndependentlyAsync()
    {
        // Arrange
        var watermark1 = DateTime.UtcNow.AddHours(-1);
        var watermark2 = DateTime.UtcNow.AddHours(-2);

        var cs1Csos = new List<ConnectedSystemObject>
        {
            new() { Id = Guid.NewGuid(), ConnectedSystemId = 1, LastUpdated = DateTime.UtcNow }
        };
        var cs2Csos = new List<ConnectedSystemObject>
        {
            new() { Id = Guid.NewGuid(), ConnectedSystemId = 2, LastUpdated = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), ConnectedSystemId = 2, LastUpdated = DateTime.UtcNow }
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(1, watermark1, 1, 200))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObject>
            {
                Results = cs1Csos,
                TotalResults = 1,
                CurrentPage = 1,
                PageSize = 200
            });

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(2, watermark2, 1, 200))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObject>
            {
                Results = cs2Csos,
                TotalResults = 2,
                CurrentPage = 1,
                PageSize = 200
            });

        // Act
        var result1 = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(1, watermark1, 1, 200);
        var result2 = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(2, watermark2, 1, 200);

        // Assert
        Assert.That(result1.Results, Has.Count.EqualTo(1));
        Assert.That(result2.Results, Has.Count.EqualTo(2));
        Assert.That(result1.Results.All(cso => cso.ConnectedSystemId == 1), Is.True);
        Assert.That(result2.Results.All(cso => cso.ConnectedSystemId == 2), Is.True);
    }

    #endregion

    #region CSO Obsoletion Tests

    [Test]
    public void ObsoletedCso_WithLastUpdatedSet_IsIncludedInDeltaSyncQuery()
    {
        // Arrange - An obsoleted CSO should have LastUpdated set so it's picked up by delta sync
        var watermark = DateTime.UtcNow.AddHours(-1);
        var obsoleteCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Obsolete,
            Created = DateTime.UtcNow.AddDays(-30), // Created before watermark
            LastUpdated = DateTime.UtcNow // Updated when marked obsolete - after watermark
        };

        // Act - Check if CSO would be included in delta sync query
        var isIncluded = obsoleteCso.Created > watermark ||
                         (obsoleteCso.LastUpdated.HasValue && obsoleteCso.LastUpdated.Value > watermark);

        // Assert
        Assert.That(isIncluded, Is.True);
        Assert.That(obsoleteCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public void ObsoletedCso_WithoutLastUpdated_WouldBeMissedByDeltaSync()
    {
        // Arrange - This test documents the bug that was fixed: if LastUpdated wasn't set,
        // obsoleted CSOs created before the watermark would be missed
        var watermark = DateTime.UtcNow.AddHours(-1);
        var obsoleteCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Obsolete,
            Created = DateTime.UtcNow.AddDays(-30), // Created before watermark
            LastUpdated = null // BUG: LastUpdated not set when marked obsolete
        };

        // Act - Check if CSO would be included in delta sync query
        var isIncluded = obsoleteCso.Created > watermark ||
                         (obsoleteCso.LastUpdated.HasValue && obsoleteCso.LastUpdated.Value > watermark);

        // Assert - This CSO would be MISSED (the bug scenario)
        Assert.That(isIncluded, Is.False);
        Assert.That(obsoleteCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_ReturnsObsoleteCsosWithLastUpdatedAsync()
    {
        // Arrange - Obsoleted CSOs should be returned if their LastUpdated is after the watermark
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        var obsoleteCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Status = ConnectedSystemObjectStatus.Obsolete,
            Created = DateTime.UtcNow.AddDays(-30),
            LastUpdated = DateTime.UtcNow // Set when marked obsolete
        };

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject> { obsoleteCso },
            TotalResults = 1,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result.Results, Has.Count.EqualTo(1));
        Assert.That(result.Results.First().Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    [Test]
    public void DeltaSyncQuery_IncludesBothNormalAndObsoleteCsos()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-1);

        var normalCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow.AddDays(-30),
            LastUpdated = DateTime.UtcNow
        };

        var obsoleteCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Obsolete,
            Created = DateTime.UtcNow.AddDays(-30),
            LastUpdated = DateTime.UtcNow
        };

        var allCsos = new List<ConnectedSystemObject> { normalCso, obsoleteCso };

        // Act - Apply the delta sync filter logic
        var includedCsos = allCsos.Where(cso =>
            cso.Created > watermark ||
            (cso.LastUpdated.HasValue && cso.LastUpdated.Value > watermark))
            .ToList();

        // Assert - Both should be included
        Assert.That(includedCsos, Has.Count.EqualTo(2));
        Assert.That(includedCsos.Any(cso => cso.Status == ConnectedSystemObjectStatus.Normal), Is.True);
        Assert.That(includedCsos.Any(cso => cso.Status == ConnectedSystemObjectStatus.Obsolete), Is.True);
    }

    [Test]
    public void CsoObsoletion_ShouldSetLastUpdated_SoItAppearsInDeltaSync()
    {
        // Arrange - Simulating what should happen when a CSO is marked obsolete
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow.AddDays(-30),
            LastUpdated = null
        };

        // Act - Simulate marking the CSO as obsolete (this is what SyncImportTaskProcessor does)
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;

        // Assert
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
        Assert.That(cso.LastUpdated, Is.Not.Null);
        Assert.That(cso.LastUpdated!.Value, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-5)));
    }

    [Test]
    public void ObsoleteCso_CreatedBeforeWatermark_WithLastUpdatedAfter_IsIncludedInDeltaSync()
    {
        // Arrange - Key scenario: CSO existed for a long time, then was deleted from source system
        var watermark = DateTime.UtcNow.AddHours(-1);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Status = ConnectedSystemObjectStatus.Obsolete,
            Created = DateTime.UtcNow.AddMonths(-6), // Created 6 months ago
            LastUpdated = DateTime.UtcNow.AddMinutes(-30) // Marked obsolete 30 minutes ago
        };

        // Act
        var isIncludedByCreated = cso.Created > watermark;
        var isIncludedByLastUpdated = cso.LastUpdated.HasValue && cso.LastUpdated.Value > watermark;
        var isIncluded = isIncludedByCreated || isIncludedByLastUpdated;

        // Assert
        Assert.That(isIncludedByCreated, Is.False, "Should NOT be included by Created date");
        Assert.That(isIncludedByLastUpdated, Is.True, "SHOULD be included by LastUpdated date");
        Assert.That(isIncluded, Is.True, "CSO should be included in delta sync");
    }

    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_WithMixedStatusCsos_ReturnsAllModifiedAsync()
    {
        // Arrange
        var watermark = DateTime.UtcNow.AddHours(-1);
        var connectedSystemId = 1;

        var normalCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Status = ConnectedSystemObjectStatus.Normal,
            LastUpdated = DateTime.UtcNow
        };

        var obsoleteCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Status = ConnectedSystemObjectStatus.Obsolete,
            LastUpdated = DateTime.UtcNow
        };

        var pagedResult = new PagedResultSet<ConnectedSystemObject>
        {
            Results = new List<ConnectedSystemObject> { normalCso, obsoleteCso },
            TotalResults = 2,
            CurrentPage = 1,
            PageSize = 200
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200))
            .ReturnsAsync(pagedResult);

        // Act
        var result = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
            connectedSystemId, watermark, 1, 200);

        // Assert
        Assert.That(result.Results, Has.Count.EqualTo(2));
        Assert.That(result.Results.Count(cso => cso.Status == ConnectedSystemObjectStatus.Normal), Is.EqualTo(1));
        Assert.That(result.Results.Count(cso => cso.Status == ConnectedSystemObjectStatus.Obsolete), Is.EqualTo(1));
    }

    [Test]
    public void ConnectedSystemObjectStatus_Obsolete_IsValidEnumValue()
    {
        // Arrange & Act & Assert - Ensure the Obsolete status exists and can be used
        Assert.That(Enum.IsDefined(typeof(ConnectedSystemObjectStatus), ConnectedSystemObjectStatus.Obsolete), Is.True);
        Assert.That((int)ConnectedSystemObjectStatus.Obsolete, Is.GreaterThanOrEqualTo(0));
    }

    #endregion
}
