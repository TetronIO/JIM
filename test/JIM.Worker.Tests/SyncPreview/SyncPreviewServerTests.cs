// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
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

    /// <summary>
    /// The generated-mapping counterpart of <see cref="ArrangeInboundFixture"/> (Unique Value Generation,
    /// #242): the same import Synchronisation Rule and target Metaverse attribute, but with a "JIM generates
    /// it" mapping (a <see cref="SyncRuleMappingGeneration"/> row, default settings: OnlyIfTaken) whose base
    /// Expression is <paramref name="baseExpression"/> against the seeded unjoined SOURCE_USER CSO.
    /// </summary>
    private (ConnectedSystemObject Cso, SyncRule ImportRule, MetaverseAttribute MvEmployeeIdAttr, SyncRuleMapping Mapping)
        ArrangeGeneratedInboundFixture(string baseExpression = "cs[\"EMPLOYEE_ID\"]")
    {
        var importRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Import Synchronisation Rule 1");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var mvEmployeeIdAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);

        importRule.MetaverseObjectType = mvUserType;
        importRule.MetaverseObjectTypeId = mvUserType.Id;
        importRule.AttributeFlowRules.Clear();
        var mapping = new SyncRuleMapping
        {
            Id = 7202,
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Generation = new SyncRuleMappingGeneration()
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7202,
            Order = 1,
            Expression = baseExpression
        });
        importRule.AttributeFlowRules.Add(mapping);

        var cso = ConnectedSystemObjectsData[0];
        SyncRepo.SeedConnectedSystemObject(cso);

        return (cso, importRule, mvEmployeeIdAttr, mapping);
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

    /// <summary>
    /// The scope-out cancellation, mirrored in the outbound-only preview: the Metaverse
    /// Object is out of the export Synchronisation Rule's scope, and its target Connected System Object is
    /// still Pending Provisioning with an unsent Create Pending Export (nothing was ever exported). The real
    /// run cancels that provisioning outright; the preview must report a ProvisioningCancelled node, not a
    /// Deprovision Queued one, and must propose no export.
    /// </summary>
    [Test]
    public async Task PreviewSyncForMvoAsync_TargetNeverExportedProvisioningOutOfScope_ReportsProvisioningCancelledNodeAsync()
    {
        // Arrange - a joined target object still Pending Provisioning, then scope the rule out. The scope-out
        // cancellation needs the unsent Create Pending Export itself as proof the CSO is persisted and safe to
        // remove; see ScopeOutCancelsNeverExportedProvisioning.
        var (mvo, _, exportRule, _, cso) = ArrangeOutboundFixture(csoStoredEmployeeId: null);
        cso.Status = ConnectedSystemObjectStatus.PendingProvisioning;
        var unsentCreate = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = cso.ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        PendingExportsData.Add(unsentCreate);
        SyncRepo.SeedPendingExport(unsentCreate);
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        var scopingGroup = new SyncRuleScopingCriteriaGroup();
        scopingGroup.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = MetaverseObjectTypesData.Single(t => t.Name == "User").Attributes
                .Single(a => a.Name == Constants.BuiltInAttributes.DisplayName),
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "a display name this Metaverse Object does not have"
        });
        exportRule.ObjectScopingCriteriaGroups.Add(scopingGroup);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForMvoAsync(mvo.Id);

        // Assert - a ProvisioningCancelled node, nothing proposed, and the CSO left exactly as it was
        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        var node = result.OutcomeTree[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled));
            Assert.That(node.SyncRuleId, Is.EqualTo(exportRule.Id));
            Assert.That(node.DetailMessage, Is.EqualTo(exportRule.ConnectedSystemId.ToString()));
            Assert.That(result.Outbound.ProposedExports, Is.Empty, "Nothing exists in the target system to export");
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(mvo.Id), "A preview must not disconnect the CSO");
            Assert.That(PendingExportsData, Has.Count.EqualTo(1), "A preview must not touch the seeded unsent Create");
        }
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
    public async Task PreviewSyncForCsoAsync_ProposedRuleSetRemovesTheOnlyImportRule_ReportsNothingWouldProcessTheObjectAsync()
    {
        // What disabling a Synchronisation Rule means, put to the engine (#1462). A disabled stand-in SUBSTITUTED
        // in would not achieve it: nothing downstream of the load re-checks Enabled, so the rule would go on being
        // evaluated and the preview would report that disabling it changes nothing.
        var (cso, importRule, _) = ArrangeInboundFixture();
        importRule.ProjectToMetaverse = true;

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id,
            proposedRuleSet: ProposedSyncRuleSet.Removing(importRule.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Warnings.Any(w => w.Code == SyncPreviewMessageCode.NoApplicableSyncRule), Is.True,
                "with its only import rule out of the set, nothing would process the object inbound");
            Assert.That(result.Inbound!.WouldProject, Is.False);
            Assert.That(cso.MetaverseObjectId, Is.Null);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ProposedRuleSetAddsARuleThatIsNotLoaded_EvaluatesItAsync()
    {
        // What enabling a disabled rule means. A substitution cannot express it: the rule is absent from the
        // loaded set, so there is nothing of that id to replace, and the preview would report no change.
        var (cso, importRule, mvEmployeeIdAttr) = ArrangeInboundFixture();
        importRule.ProjectToMetaverse = true;

        // Disable it, which is precisely how a rule leaves the loaded set: the repository reads with
        // includeDisabled false, so there is no rule of that id for a substitution to find.
        importRule.Enabled = false;

        var withoutTheRule = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);
        var withTheRule = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id,
            proposedRuleSet: ProposedSyncRuleSet.Adding(importRule));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutTheRule.Inbound!.WouldProject, Is.False, "nothing evaluates the object while the rule is out of the set");
            Assert.That(withTheRule.Inbound!.WouldProject, Is.True, "the added rule is evaluated, so the object would project");
            Assert.That(withTheRule.Inbound!.AttributeFlowChanges.Any(c => c.AttributeId == mvEmployeeIdAttr.Id), Is.True);
            Assert.That(cso.MetaverseObjectId, Is.Null, "and the preview still writes nothing");
        }
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

    #region Out-of-Scope Destructive Cascade (#288 Phase 1 of the Sync Preview Surface plan)

    /// <summary>
    /// Arranges a joined, out-of-scope Connected System Object whose Metaverse Object's type carries the
    /// given Deletion Rule settings: the seeded user import Synchronisation Rule gets a scoping criterion
    /// the object fails, and the object is joined directly (bypassing the join probe, which the preview's
    /// out-of-scope branch never reaches).
    /// </summary>
    private (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule ImportRule, ConnectedSystem SourceSystem)
        ArrangeOutOfScopeCascadeFixture(
            MetaverseObjectDeletionRule deletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            TimeSpan? gracePeriod = null)
    {
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");
        var sourceUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvUserType.DeletionRule = deletionRule;
        mvUserType.DeletionGracePeriod = gracePeriod;
        mvUserType.DeletionTriggerConnectedSystemIds = [];

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();

        var importRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Import Synchronisation Rule 1");
        importRule.MetaverseObjectType = mvUserType;
        importRule.MetaverseObjectTypeId = mvUserType.Id;
        importRule.ConnectedSystemId = sourceSystem.Id;
        importRule.ConnectedSystemObjectTypeId = sourceUserType.Id;
        importRule.ConnectedSystemObjectType = sourceUserType;
        importRule.Direction = SyncRuleDirection.Import;
        importRule.AttributeFlowRules.Clear();

        var csEmployeeIdAttr = sourceUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_ID);
        importRule.ObjectScopingCriteriaGroups.Clear();
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = csEmployeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "IN_SCOPE_VALUE"
                }
            }
        });

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceSystem.Id,
            ConnectedSystem = sourceSystem,
            Type = sourceUserType,
            TypeId = sourceUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            Attribute = csEmployeeIdAttr,
            AttributeId = csEmployeeIdAttr.Id,
            StringValue = "OUT_OF_SCOPE_VALUE" // fails the scoping criterion above
        });

        SyncRepo.SeedConnectedSystemObject(cso);
        SyncRepo.SeedMetaverseObject(mvo);

        return (cso, mvo, importRule, sourceSystem);
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithNoRemainingConnectors_ReportsMvoDeletedAndPersistsNothingAsync()
    {
        // Arrange - no other Connected System Object is joined to the Metaverse Object, so the last
        // connector disconnecting deletes it immediately (zero/null grace period).
        var (cso, mvo, _, _) = ArrangeOutOfScopeCascadeFixture(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var mvoCountBefore = MetaverseObjectsData.Count;

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the warning and the cascade's MvoDeleted node both appear
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Warnings.Any(w => w.Code == SyncPreviewMessageCode.OutOfScope), Is.True);
            Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
            Assert.That(result.OutcomeTree[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope));
            Assert.That(result.OutcomeTree[0].Children.Any(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted), Is.True);
        }

        // Zero side effects: the join is untouched and nothing was deleted
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(mvo.Id), "A preview must never break the join");
            Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvo.Id), Is.True, "A preview must never delete the Metaverse Object");
            Assert.That(MetaverseObjectsData, Has.Count.EqualTo(mvoCountBefore));
            Assert.That(PendingExportsData, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithAnotherConnectorJoined_StopsAtTheDisconnectRootAsync()
    {
        // Arrange - a second Connected System Object on the same system remains joined to the Metaverse
        // Object, so it is still a connector once the previewed object disconnects.
        var (cso, mvo, _, sourceSystem) = ArrangeOutOfScopeCascadeFixture(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var otherCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = sourceSystem.Id,
            ConnectedSystem = sourceSystem,
            Type = cso.Type,
            TypeId = cso.TypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        SyncRepo.SeedConnectedSystemObject(otherCso);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the cascade stops at the bare disconnect root; no deletion fate is recorded
        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        var root = result.OutcomeTree[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope));
            Assert.That(root.Children, Is.Empty, "No remaining-connector deletion fate to record when a connector remains");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithManualDeletionRule_StopsAtTheDisconnectRootAsync()
    {
        // Arrange - Deletion Rule Manual never fires, regardless of remaining connectors
        var (cso, _, _, _) = ArrangeOutOfScopeCascadeFixture(MetaverseObjectDeletionRule.Manual);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert
        Assert.That(result.OutcomeTree, Has.Count.EqualTo(1));
        Assert.That(result.OutcomeTree[0].Children, Is.Empty);
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithScheduledDeletion_ReportsMvoDeletionScheduledWithNoDownstreamNodesAsync()
    {
        // Arrange - a grace period schedules rather than immediately deletes
        var (cso, _, _, _) = ArrangeOutOfScopeCascadeFixture(
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: TimeSpan.FromDays(7));

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - scheduled, not immediate: no downstream deprovisioning is staged (mirrors
        // SyncTaskProcessorBase.FindMvoDeletedOutcomeNodes, which only nests deprovisioning under MvoDeleted)
        var deletionNode = result.OutcomeTree[0].Children.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deletionNode.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled));
            Assert.That(deletionNode.Children, Is.Empty);
            Assert.That(result.Outbound.ProposedExports, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithDownstreamDisconnectOnlyTarget_ReportsWarningAndNoTreeNodeAsync()
    {
        // Arrange - a downstream target CSO whose export rule's Outbound Deprovision Action is Disconnect
        // (the default): the real run would disconnect it without deprovisioning, so no tree node either.
        // WhenAuthoritativeSourceDisconnected with the source as trigger, so deletion fires despite the
        // target remaining joined (WhenLastConnectorDisconnected cannot: the target is still a connector).
        var (cso, mvo, _, _) = ArrangeOutOfScopeCascadeFixture(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected);
        mvo.Type!.DeletionTriggerConnectedSystemIds = [cso.ConnectedSystemId];

        var targetSystem = ConnectedSystemsData.First(s => s.Id != cso.ConnectedSystemId);
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvo.Type!.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.ObjectMatchingRules = new List<ObjectMatchingRule>();

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            // A live target: a Pending Provisioning one that was never exported has its provisioning cancelled
            // instead (a ProvisioningCancelled node under the deletion), whatever the rule's action.
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };
        SyncRepo.SeedConnectedSystemObject(targetCso);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - warned, not a tree node: the real run records nothing for a disconnect-only downstream object
        var deletionNode = result.OutcomeTree[0].Children.Single(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deletionNode.Children, Is.Empty, "A disconnect-only downstream object gets no tree node");
            Assert.That(result.Warnings.Any(w => w.Code == SyncPreviewMessageCode.DownstreamDisconnectOnly
                && w.ConnectedSystemId == targetSystem.Id), Is.True,
                "The disconnect-only downstream object must still be surfaced, as a warning");
            Assert.That(result.Outbound.ProposedExports, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithDownstreamDeleteTarget_QueuesDeprovisioningAndProposesTheDeleteAsync()
    {
        // Arrange - a downstream target CSO whose export rule's Outbound Deprovision Action is Delete.
        // WhenAuthoritativeSourceDisconnected with the source as trigger, so deletion fires despite the
        // target remaining joined (WhenLastConnectorDisconnected cannot: the target is still a connector).
        var (cso, mvo, _, _) = ArrangeOutOfScopeCascadeFixture(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected);
        mvo.Type!.DeletionTriggerConnectedSystemIds = [cso.ConnectedSystemId];

        var targetSystem = ConnectedSystemsData.First(s => s.Id != cso.ConnectedSystemId);
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvo.Type!.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.ObjectMatchingRules = new List<ObjectMatchingRule>();

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };
        SyncRepo.SeedConnectedSystemObject(targetCso);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - a DeprovisionQueued node nested under MvoDeleted, and a proposed delete export
        var deletionNode = result.OutcomeTree[0].Children.Single(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deletionNode.Children, Has.Count.EqualTo(1));
            Assert.That(deletionNode.Children[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued));
            Assert.That(deletionNode.Children[0].StagedChangeType, Is.EqualTo(PendingExportChangeType.Delete));
            Assert.That(result.Warnings.Any(w => w.Code == SyncPreviewMessageCode.DownstreamDisconnectOnly), Is.False);
        }

        Assert.That(result.Outbound.ProposedExports.Count(pe =>
            pe.ChangeType == PendingExportChangeType.Delete && pe.ConnectedSystemObjectId == targetCso.Id), Is.EqualTo(1));

        // Zero side effects
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targetCso.MetaverseObjectId, Is.EqualTo(mvo.Id), "A preview must never disconnect the downstream object");
            Assert.That(PendingExportsData, Is.Empty, "A preview must persist nothing");
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

    #region Unique Value Generation (#242)

    [Test]
    public async Task PreviewSyncForCsoAsync_ProjectingObjectWithGeneratedMapping_ShowsCandidateRecordsOutcomeMakesNoWriteAsync()
    {
        // Arrange - a generated mapping (OnlyIfTaken, base expression cs["EMPLOYEE_ID"] = "E123") on a
        // projecting import Synchronisation Rule.
        var (cso, importRule, mvEmployeeIdAttr, _) = ArrangeGeneratedInboundFixture();
        importRule.ProjectToMetaverse = true;
        var mvoCountBefore = MetaverseObjectsData.Count;

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the candidate flows exactly like an ordinary Attribute Flow value would
        Assert.That(result.Inbound, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound!.WouldProject, Is.True);
            Assert.That(result.Inbound!.AttributeFlowChanges.Any(c =>
                c.AttributeId == mvEmployeeIdAttr.Id && c.IsAddition && c.Value == "E123"), Is.True,
                "The generated candidate must be captured as an inbound attribute change, exactly like an ordinary flow");
            Assert.That(result.HasBlockingErrors, Is.False);
        }

        // The outcome tree records a GeneratedValueAssigned child of the Projected root, alongside the
        // Attribute Flow child, naming the attribute and the candidate value.
        var root = result.OutcomeTree.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
            var generatedNode = root.Children.SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned);
            Assert.That(generatedNode, Is.Not.Null, "the outcome tree must record the generated value");
            Assert.That(generatedNode!.DetailMessage, Is.EqualTo($"{mvEmployeeIdAttr.Name}: E123"));
        }

        // Zero side effects: a preview never writes an assignment or joins/creates anything. The guard
        // (ReadOnlySyncRepositoryGuard) would throw PreviewWriteAttemptedException if the resolve path ever
        // reached for one (CreateGeneratedValueAssignmentsAsync is never called here at all, since
        // CommitAssignmentsAsync is not); this asserts the observable consequence, that nothing changed.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObject, Is.Null, "a preview must not join the CSO");
            Assert.That(cso.MetaverseObjectId, Is.Null);
            Assert.That(MetaverseObjectsData, Has.Count.EqualTo(mvoCountBefore));
            Assert.That(PendingExportsData, Is.Empty);
        }
    }

    /// <summary>
    /// Nothing is reserved or written by a dry-run resolve (plan "The service": DryRun releases whatever it
    /// claims before <c>ResolveAsync</c> returns), so a second, independent preview of the same object sees
    /// the identical free candidate rather than a collision-suffixed one.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_CalledTwiceInARow_ShowsTheSameCandidateBothTimesAsync()
    {
        var (cso, importRule, mvEmployeeIdAttr, _) = ArrangeGeneratedInboundFixture();
        importRule.ProjectToMetaverse = true;

        var first = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);
        var second = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        var firstValue = first.Inbound!.AttributeFlowChanges.Single(c => c.AttributeId == mvEmployeeIdAttr.Id).Value;
        var secondValue = second.Inbound!.AttributeFlowChanges.Single(c => c.AttributeId == mvEmployeeIdAttr.Id).Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstValue, Is.EqualTo("E123"));
            Assert.That(secondValue, Is.EqualTo(firstValue), "nothing was reserved or written by the first preview");
        }
    }

    /// <summary>
    /// The flow-errors ternary fix: a generated mapping whose base Expression returns more than one value
    /// (here, <c>Split</c> producing a <c>string[]</c>, the same array shape
    /// <see cref="AttributeFlowErrorKind.GeneratedBaseNotSingleValue"/> exists for) gets its own accurate
    /// message rather than the generic "a required input has no value" one, which described a different
    /// failure entirely.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_GeneratedBaseExpressionReturnsMultipleValues_ReportsItsOwnMessageAsync()
    {
        var (cso, importRule, mvEmployeeIdAttr, _) = ArrangeGeneratedInboundFixture(baseExpression: "Split(\"a,b\", \",\")");
        importRule.ProjectToMetaverse = true;

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        Assert.That(result.Errors, Has.Count.EqualTo(1));
        var error = result.Errors[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Code, Is.EqualTo(SyncPreviewMessageCode.ExpressionEvaluationError));
            Assert.That(error.AttributeName, Is.EqualTo(mvEmployeeIdAttr.Name));
            Assert.That(error.Detail, Does.Contain("returned more than one value"));
            Assert.That(error.Detail, Does.Contain("must produce a single text value, not an array"));
            Assert.That(error.Detail, Does.Not.Contain("a required input has no value"),
                "this failure is not a missing-input failure, and must not read like one");
        }
    }

    /// <summary>
    /// Adopt-before-generate participation (work package J): the previewed CSO joins an already-persisted
    /// Metaverse Object that is ALSO joined, via a participating export target (Dummy Target System), to a
    /// Connected System Object that already holds a value for the generated attribute. The preview must
    /// compute the same participating targets and adoptable value the worker would (through the shared
    /// <c>GeneratedValueParticipation</c> helper), so it shows the existing value adopted rather than a
    /// fresh candidate, and writes nothing.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ParticipatingTargetAlreadyHoldsAValue_ShowsItAdoptedAsync()
    {
        // Arrange - a generated mapping (base expression cs["EMPLOYEE_ID"] = "E123") on a JOIN (not a
        // projection), so the working Metaverse Object is already persisted when generation resolves.
        var (cso, importRule, mvEmployeeIdAttr, _) = ArrangeGeneratedInboundFixture();

        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        SyncRepo.SeedMetaverseObject(mvo);
        cso.MetaverseObjectId = mvo.Id;
        cso.MetaverseObject = mvo;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;

        // The participating export target: Dummy Target System, enabled export rule with a single-source
        // mapping reading the SAME generated attribute, already holding "jsmith" for the object the
        // previewed CSO is joined to.
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csTargetEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.ObjectMatchingRules = new List<ObjectMatchingRule>();
        exportRule.AttributeFlowRules.Clear();
        var employeeIdExportMapping = new SyncRuleMapping
        {
            Id = 7601,
            SyncRule = exportRule,
            SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = csTargetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = csTargetEmployeeIdAttr.Id
        };
        employeeIdExportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 7601,
            Order = 1,
            MetaverseAttribute = mvEmployeeIdAttr,
            MetaverseAttributeId = mvEmployeeIdAttr.Id
        });
        exportRule.AttributeFlowRules.Add(employeeIdExportMapping);

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = csTargetEmployeeIdAttr,
            AttributeId = csTargetEmployeeIdAttr.Id,
            StringValue = "jsmith"
        });
        SyncRepo.SeedConnectedSystemObject(targetCso);

        // Act
        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        // Assert - the existing target value is adopted, not the freshly evaluated base "E123"
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound!.AttributeFlowChanges.Any(c =>
                c.AttributeId == mvEmployeeIdAttr.Id && c.IsAddition && c.Value == "jsmith"), Is.True,
                "the participating target's existing value must be adopted, not a fresh candidate generated");
            Assert.That(result.HasBlockingErrors, Is.False);
        }

        var root = result.OutcomeTree.Single();
        var generatedNode = root.Children.SingleOrDefault(c =>
            c.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned
                or ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedNode, Is.Not.Null);
            Assert.That(generatedNode!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted),
                "adoption must be recorded, not a fresh generation");
            Assert.That(generatedNode.DetailMessage, Is.EqualTo($"{mvEmployeeIdAttr.Name}: jsmith"));
        }

        // Zero side effects: nothing written.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty);
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(mvo.Id), "a preview must not change the existing join");
        }
    }

    /// <summary>
    /// A generation that would fail in the real run (here, Exhausted: a one-attempt budget and the exact
    /// candidate already taken by another object) must not simply show nothing in the preview; it must
    /// surface as a warning, mirroring the error the real run would record on the object.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_GeneratedValueWouldBeExhausted_AddsWarningAsync()
    {
        var (cso, importRule, mvEmployeeIdAttr, mapping) = ArrangeGeneratedInboundFixture();
        importRule.ProjectToMetaverse = true;
        mapping.Generation!.AttemptLimit = 1;

        // Another object already holds the exact base candidate; with a one-attempt budget there is no
        // room for a suffixed retry, so resolution is exhausted.
        var takenMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = MetaverseObjectTypesData.Single(t => t.Name == "User")
        };
        takenMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = takenMvo,
            Attribute = mvEmployeeIdAttr,
            AttributeId = mvEmployeeIdAttr.Id,
            StringValue = "E123"
        });
        SyncRepo.SeedMetaverseObject(takenMvo);

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Inbound!.AttributeFlowChanges.Any(c => c.AttributeId == mvEmployeeIdAttr.Id), Is.False,
                "no candidate was resolved, so nothing should flow for the attribute");

            var warning = result.Warnings.SingleOrDefault(w => w.Code == SyncPreviewMessageCode.GeneratedValueWouldFail);
            Assert.That(warning, Is.Not.Null, "an exhausted generation must surface as a warning, not silently show nothing");
            Assert.That(warning!.AttributeName, Is.EqualTo(mvEmployeeIdAttr.Name));
            Assert.That(warning.Detail, Is.Not.Empty);
        }
    }

    #endregion

    #region Out-of-scope cascade re-election with a generated survivor (#242)

    /// <summary>
    /// The previewed CSO (Dummy Source System) is joined and about to fall out of scope; Dummy Target System
    /// is joined to the SAME Metaverse Object via a lower-priority GENERATED import mapping. The scope-exit
    /// cascade's recall must re-elect Dummy Target System's mapping and resolve it through the preview's own
    /// dry-run resolver, not drop it silently (work package J: the re-election call previously passed no
    /// resolver at all).
    /// </summary>
    private (ConnectedSystemObject Cso, MetaverseObject Mvo, MetaverseAttribute MvEmployeeIdAttr)
        ArrangeScopeExitGeneratedReElectionFixture()
    {
        var (cso, importRule, mvEmployeeIdAttr) = ArrangeInboundFixture();
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var sourceUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_USER");
        sourceUserType.RemoveContributedAttributesOnObsoletion = true;

        importRule.MetaverseObjectType = mvUserType;
        importRule.MetaverseObjectTypeId = mvUserType.Id;
        var hrMapping = importRule.AttributeFlowRules.Single();
        hrMapping.SyncRuleId = importRule.Id;
        hrMapping.Priority = 1;

        // A scoping criterion the previewed CSO fails, forcing the out-of-scope cascade.
        var scopingGroup = new SyncRuleScopingCriteriaGroup();
        scopingGroup.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = sourceUserType.Attributes.Single(a => a.Id == (int)MockSourceSystemAttributeNames.EMPLOYEE_TYPE),
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "an employee type this object does not have"
        });
        importRule.ObjectScopingCriteriaGroups.Add(scopingGroup);

        // The joined Metaverse Object, holding the previewed CSO's value with its own provenance.
        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = mvEmployeeIdAttr,
            AttributeId = mvEmployeeIdAttr.Id,
            StringValue = "EMP-HR",
            ContributedBySystemId = cso.ConnectedSystemId,
            ContributedBySyncRuleId = importRule.Id
        });
        cso.MetaverseObjectId = mvo.Id;
        cso.MetaverseObject = mvo;
        cso.JoinType = ConnectedSystemObjectJoinType.Joined;
        SyncRepo.SeedMetaverseObject(mvo);
        // Re-seed: the initial seed (inside ArrangeInboundFixture) ran before MetaverseObjectId was set, so
        // the repository's by-MVO index needs refreshing for survivor discovery to find this CSO's system.
        SyncRepo.SeedConnectedSystemObject(cso);

        // The surviving Connected System (Dummy Target System): a generated mapping at lower priority,
        // joined to the same Metaverse Object.
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csTargetEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var survivorRule = new SyncRule
        {
            Id = 7701,
            Name = "Survivor Import Synchronisation Rule",
            ConnectedSystemId = targetSystem.Id,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectType = mvUserType,
            MetaverseObjectTypeId = mvUserType.Id,
            ConnectedSystemObjectType = targetUserType,
            ConnectedSystemObjectTypeId = targetUserType.Id
        };
        var generatedMapping = new SyncRuleMapping
        {
            Id = 7702,
            SyncRule = survivorRule,
            SyncRuleId = survivorRule.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Priority = 2,
            Generation = new SyncRuleMappingGeneration()
        };
        generatedMapping.Sources.Add(new SyncRuleMappingSource { Id = 7702, Order = 1, Expression = "\"trn-001\"" });
        survivorRule.AttributeFlowRules.Add(generatedMapping);
        SyncRulesData.Add(survivorRule);
        SyncRepo.SeedSyncRule(survivorRule);

        var survivorCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        survivorCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = survivorCso,
            Attribute = csTargetEmployeeIdAttr,
            AttributeId = csTargetEmployeeIdAttr.Id,
            StringValue = "unused"
        });
        SyncRepo.SeedConnectedSystemObject(survivorCso);

        return (cso, mvo, mvEmployeeIdAttr);
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithSurvivingGeneratedContributor_ShowsGeneratedValueAsync()
    {
        var (cso, _, mvEmployeeIdAttr) = ArrangeScopeExitGeneratedReElectionFixture();

        var result = await Jim.SyncPreview.PreviewSyncForCsoAsync(cso.ConnectedSystemId, cso.Id);

        Assert.That(result.Warnings.Any(w => w.Code == SyncPreviewMessageCode.OutOfScope), Is.True);

        var root = result.OutcomeTree.SingleOrDefault(n => n.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);
        Assert.That(root, Is.Not.Null, "the cascade must record the disconnection root");

        var generatedNode = root!.Children.SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned);
        Assert.That(generatedNode, Is.Not.Null, "the re-elected generated mapping must be resolved and shown, not dropped");
        Assert.That(generatedNode!.DetailMessage, Is.EqualTo($"{mvEmployeeIdAttr.Name}: trn-001"));

        Assert.That(SyncRepo.GeneratedValueAssignments, Is.Empty, "a preview must persist nothing");
    }

    #endregion
}
