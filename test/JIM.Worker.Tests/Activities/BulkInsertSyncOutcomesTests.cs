using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for ActivityRepository.BulkInsertRpeisAsync when RPEIs contain SyncOutcome trees.
/// Since unit tests use a mocked DbContext, the raw SQL path throws and falls back to EF AddRange.
/// These tests verify that outcomes are correctly flattened, ID-generated, and FK-linked.
/// </summary>
[TestFixture]
public class BulkInsertSyncOutcomesTests
{
    private Mock<JimDbContext> _mockDbContext = null!;
    private List<ActivityRunProfileExecutionItem> _rpeiData = null!;
    private List<ActivityRunProfileExecutionItemSyncOutcome> _outcomeData = null!;
    private Mock<DbSet<ActivityRunProfileExecutionItem>> _mockDbSetRpeis = null!;
    private Mock<DbSet<ActivityRunProfileExecutionItemSyncOutcome>> _mockDbSetOutcomes = null!;
    private PostgresDataRepository _repository = null!;
    private List<object> _addedEntities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _rpeiData = new List<ActivityRunProfileExecutionItem>();
        _outcomeData = new List<ActivityRunProfileExecutionItemSyncOutcome>();
        _addedEntities = new List<object>();
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
        _mockDbSetOutcomes = _outcomeData.BuildMockDbSet();
        _mockDbContext = new Mock<JimDbContext>();
        _mockDbContext.Setup(m => m.ActivityRunProfileExecutionItems).Returns(_mockDbSetRpeis.Object);
        _mockDbContext.Setup(m => m.ActivityRunProfileExecutionItemSyncOutcomes).Returns(_mockDbSetOutcomes.Object);

        // Capture AddRange calls to verify outcomes are tracked
        _mockDbContext.Setup(m => m.AddRange(It.IsAny<IEnumerable<object>>()))
            .Callback<IEnumerable<object>>(entities => _addedEntities.AddRange(entities));

        _repository = new PostgresDataRepository(_mockDbContext.Object);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_RpeiWithNoOutcomes_NoOutcomesAddedAsync()
    {
        // Arrange
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Added
        };
        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - no outcomes should have been added
        var outcomeEntities = _addedEntities.OfType<ActivityRunProfileExecutionItemSyncOutcome>().ToList();
        Assert.That(outcomeEntities, Is.Empty);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_RpeiWithSingleOutcome_OutcomeIdsPreGeneratedAsync()
    {
        // Arrange
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected,
            OutcomeSummary = "Projected:1"
        };

        rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            TargetEntityDescription = "John Smith",
            Ordinal = 0
        });

        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - outcome should have ID pre-generated and FK set
        var outcome = rpei.SyncOutcomes[0];
        Assert.That(outcome.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(outcome.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(outcome.ParentSyncOutcomeId, Is.Null, "Root outcome should have no parent");
    }

    [Test]
    public async Task BulkInsertRpeisAsync_RpeiWithNestedOutcomes_FksSetCorrectlyAsync()
    {
        // Arrange - Projected -> AttributeFlow -> PendingExportCreated x2
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected,
            OutcomeSummary = "Projected:1,AttributeFlow:12,PendingExportCreated:2"
        };

        var root = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            TargetEntityDescription = "John Smith",
            Ordinal = 0
        };

        var attrFlow = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            DetailCount = 12,
            Ordinal = 0
        };

        attrFlow.Children.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            TargetEntityDescription = "AD",
            Ordinal = 0
        });

        attrFlow.Children.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            TargetEntityDescription = "LDAP",
            Ordinal = 1
        });

        root.Children.Add(attrFlow);
        rpei.SyncOutcomes.Add(root);

        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - all outcomes should have IDs and correct FK references
        Assert.That(root.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(root.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(root.ParentSyncOutcomeId, Is.Null);

        Assert.That(attrFlow.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(attrFlow.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(attrFlow.ParentSyncOutcomeId, Is.EqualTo(root.Id));

        var pe1 = attrFlow.Children[0];
        var pe2 = attrFlow.Children[1];
        Assert.That(pe1.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(pe1.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(pe1.ParentSyncOutcomeId, Is.EqualTo(attrFlow.Id));

        Assert.That(pe2.Id, Is.Not.EqualTo(Guid.Empty));
        Assert.That(pe2.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(pe2.ParentSyncOutcomeId, Is.EqualTo(attrFlow.Id));

        // All IDs should be unique
        var allIds = new[] { root.Id, attrFlow.Id, pe1.Id, pe2.Id };
        Assert.That(allIds.Distinct().Count(), Is.EqualTo(4));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_MultipleRpeisWithOutcomes_AllOutcomesFkdCorrectlyAsync()
    {
        // Arrange - two RPEIs, each with their own outcomes
        var rpei1 = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected
        };
        rpei1.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            Ordinal = 0
        });

        var rpei2 = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Exported
        };
        rpei2.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            Ordinal = 0
        });

        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei1, rpei2 };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - each outcome FK points to its own RPEI
        Assert.That(rpei1.SyncOutcomes[0].ActivityRunProfileExecutionItemId, Is.EqualTo(rpei1.Id));
        Assert.That(rpei2.SyncOutcomes[0].ActivityRunProfileExecutionItemId, Is.EqualTo(rpei2.Id));
        Assert.That(rpei1.SyncOutcomes[0].Id, Is.Not.EqualTo(rpei2.SyncOutcomes[0].Id));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_RpeiWithOutcomeSummary_PersistsCorrectlyAsync()
    {
        // Arrange
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected,
            OutcomeSummary = "Projected:1,AttributeFlow:5"
        };

        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert
        Assert.That(rpei.OutcomeSummary, Is.EqualTo("Projected:1,AttributeFlow:5"));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_PreservesExistingOutcomeIds_WhenSetAsync()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Added
        };
        rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = existingId,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded,
            Ordinal = 0
        });

        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - pre-set ID should be preserved
        Assert.That(rpei.SyncOutcomes[0].Id, Is.EqualTo(existingId));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_OutcomesBuiltViaSyncOutcomeBuilder_NoDuplicatesAsync()
    {
        // Arrange - use SyncOutcomeBuilder which adds to BOTH parent.Children AND rpei.SyncOutcomes
        // This is the production pattern; FlattenSyncOutcomes must not double-visit children.
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Projected,
        };

        var root = SyncOutcomeBuilder.AddRootOutcome(rpei,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            detailCount: 5);
        var attrFlow = SyncOutcomeBuilder.AddChildOutcome(rpei, root,
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            detailCount: 5);
        SyncOutcomeBuilder.AddChildOutcome(rpei, attrFlow,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            targetEntityDescription: "AD");
        SyncOutcomeBuilder.AddChildOutcome(rpei, attrFlow,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            targetEntityDescription: "LDAP");
        SyncOutcomeBuilder.BuildOutcomeSummary(rpei);

        var rpeis = new List<ActivityRunProfileExecutionItem> { rpei };

        // Act - should not throw due to duplicate outcome IDs
        await _repository.Activity.BulkInsertRpeisAsync(rpeis);

        // Assert - all 4 outcomes should have unique IDs and correct FKs
        var allOutcomes = rpei.SyncOutcomes;
        Assert.That(allOutcomes, Has.Count.EqualTo(4));
        Assert.That(allOutcomes.Select(o => o.Id).Distinct().Count(), Is.EqualTo(4),
            "All outcome IDs must be unique");
        Assert.That(allOutcomes.All(o => o.Id != Guid.Empty), Is.True,
            "All outcome IDs must be pre-generated");
        Assert.That(allOutcomes.All(o => o.ActivityRunProfileExecutionItemId == rpei.Id), Is.True,
            "All outcomes must reference their RPEI");
    }
}
