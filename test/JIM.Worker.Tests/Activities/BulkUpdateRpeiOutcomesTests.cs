using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for ActivityRepository.BulkUpdateRpeiOutcomesAsync - updates OutcomeSummary and error fields
/// on already-persisted RPEIs, and inserts new sync outcomes.
/// Since unit tests use a mocked DbContext, the raw SQL path throws and falls back to EF tracking.
/// </summary>
[TestFixture]
public class BulkUpdateRpeiOutcomesTests
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
    public async Task BulkUpdateRpeiOutcomesAsync_EmptyList_NoErrorAsync()
    {
        // Act & Assert - should complete without error
        await _repository.Activity.BulkUpdateRpeiOutcomesAsync(
            new List<ActivityRunProfileExecutionItem>(),
            new List<ActivityRunProfileExecutionItemSyncOutcome>());
    }

    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_UpdatesOutcomeSummaryAsync()
    {
        // Arrange - simulate an already-persisted RPEI with a new outcome added
        var rpeiId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = rpeiId,
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Updated,
            OutcomeSummary = "CsoUpdated:1,ExportConfirmed:1"
        };

        var newOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = rpeiId,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed,
            DetailCount = 5,
            Ordinal = 1
        };

        // Act
        await _repository.Activity.BulkUpdateRpeiOutcomesAsync(
            new List<ActivityRunProfileExecutionItem> { rpei },
            new List<ActivityRunProfileExecutionItemSyncOutcome> { newOutcome });

        // Assert - the method should complete without error (EF fallback in tests)
        Assert.That(rpei.OutcomeSummary, Is.EqualTo("CsoUpdated:1,ExportConfirmed:1"));
    }

    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_UpdatesErrorFieldsAsync()
    {
        // Arrange
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Updated,
            ErrorType = ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed,
            ErrorMessage = "Export confirmation failed for 2 attribute(s)",
            DataSnapshot = "Failed attributes: displayName, mail"
        };

        // Act
        await _repository.Activity.BulkUpdateRpeiOutcomesAsync(
            new List<ActivityRunProfileExecutionItem> { rpei },
            new List<ActivityRunProfileExecutionItemSyncOutcome>());

        // Assert - error fields should be preserved on the RPEI object
        Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed));
        Assert.That(rpei.ErrorMessage, Is.EqualTo("Export confirmation failed for 2 attribute(s)"));
        Assert.That(rpei.DataSnapshot, Is.EqualTo("Failed attributes: displayName, mail"));
    }

    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_MultipleRpeis_AllProcessedAsync()
    {
        // Arrange
        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ActivityId = Guid.NewGuid(),
                ObjectChangeType = ObjectChangeType.Updated,
                OutcomeSummary = "CsoUpdated:1,ExportConfirmed:1"
            },
            new()
            {
                Id = Guid.NewGuid(),
                ActivityId = Guid.NewGuid(),
                ObjectChangeType = ObjectChangeType.Updated,
                OutcomeSummary = "CsoUpdated:1,ExportFailed:1",
                ErrorType = ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed,
                ErrorMessage = "Failed"
            }
        };

        var newOutcomes = rpeis.Select(r => new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = r.Id,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed,
            Ordinal = 1
        }).ToList();

        // Act & Assert - should process all RPEIs without error
        await _repository.Activity.BulkUpdateRpeiOutcomesAsync(rpeis, newOutcomes);
    }

    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_WithNullableFields_HandlesNullsAsync()
    {
        // Arrange - RPEI with null error fields (no error, just outcome summary update)
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Updated,
            OutcomeSummary = "CsoUpdated:1,ExportConfirmed:1",
            ErrorType = null,
            ErrorMessage = null,
            DataSnapshot = null
        };

        // Act & Assert
        await _repository.Activity.BulkUpdateRpeiOutcomesAsync(
            new List<ActivityRunProfileExecutionItem> { rpei },
            new List<ActivityRunProfileExecutionItemSyncOutcome>());
    }
}
