using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for ActivityRepository.BulkInsertRpeisAsync - the raw SQL bulk insert method for RPEIs.
/// Since unit tests use a mocked DbContext, the raw SQL path throws and falls back to EF AddRange.
/// These tests verify the fallback path and correct ID pre-generation behaviour.
/// </summary>
[TestFixture]
public class BulkInsertRpeisTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<ActivityRunProfileExecutionItem> _rpeiData = null!;
    private Mock<DbSet<ActivityRunProfileExecutionItem>> _mockDbSetRpeis = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _rpeiData = new List<ActivityRunProfileExecutionItem>();
        SetupMockDbContext();
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
    }

    private void SetupMockDbContext()
    {
        _mockDbSetRpeis = _rpeiData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.ActivityRunProfileExecutionItems).Returns(_mockDbSetRpeis.Object);
        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_EmptyList_NoErrorAsync()
    {
        // Arrange
        var rpeis = new List<ActivityRunProfileExecutionItem>();

        // Act & Assert - should complete without error
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_SingleRpei_PersistsCorrectlyAsync()
    {
        // Arrange
        var activityId = Guid.NewGuid();
        var csoId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Added,
            ConnectedSystemObjectId = csoId,
            ExternalIdSnapshot = "ext-id-001"
        };
        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - ID should be pre-generated (no longer Guid.Empty)
        Assert.That(rpei.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(rpei.ActivityId, Is.EqualTo(activityId));
        Assert.That(rpei.ObjectChangeType, Is.EqualTo(ObjectChangeType.Added));
        Assert.That(rpei.ConnectedSystemObjectId, Is.EqualTo(csoId));
        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("ext-id-001"));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_WithNullableFields_HandlesNullsCorrectlyAsync()
    {
        // Arrange - RPEI with all nullable fields set to null
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.NoChange,
            NoChangeReason = null,
            ConnectedSystemObjectId = null,
            ExternalIdSnapshot = null,
            DataSnapshot = null,
            ErrorType = null,
            ErrorMessage = null,
            ErrorStackTrace = null,
            AttributeFlowCount = null
        };
        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act & Assert - should handle nulls without error
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);
        Assert.That(rpei.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_WithAllFieldsPopulated_PersistsCorrectlyAsync()
    {
        // Arrange - RPEI with every field populated
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Joined,
            NoChangeReason = NoChangeReason.MvoNoAttributeChanges,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ExternalIdSnapshot = "user@example.com",
            DataSnapshot = "{\"key\": \"value\"}",
            ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError,
            ErrorMessage = "Something went wrong",
            ErrorStackTrace = "at SomeMethod() in SomeFile.cs:line 42",
            AttributeFlowCount = 5
        };
        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert
        Assert.That(rpei.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(rpei.NoChangeReason, Is.EqualTo(NoChangeReason.MvoNoAttributeChanges));
        Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
        Assert.That(rpei.AttributeFlowCount, Is.EqualTo(5));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_PreGeneratesIds_WhenEmptyAsync()
    {
        // Arrange - RPEIs with no ID set (Guid.Empty is the default)
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            new() { ActivityId = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Added },
            new() { ActivityId = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Updated },
            new() { ActivityId = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Deleted }
        };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - all IDs should be unique and non-empty
        var ids = rpeis.Select(r => r.Id).ToList();
        Assert.That(ids, Has.All.Not.EqualTo(Guid.Empty));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(3), "All IDs should be unique");
    }

    [Test]
    public async Task BulkInsertRpeisAsync_PreservesExistingIds_WhenSetAsync()
    {
        // Arrange - RPEI with a pre-set ID
        var existingId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = existingId,
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected
        };
        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - ID should remain unchanged
        Assert.That(rpei.Id, Is.EqualTo(existingId));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_MultipleRpeis_AllPersistedAsync()
    {
        // Arrange - mix of change types and error states
        var activityId = Guid.NewGuid();
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            new()
            {
                ActivityId = activityId,
                ObjectChangeType = ObjectChangeType.Added,
                ConnectedSystemObjectId = Guid.NewGuid(),
                ExternalIdSnapshot = "user1"
            },
            new()
            {
                ActivityId = activityId,
                ObjectChangeType = ObjectChangeType.Updated,
                ConnectedSystemObjectId = Guid.NewGuid(),
                ExternalIdSnapshot = "user2"
            },
            new()
            {
                ActivityId = activityId,
                ObjectChangeType = ObjectChangeType.Deleted,
                ConnectedSystemObjectId = Guid.NewGuid(),
                ExternalIdSnapshot = "user3",
                ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError,
                ErrorMessage = "Deletion failed"
            },
            new()
            {
                ActivityId = activityId,
                ObjectChangeType = ObjectChangeType.Joined,
                ConnectedSystemObjectId = Guid.NewGuid(),
                AttributeFlowCount = 3
            },
            new()
            {
                ActivityId = activityId,
                ObjectChangeType = ObjectChangeType.NoChange,
                NoChangeReason = NoChangeReason.CsoAlreadyCurrent,
                ConnectedSystemObjectId = Guid.NewGuid()
            }
        };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - all should have unique IDs
        var ids = rpeis.Select(r => r.Id).ToList();
        Assert.That(ids, Has.All.Not.EqualTo(Guid.Empty));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(5));
    }
}
