// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Tests for SyncPreviewServer.PreviewFullSyncAsync (#288 plan Phase 4, PRD decision D2): the full-system
/// preview tier, returning whole-population outcome counts plus a bounded per-category sample of full
/// trees, under an explicit work budget with truncation flagged, persisting nothing.
/// </summary>
public class FullSyncPreviewServerTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    #endregion

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        var syncRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Source System Full Sync");
        ActivitiesData = TestUtilities.GetActivityData(syncRunProfile.RunType, syncRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        SyncRepo = TestUtilities.CreateSyncRepository(
            activity: ActivitiesData.First(),
            syncRules: SyncRulesData);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    /// <summary>
    /// Seeds the two stock SOURCE_USER Connected System Objects into the sync repository, gives the user
    /// import Synchronisation Rule projection plus one direct Employee ID flow, and returns the pair.
    /// cso1 carries Employee ID E123, cso2 E124, so a scoping criterion on E123 splits them.
    /// </summary>
    private (ConnectedSystemObject Cso1, ConnectedSystemObject Cso2, SyncRule ImportRule) ArrangePopulationFixture()
    {
        var importRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Import Synchronisation Rule 1");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var sourceUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var mvEmployeeIdAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var csEmployeeIdAttr = sourceUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID);

        importRule.MetaverseObjectType = mvUserType;
        importRule.MetaverseObjectTypeId = mvUserType.Id;
        importRule.ProjectToMetaverse = true;
        importRule.AttributeFlowRules.Clear();
        var mapping = new SyncRuleMapping
        {
            Id = 7501,
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7501,
            Order = 1,
            ConnectedSystemAttribute = csEmployeeIdAttr,
            ConnectedSystemAttributeId = csEmployeeIdAttr.Id
        });
        importRule.AttributeFlowRules.Add(mapping);

        var cso1 = ConnectedSystemObjectsData[0];
        var cso2 = ConnectedSystemObjectsData[1];
        SyncRepo.SeedConnectedSystemObject(cso1);
        SyncRepo.SeedConnectedSystemObject(cso2);

        return (cso1, cso2, importRule);
    }

    [Test]
    public async Task PreviewFullSyncAsync_ProjectingPopulation_CountsEveryObjectAndSamplesFullTreesAsync()
    {
        // Arrange - both stock user objects would project
        var (cso1, cso2, _) = ArrangePopulationFixture();
        var mvoCountBefore = MetaverseObjectsData.Count;

        // Act
        var result = await Jim.SyncPreview.PreviewFullSyncAsync(cso1.ConnectedSystemId);

        // Assert - the count tier covers the whole population; the samples carry full trees
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalObjectCount, Is.EqualTo(2));
            Assert.That(result.EvaluatedObjectCount, Is.EqualTo(2));
            Assert.That(result.Truncated, Is.False);
            Assert.That(result.TruncationReason, Is.EqualTo(FullSyncPreviewTruncationReason.None));
            Assert.That(result.Counts.WouldProject, Is.EqualTo(2));
            Assert.That(result.Counts.OutOfScope, Is.Zero);
            Assert.That(result.Counts.BlockedByErrors, Is.Zero);
        }

        var projectionSamples = result.Samples
            .Where(s => s.Category == FullSyncPreviewCategory.WouldProject).ToList();
        Assert.That(projectionSamples, Has.Count.EqualTo(2), "Both projecting objects fit the default sample bound");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projectionSamples.Select(s => s.ConnectedSystemObjectId),
                Is.EquivalentTo(new[] { cso1.Id, cso2.Id }));
            Assert.That(projectionSamples.All(s => s.Preview.Inbound!.WouldProject), Is.True);
            Assert.That(projectionSamples.All(s => s.Preview.OutcomeTree.Count > 0), Is.True,
                "A sampled object carries its full speculative outcome tree");
        }

        // Zero side effects: nothing joined, created or staged
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso1.MetaverseObjectId, Is.Null);
            Assert.That(cso2.MetaverseObjectId, Is.Null);
            Assert.That(MetaverseObjectsData, Has.Count.EqualTo(mvoCountBefore));
            Assert.That(PendingExportsData, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_ScopedRuleSplitsThePopulation_CountsBothCategoriesAsync()
    {
        // Arrange - scope the rule to Employee ID E123: cso1 in scope, cso2 out
        var (cso1, _, importRule) = ArrangePopulationFixture();
        var sourceUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var scopingGroup = new SyncRuleScopingCriteriaGroup();
        scopingGroup.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = sourceUserType.Attributes
                .Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID),
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "E123"
        });
        importRule.ObjectScopingCriteriaGroups.Add(scopingGroup);

        // Act
        var result = await Jim.SyncPreview.PreviewFullSyncAsync(cso1.ConnectedSystemId);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Counts.WouldProject, Is.EqualTo(1));
            Assert.That(result.Counts.OutOfScope, Is.EqualTo(1));
            Assert.That(result.Samples.Count(s => s.Category == FullSyncPreviewCategory.OutOfScope), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_ObjectCapBelowPopulation_TruncatesAndSaysSoAsync()
    {
        // Arrange
        var (cso1, _, _) = ArrangePopulationFixture();

        // Act - a work budget of one object over a population of two
        var result = await Jim.SyncPreview.PreviewFullSyncAsync(cso1.ConnectedSystemId,
            new FullSyncPreviewOptions { MaxObjects = 1 });

        // Assert - the cap is honoured and the truncation is flagged (PRD requirement 14)
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalObjectCount, Is.EqualTo(2), "The population size is still reported in full");
            Assert.That(result.EvaluatedObjectCount, Is.EqualTo(1));
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TruncationReason, Is.EqualTo(FullSyncPreviewTruncationReason.ObjectCapReached));
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_TimeBudgetAlreadyExhausted_TruncatesWithoutEvaluatingAsync()
    {
        // Arrange
        var (cso1, _, _) = ArrangePopulationFixture();

        // Act - a zero time budget cannot cover any evaluation
        var result = await Jim.SyncPreview.PreviewFullSyncAsync(cso1.ConnectedSystemId,
            new FullSyncPreviewOptions { TimeBudget = TimeSpan.Zero });

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.EvaluatedObjectCount, Is.Zero);
            Assert.That(result.Truncated, Is.True);
            Assert.That(result.TruncationReason, Is.EqualTo(FullSyncPreviewTruncationReason.TimeBudgetExhausted));
            Assert.That(result.TotalObjectCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_SampleBoundBelowCategoryCount_CountsAllSamplesFewerAsync()
    {
        // Arrange
        var (cso1, _, _) = ArrangePopulationFixture();

        // Act - both objects project; only one tree is retained
        var result = await Jim.SyncPreview.PreviewFullSyncAsync(cso1.ConnectedSystemId,
            new FullSyncPreviewOptions { SampleTreesPerCategory = 1 });

        // Assert - the count tier still covers everything; the sample tier is bounded
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Counts.WouldProject, Is.EqualTo(2));
            Assert.That(result.Samples.Count(s => s.Category == FullSyncPreviewCategory.WouldProject), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_SystemWithNoObjects_ReturnsAnEmptyResultWithoutThrowingAsync()
    {
        // Act - Connected System 2 has no seeded objects
        var result = await Jim.SyncPreview.PreviewFullSyncAsync(2);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalObjectCount, Is.Zero);
            Assert.That(result.EvaluatedObjectCount, Is.Zero);
            Assert.That(result.Truncated, Is.False);
            Assert.That(result.Samples, Is.Empty);
        }
    }
}
