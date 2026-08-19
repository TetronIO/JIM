// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Tests for SyncPreviewServer (#288 plan Phase 3): the per-object preview surface that composes the
/// inbound chain (scope, join or projection, Attribute Flow) with the Phase 2 evaluation-only outbound
/// path into a SyncPreviewResult, persisting nothing and claiming nothing.
/// </summary>
public class SyncPreviewServerTests
{
    private const int IncumbentRuleId = 101;
    private const string IncumbentEmployeeId = "EMP-HR";

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

    #region arrange helpers

    /// <summary>
    /// Arranges the outbound topology the Metaverse Object previews use: the seeded user export
    /// Synchronisation Rule repointed at the Dummy Source System with one direct Employee ID flow, a
    /// Metaverse Object carrying an Employee ID value, and (optionally) a JOINED Connected System Object
    /// in that system whose stored value for the flowed attribute the caller controls.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystem SourceSystem, SyncRule ExportRule,
        ConnectedSystemObjectTypeAttribute CsEmployeeIdAttr, ConnectedSystemObject Cso)
        ArrangeOutboundFixture(string? csoStoredEmployeeId)
    {
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = sourceSystem.Id;
        exportRule.ConnectedSystem = sourceSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.ObjectMatchingRules = new List<ObjectMatchingRule>();

        exportRule.AttributeFlowRules.Clear();
        var employeeIdMapping = new SyncRuleMapping
        {
            Id = 7101,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = csEmployeeIdAttr,
            TargetConnectedSystemAttributeId = csEmployeeIdAttr.Id
        };
        employeeIdMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7101,
            Order = 1,
            MetaverseAttribute = employeeIdAttr,
            MetaverseAttributeId = employeeIdAttr.Id
        });
        exportRule.AttributeFlowRules.Add(employeeIdMapping);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        SyncRepo.SeedConnectedSystemObject(cso);

        if (csoStoredEmployeeId != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = cso,
                Attribute = csEmployeeIdAttr,
                AttributeId = csEmployeeIdAttr.Id,
                StringValue = csoStoredEmployeeId
            });
        }

        SyncRepo.SeedMetaverseObject(mvo);
        return (mvo, sourceSystem, exportRule, csEmployeeIdAttr, cso);
    }

    /// <summary>
    /// Arranges the inbound topology the Connected System Object previews use: the seeded user import
    /// Synchronisation Rule given one direct Employee ID flow, and the seeded unjoined SOURCE_USER
    /// Connected System Object registered with the sync repository.
    /// </summary>
    private (ConnectedSystemObject Cso, SyncRule ImportRule, MetaverseAttribute MvEmployeeIdAttr)
        ArrangeInboundFixture()
    {
        var importRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Import Synchronisation Rule 1");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var sourceUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var mvEmployeeIdAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var csEmployeeIdAttr = sourceUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID);

        importRule.MetaverseObjectType = mvUserType;
        importRule.MetaverseObjectTypeId = mvUserType.Id;
        importRule.AttributeFlowRules.Clear();
        var mapping = new SyncRuleMapping
        {
            Id = 7201,
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7201,
            Order = 1,
            ConnectedSystemAttribute = csEmployeeIdAttr,
            ConnectedSystemAttributeId = csEmployeeIdAttr.Id
        });
        importRule.AttributeFlowRules.Add(mapping);

        var cso = ConnectedSystemObjectsData[0];
        SyncRepo.SeedConnectedSystemObject(cso);

        return (cso, importRule, mvEmployeeIdAttr);
    }

    #endregion

    #region PreviewSyncForMvoAsync

    [Test]
    public async Task PreviewSyncForMvoAsync_JoinedTargetMissingTheFlowedValue_ReportsAnOutboundUpdateAndPersistsNothingAsync()
    {
        // Arrange - a joined target object that does not yet hold the flowed value
        var (mvo, _, exportRule, csEmployeeIdAttr, _) = ArrangeOutboundFixture(csoStoredEmployeeId: null);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForMvoAsync(mvo.Id);

        // Assert - the composed outbound summary says one Update would be staged; nothing was persisted
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound, Is.Null, "An MVO preview has no inbound chain");
            Assert.That(result.Outbound.ObjectsToUpdate, Is.EqualTo(1));
            Assert.That(result.Outbound.ObjectsToCreate, Is.Zero);
            Assert.That(result.Outbound.TotalAttributeChanges, Is.EqualTo(1));
            Assert.That(result.Outbound.ProposedExports.Single().AttributeValueChanges
                .Any(avc => avc.AttributeId == csEmployeeIdAttr.Id && avc.StringValue == "EMP001"), Is.True,
                "The proposed export must carry the flowed value");
            Assert.That(result.OutboundDecisions.Entries, Has.Count.EqualTo(1),
                "The Phase 2 decision records must ride along");
            Assert.That(result.HasBlockingErrors, Is.False);
            Assert.That(result.AffectedSyncRules.Any(r => r.Id == exportRule.Id && r.Name == exportRule.Name), Is.True,
                "The participating export Synchronisation Rule must be reported");
            Assert.That(PendingExportsData, Is.Empty, "A preview must persist nothing");
        }

        // The outcome tree reports the staged Pending Export in the real tree's shape
        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.OutcomeTree[0].OutcomeType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
            Assert.That(result.OutcomeTree[0].DetailCount, Is.EqualTo(1));
            Assert.That(result.OutcomeTree[0].SyncRuleId, Is.EqualTo(exportRule.Id));
        }
    }

    [Test]
    public async Task PreviewSyncForMvoAsync_NoTargetPresence_ReportsProvisioningWithANestedPendingExportNodeAsync()
    {
        // Arrange - remove the joined target object so the provisioning path is taken
        var (mvo, _, exportRule, _, cso) = ArrangeOutboundFixture(csoStoredEmployeeId: null);
        SyncRepo.RemoveConnectedSystemObject(cso);
        var csoCountBefore = SyncRepo.ConnectedSystemObjectCount;

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForMvoAsync(mvo.Id);

        // Assert - one Create; the tree mirrors the real Provisioned -> Pending Export nesting
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outbound.ObjectsToCreate, Is.EqualTo(1));
            Assert.That(SyncRepo.ConnectedSystemObjectCount, Is.EqualTo(csoCountBefore),
                "A preview must not create a provisioning CSO");
        }

        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        var provisioned = result.OutcomeTree[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisioned.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned));
            Assert.That(provisioned.SyncRuleId, Is.EqualTo(exportRule.Id));
            Assert.That(provisioned.Children, Has.Count.EqualTo(1));
        }
        Assert.That(provisioned.Children[0].OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
    }

    [Test]
    public async Task PreviewSyncForMvoAsync_UnknownMetaverseObject_ReturnsObjectNotFoundErrorWithoutThrowingAsync()
    {
        // Act - an id nothing holds; an expected block returns in Errors rather than throwing (PRD requirement 5)
        var result = await Jim.SyncPreview.PreviewSyncForMvoAsync(Guid.NewGuid());

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasBlockingErrors, Is.True);
            Assert.That(result.Errors.Single().Code, Is.EqualTo(SyncPreviewMessageCode.ObjectNotFound));
            Assert.That(result.OutcomeTree, Is.Empty);
        }
    }

    #endregion

    #region PreviewSyncForCsoAsync

    [Test]
    public async Task PreviewSyncForCsoAsync_UnjoinedInScopeCsoWithProjectionEnabled_ReportsWouldProjectWithFlowsAndPersistsNothingAsync()
    {
        // Arrange - projection enabled on the import Synchronisation Rule; the CSO is unjoined
        var (cso, importRule, mvEmployeeIdAttr) = ArrangeInboundFixture();
        importRule.ProjectToMetaverse = true;
        var mvoCountBefore = MetaverseObjectsData.Count;

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the inbound summary says the CSO would project, with the Employee ID flow captured
        Assert.That(result.Inbound, Is.Not.Null, "A CSO preview must carry an inbound summary");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound!.WouldProject, Is.True);
            Assert.That(result.Inbound!.ProjectedMetaverseObjectTypeName, Is.EqualTo("User"));
            Assert.That(result.Inbound!.WouldJoinMetaverseObjectId, Is.Null);
            Assert.That(result.Inbound!.AlreadyJoinedMetaverseObjectId, Is.Null);
            Assert.That(result.Inbound!.AttributeFlowChanges.Any(c =>
                c.AttributeId == mvEmployeeIdAttr.Id && c.IsAddition && c.Value == "E123"), Is.True,
                "The Employee ID flow must be captured as an inbound attribute change");
            Assert.That(result.AffectedSyncRules.Any(r => r.Id == importRule.Id), Is.True);
            Assert.That(result.HasBlockingErrors, Is.False);
        }

        // The outcome tree mirrors the real Projected root with an Attribute Flow child
        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        var root = result.OutcomeTree[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
            Assert.That(root.SyncRuleId, Is.EqualTo(importRule.Id));
            Assert.That(root.Children.Any(c =>
                c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow), Is.True,
                "The projection root must carry an Attribute Flow child");
        }

        // Zero side effects: the CSO was not joined, and no Metaverse Object was created
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObject, Is.Null, "A preview must not join the CSO");
            Assert.That(cso.MetaverseObjectId, Is.Null);
            Assert.That(MetaverseObjectsData, Has.Count.EqualTo(mvoCountBefore));
            Assert.That(PendingExportsData, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_UnjoinedCsoMatchingAnExistingMvo_ReportsWouldJoinWithoutClaimingAsync()
    {
        // Arrange - a matching rule on Employee ID; the seeded MVO holds E123, as does the CSO
        var (cso, importRule, mvEmployeeIdAttr) = ArrangeInboundFixture();
        var existingMvo = MetaverseObjectsData[0];
        SyncRepo.SeedMetaverseObject(existingMvo);

        // A display name flow whose value differs ("Joe Bloggs" vs the MVO's "joe bloggs"), so the join
        // preview carries a genuine Attribute Flow; a joined object with no flows records no outcomes,
        // in the preview exactly as in a real synchronisation.
        var mvDisplayNameAttr = MetaverseObjectTypesData.Single(t => t.Name == "User")
            .Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var displayNameMapping = new SyncRuleMapping
        {
            Id = 7302,
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id
        };
        displayNameMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7302,
            Order = 1,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER")
                .Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.DISPLAY_NAME),
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.DISPLAY_NAME
        });
        importRule.AttributeFlowRules.Add(displayNameMapping);

        var objectMatchingRule = new ObjectMatchingRule
        {
            Id = 7301,
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id
        };
        objectMatchingRule.Sources.Add(new ObjectMatchingRuleSource
        {
            Id = 7301,
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.EMPLOYEE_ID,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER")
                .Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID)
        });
        importRule.ObjectMatchingRules.Add(objectMatchingRule);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the preview reports the join it would make, and the CSO remains unclaimed
        Assert.That(result.Inbound, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound!.WouldJoinMetaverseObjectId, Is.EqualTo(existingMvo.Id));
            Assert.That(result.Inbound!.WouldProject, Is.False);
            Assert.That(cso.MetaverseObjectId, Is.Null, "A preview must never claim the join");
            Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
        }

        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        Assert.That(result.OutcomeTree[0].OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Joined));
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_CsoOutOfScopeOfAllImportRules_ReportsOutOfScopeAndStopsTheChainAsync()
    {
        // Arrange - a scoping criterion the CSO does not satisfy
        var (cso, importRule, _) = ArrangeInboundFixture();
        importRule.ProjectToMetaverse = true;
        var scopingGroup = new SyncRuleScopingCriteriaGroup();
        scopingGroup.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER")
                .Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE),
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "an employee type this object does not have"
        });
        importRule.ObjectScopingCriteriaGroups.Add(scopingGroup);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the chain stops at scoping: an advisory message, no inbound flows, no outcome nodes
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Warnings.Any(w => w.Code == SyncPreviewMessageCode.OutOfScope), Is.True,
                "An out-of-scope Connected System Object must be reported as such");
            Assert.That(result.HasBlockingErrors, Is.False, "Out of scope is a normal outcome, not a blocker");
            Assert.That(result.Inbound, Is.Not.Null);
            Assert.That(result.Inbound!.WouldProject, Is.False);
            Assert.That(result.Inbound!.AttributeFlowChanges, Is.Empty);
            Assert.That(result.OutcomeTree, Is.Empty);
            Assert.That(cso.MetaverseObjectId, Is.Null);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_MultiValuedSourceToSingleValuedTarget_ReportsABlockingErrorAndStillReturnsAsync()
    {
        // Arrange - flow the multi-valued QUALIFICATIONS attribute at a single-valued Metaverse attribute
        var (cso, importRule, mvEmployeeIdAttr) = ArrangeInboundFixture();
        importRule.ProjectToMetaverse = true;
        var csQualificationsAttr = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER")
            .Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.QUALIFICATIONS);
        var badMapping = new SyncRuleMapping
        {
            Id = 7401,
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id
        };
        badMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7401,
            Order = 1,
            ConnectedSystemAttribute = csQualificationsAttr,
            ConnectedSystemAttributeId = csQualificationsAttr.Id
        });
        importRule.AttributeFlowRules.Clear();
        importRule.AttributeFlowRules.Add(badMapping);

        // Act - the preview still returns (PRD requirement 5); the violation lands in Errors
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasBlockingErrors, Is.True);
            Assert.That(result.Errors.Any(e => e.Code == SyncPreviewMessageCode.MultiValuedToSingleValuedFlow), Is.True,
                "The MVA to SVA violation must surface as a programmatic error code");
            Assert.That(result.Inbound, Is.Not.Null, "The rest of the preview still completes");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_UnknownCso_ReturnsObjectNotFoundErrorWithoutThrowingAsync()
    {
        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(1, Guid.NewGuid());

        // Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasBlockingErrors, Is.True);
            Assert.That(result.Errors.Single().Code, Is.EqualTo(SyncPreviewMessageCode.ObjectNotFound));
            Assert.That(result.OutcomeTree, Is.Empty);
        }
    }

    #endregion

    #region Attribute Priority

    /// <summary>
    /// Arranges the Attribute Priority topology a preview has to answer for: the Metaverse Object's Employee ID is
    /// owned by an authoritative import Synchronisation Rule on ANOTHER Connected System, and the rule being
    /// previewed contributes to the same attribute from this one.
    /// </summary>
    /// <param name="previewedRulePriority">The previewed rule's mapping priority (1 = highest).</param>
    /// <param name="incumbentRulePriority">The owning rule's mapping priority.</param>
    /// <param name="csoEmployeeId">The previewed object's source value, or null to contribute no value.</param>
    private (ConnectedSystemObject Cso, MetaverseObject Mvo, MetaverseAttribute MvEmployeeIdAttr)
        ArrangeAttributePriorityFixture(int previewedRulePriority, int incumbentRulePriority, string? csoEmployeeId)
    {
        var (cso, importRule, mvEmployeeIdAttr) = ArrangeInboundFixture();
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");

        // The previewed rule's mapping, carrying its persisted rule id: the gate reads it to identify the
        // contributor, and a mapping without one takes nothing over.
        var previewedMapping = importRule.AttributeFlowRules.Single();
        previewedMapping.SyncRuleId = importRule.Id;
        previewedMapping.Priority = previewedRulePriority;

        // The authoritative rule on another Connected System. Never evaluated by this preview (the context loads
        // only this system's rules), but a contributor to the same attribute, which is what makes the attribute
        // contested and the gate live.
        var incumbentRule = new SyncRule
        {
            Id = IncumbentRuleId,
            Name = "HR Import Synchronisation Rule",
            ConnectedSystemId = 2,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectTypeId = mvUserType.Id,
            MetaverseObjectType = mvUserType
        };
        incumbentRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 7301,
            SyncRule = incumbentRule,
            SyncRuleId = incumbentRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Priority = incumbentRulePriority
        });
        SyncRulesData.Add(incumbentRule);
        SyncRepo.SeedSyncRule(incumbentRule);

        // The Metaverse Object the previewed object is joined to, holding the incumbent's value with its
        // provenance stamped: what the gate compares an incoming contribution against.
        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = mvEmployeeIdAttr,
            AttributeId = mvEmployeeIdAttr.Id,
            StringValue = IncumbentEmployeeId,
            ContributedBySystemId = 2,
            ContributedBySyncRuleId = incumbentRule.Id
        });
        SyncRepo.SeedMetaverseObject(mvo);

        cso.MetaverseObjectId = mvo.Id;
        cso.MetaverseObject = mvo;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;

        var csEmployeeIdAttr = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER")
            .Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID);
        cso.AttributeValues.RemoveAll(av => av.AttributeId == csEmployeeIdAttr.Id);
        if (csoEmployeeId != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = cso,
                Attribute = csEmployeeIdAttr,
                AttributeId = csEmployeeIdAttr.Id,
                StringValue = csoEmployeeId
            });
        }

        return (cso, mvo, mvEmployeeIdAttr);
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ContributionLosesAttributePriority_ReportsNoFlowForThatAttributeAsync()
    {
        // The preview's whole promise is that it answers what the next synchronisation would do. A contribution
        // that loses priority resolution is refused by a real run, so reporting it as a flow tells an
        // administrator their edit takes effect when it does not, and hides that the attribute has an owner.
        var (cso, _, mvEmployeeIdAttr) = ArrangeAttributePriorityFixture(
            previewedRulePriority: 5, incumbentRulePriority: 1, csoEmployeeId: "EMP-LOSER");

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound, Is.Not.Null);
            Assert.That(result.Inbound!.AttributeFlowChanges.Any(c => c.AttributeId == mvEmployeeIdAttr.Id), Is.False,
                "A losing contribution must not be reported as a flow: a real synchronisation refuses it");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_WinningContributionHasNoValueBesideAnotherContributor_ReportsNoWithdrawalAsync()
    {
        // The other half of the gate, and the more alarming one to get wrong: without a priority context the
        // engine falls back to its historic clear, so the preview reports the identity LOSING its authoritative
        // Employee ID. A real run abstains and leaves the incumbent in place.
        var (cso, _, mvEmployeeIdAttr) = ArrangeAttributePriorityFixture(
            previewedRulePriority: 1, incumbentRulePriority: 5, csoEmployeeId: null);

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound, Is.Not.Null);
            Assert.That(result.Inbound!.AttributeFlowChanges.Any(c =>
                c.AttributeId == mvEmployeeIdAttr.Id && !c.IsAddition), Is.False,
                "A contribution with no value must abstain beside another contributor, not clear the attribute");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ContributionWinsAttributePriority_StillReportsTheFlowAsync()
    {
        // The gate must not be a blanket suppression: a winning contribution flows, and the preview says so.
        var (cso, _, mvEmployeeIdAttr) = ArrangeAttributePriorityFixture(
            previewedRulePriority: 1, incumbentRulePriority: 5, csoEmployeeId: "EMP-WINNER");

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        Assert.That(result.Inbound!.AttributeFlowChanges.Any(c =>
            c.AttributeId == mvEmployeeIdAttr.Id && c.IsAddition && c.Value == "EMP-WINNER"), Is.True,
            "The winning contribution is what the next synchronisation would write");
    }

    #endregion
}
