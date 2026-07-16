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
using JIM.Utilities;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for ExportEvaluationServer - the Q1 decision to create PendingExports when MVO changes.
/// </summary>
public class ExportEvaluationTests
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

        // Set up the Connected System Run Profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        // Set up the Activity mock
        var exportRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Target System Export");
        ActivitiesData = TestUtilities.GetActivityData(exportRunProfile.RunType, exportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        // Set up the Connected Systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // Set up the Connected System Object Types mock
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // Set up the Connected System Objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        // Set up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        // Set up the Pending Export objects mock
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        // Set up the Metaverse Object Types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        // Set up the Metaverse Objects mock
        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        // Set up the Synchronisation Rule stub mocks
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Mock entity framework calls
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

        // Instantiate Jim using the mocked db context.
        // Pass SyncRulesData so the same instances that tests modify are seeded into SyncRepo.
        SyncRepo = TestUtilities.CreateSyncRepository(
            activity: ActivitiesData.First(),
            syncRules: SyncRulesData);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    /// <summary>
    /// Tests that export rules are evaluated based on MVO type matching.
    /// This validates the basic logic of finding applicable export rules for an MVO.
    /// Note: A full integration test would require more complex mocking of repository methods.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenNoExportRulesMatchMvoType_ReturnsEmptyListAsync()
    {
        // Arrange - use an MVO with type that doesn't match any enabled export rules
        var mvo = MetaverseObjectsData[0];

        // Set the MVO type to User
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        // Ensure no export rules match this type by changing all export rules to point to a different type
        foreach (var rule in SyncRulesData.Where(sr => sr.Direction == SyncRuleDirection.Export))
        {
            rule.MetaverseObjectTypeId = 9999; // Non-existent type ID
        }

        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            mvo.AttributeValues.First()
        };

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - no rules should match
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no PendingExports when no export rules match MVO type");
    }

    /// <summary>
    /// Tests the Q3 decision: circular sync prevention - exports should not be created back to the source system.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenSourceSystemIsTarget_SkipsExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");

        // Set up export rule pointing back to source system (which should be skipped)
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.ConnectedSystemId = sourceSystem.Id;
        exportRule.ConnectedSystem = sourceSystem;

        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            mvo.AttributeValues.First()
        };

        // Act - pass sourceSystem as the source of changes (Q3 circular prevention)
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes, sourceSystem);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no PendingExports when source system is the target (Q3 decision)");
    }

    /// <summary>
    /// Repoints the seeded user export Synchronisation Rule at the Dummy Target System and its
    /// TARGET_USER object type, with the given deprovisioning action. The seeded export rules all
    /// point at the source system, so without this the MVO-deletion cascade finds no rule matching
    /// a target-system CSO. Returns the rule so callers can tweak it further.
    /// </summary>
    private SyncRule ArrangeDeletionExportRule(OutboundDeprovisionAction action)
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.MetaverseObjectType = mvUserType;
        exportRule.OutboundDeprovisionAction = action;
        return exportRule;
    }

    /// <summary>
    /// Creates a CSO of the given join type in the Dummy Target System, joined to the MVO,
    /// and seeds it into both data stores.
    /// </summary>
    private ConnectedSystemObject ArrangeJoinedTargetCso(MetaverseObject mvo, ConnectedSystemObjectJoinType joinType)
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = joinType
        };

        ConnectedSystemObjectsData.Add(cso);
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>
    /// Tests that the MVO-deletion cascade honours the export Synchronisation Rule's
    /// OutboundDeprovisionAction (issue #655): a Provisioned CSO under a Delete-action rule
    /// gets a delete Pending Export.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_ProvisionedCsoWithDeleteActionRule_CreatesDeleteExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var provisionedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.GreaterThan(0), "Expected delete PendingExport for CSO under a Delete-action rule");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(provisionedCso.Id));
    }

    /// <summary>
    /// Tests the headline issue #655 behaviour: a Joined (not Provisioned) CSO under a Delete-action
    /// export Synchronisation Rule now gets a delete Pending Export when its MVO is deleted. The
    /// CSO's join type no longer gates deprovisioning; the rule's action does.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_JoinedCsoWithDeleteActionRule_CreatesDeleteExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var joinedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Joined);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Expected a delete PendingExport for a Joined CSO under a Delete-action rule (issue #655)");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(joinedCso.Id));
    }

    /// <summary>
    /// Tests the deliberate issue #655 behaviour change: a Provisioned CSO under a Disconnect-action
    /// (default) export Synchronisation Rule is disconnected only; no delete Pending Export is
    /// created, even though JIM originally provisioned the CSO.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_ProvisionedCsoWithDisconnectActionRule_DisconnectsWithoutDeleteExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Disconnect);
        var provisionedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no delete PendingExport when the rule's action is Disconnect");
        Assert.That(provisionedCso.MetaverseObjectId, Is.Null, "The CSO must still be disconnected from its MVO");
        Assert.That(provisionedCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
    }

    /// <summary>
    /// Tests the safe default: when no export Synchronisation Rule matches the CSO's system and
    /// object type, the CSO is disconnected only; no delete Pending Export is created.
    /// The seeded export rules point at the source system, so nothing matches the target CSO.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_NoMatchingExportRule_DisconnectsWithoutDeleteExportAsync()
    {
        // Arrange: deliberately no ArrangeDeletionExportRule call
        var mvo = MetaverseObjectsData[0];
        var provisionedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no delete PendingExport when no export rule matches the CSO");
        Assert.That(provisionedCso.MetaverseObjectId, Is.Null, "The CSO must still be disconnected from its MVO");
    }

    /// <summary>
    /// Tests conflict resolution: when two enabled export Synchronisation Rules match the same CSO
    /// with different deprovisioning actions, Delete wins (the most explicit admin intent).
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_ConflictingDeprovisionActions_DeleteWinsAsync()
    {
        // Arrange: rule 1 says Disconnect, a second rule with the same triple says Delete
        var mvo = MetaverseObjectsData[0];
        var disconnectRule = ArrangeDeletionExportRule(OutboundDeprovisionAction.Disconnect);
        var deleteRule = new SyncRule
        {
            Id = 99,
            ConnectedSystemId = disconnectRule.ConnectedSystemId,
            ConnectedSystem = disconnectRule.ConnectedSystem,
            Name = "Dummy User Export Synchronisation Rule 2",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = disconnectRule.ConnectedSystemObjectTypeId,
            ConnectedSystemObjectType = disconnectRule.ConnectedSystemObjectType,
            MetaverseObjectTypeId = disconnectRule.MetaverseObjectTypeId,
            MetaverseObjectType = disconnectRule.MetaverseObjectType,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };
        SyncRulesData.Add(deleteRule);
        SyncRepo.SeedSyncRule(deleteRule);

        var joinedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Joined);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Expected Delete to win when matching rules conflict");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(joinedCso.Id));
    }

    /// <summary>
    /// Tests the defensive path: an MVO with no Type cannot be matched to any export
    /// Synchronisation Rule, so its CSOs are disconnected without delete Pending Exports.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_MvoWithNoType_DisconnectsWithoutDeleteExportAsync()
    {
        // Arrange: a Delete-action rule exists, but the MVO has no Type to match it by
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var typelessMvo = new MetaverseObject { Id = Guid.NewGuid() };
        MetaverseObjectsData.Add(typelessMvo);
        SyncRepo.SeedMetaverseObject(typelessMvo);
        var provisionedCso = ArrangeJoinedTargetCso(typelessMvo, ConnectedSystemObjectJoinType.Provisioned);
        typelessMvo.ConnectedSystemObjects.Add(provisionedCso);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(typelessMvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no delete PendingExport for an MVO with no Type");
        Assert.That(provisionedCso.MetaverseObjectId, Is.Null, "The CSO must still be disconnected from its MVO");
    }

    /// <summary>
    /// Tests that all join types are treated equally under a Delete-action rule (issue #655):
    /// Provisioned, Joined and Projected CSOs joined to the same MVO all get delete Pending
    /// Exports, and all are disconnected.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionsAsync_MixedJoinTypesWithDeleteActionRule_CreatesDeleteExportsForAllJoinedCsosAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var csos = new List<ConnectedSystemObject>
        {
            ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned),
            ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Joined),
            ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Projected)
        };

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionsAsync(new[] { mvo });

        // Assert
        Assert.That(result, Has.Count.EqualTo(3), "Expected one delete PendingExport per CSO regardless of join type");
        Assert.That(result.All(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.True);
        foreach (var cso in csos)
        {
            Assert.That(result.Any(pe => pe.ConnectedSystemObjectId == cso.Id), Is.True,
                $"Expected a delete PendingExport for CSO with join type {cso.JoinType}");
            Assert.That(cso.MetaverseObjectId, Is.Null, $"CSO {cso.Id} must be disconnected from its MVO");
        }
    }

    /// <summary>
    /// Tests that when MVO deletion creates a delete export for a Provisioned CSO,
    /// the secondary external ID (e.g., DN for LDAP) is stored in AttributeValueChanges.
    /// This is critical for synchronous MVO deletion scenarios where the CSO may be deleted
    /// before the export runs.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_WhenCsoHasSecondaryExternalId_StoresItInAttributeValueChangesAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var dnAttribute = targetUserType.Attributes.Single(a => a.Name == "distinguishedName");

        // Create a provisioned CSO joined to the MVO with a DN value
        var provisionedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            SecondaryExternalIdAttributeId = dnAttribute.Id
        };

        // Add the DN attribute value to the CSO
        var expectedDn = "CN=Test User,OU=Users,DC=example,DC=com";
        provisionedCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = dnAttribute,
            AttributeId = dnAttribute.Id,
            StringValue = expectedDn
        });

        ConnectedSystemObjectsData.Add(provisionedCso);
        SyncRepo.SeedConnectedSystemObject(provisionedCso);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Expected one delete PendingExport");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result[0].AttributeValueChanges, Is.Not.Empty, "Expected AttributeValueChanges to contain the DN");
        Assert.That(result[0].AttributeValueChanges.Count, Is.EqualTo(1));
        Assert.That(result[0].AttributeValueChanges[0].AttributeId, Is.EqualTo(dnAttribute.Id));
        Assert.That(result[0].AttributeValueChanges[0].StringValue, Is.EqualTo(expectedDn), "DN should be stored in AttributeValueChanges");
    }

    /// <summary>
    /// Tests that EvaluateMvoDeletionAsync skips creating a duplicate Delete PE when one already exists.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_WhenDeletePeAlreadyExists_SkipsDuplicateCreationAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var provisionedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned);

        // Pre-populate with an existing Delete PE for this CSO
        // Must set ConnectedSystemObject navigation property because mock DbSet doesn't auto-load it
        var existingDeletePe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystemObjectId = provisionedCso.Id,
            ConnectedSystemObject = provisionedCso,
            ChangeType = PendingExportChangeType.Delete,
            Status = PendingExportStatus.Exported,
            SourceMetaverseObjectId = mvo.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        PendingExportsData.Add(existingDeletePe);
        SyncRepo.SeedPendingExport(existingDeletePe);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert — should return the existing PE, not create a new one
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Should return exactly one PE");
        Assert.That(result[0].Id, Is.EqualTo(existingDeletePe.Id), "Should return the existing Delete PE, not a new one");
        Assert.That(SyncRepo.PendingExports.Count, Is.EqualTo(1), "Should NOT create a new PE when Delete PE already exists");
    }

    /// <summary>
    /// Tests that EvaluateMvoDeletionAsync replaces an existing Create PE with a Delete PE.
    /// This can happen when a CSO was provisioned but never exported before the MVO is deleted.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_WhenCreatePeExists_ReplacesWithDeletePeAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var provisionedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned);

        // Pre-populate with an existing Create PE for this CSO (not yet exported)
        // Must set ConnectedSystemObject navigation property because mock DbSet doesn't auto-load it
        var existingCreatePe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystemObjectId = provisionedCso.Id,
            ConnectedSystemObject = provisionedCso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        PendingExportsData.Add(existingCreatePe);
        SyncRepo.SeedPendingExport(existingCreatePe);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert — should create a new Delete PE (replacing the Create PE)
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Should return exactly one PE");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete), "Should be a Delete PE");
        Assert.That(result[0].Id, Is.Not.EqualTo(existingCreatePe.Id), "Should be a new PE, not the old Create PE");
        Assert.That(SyncRepo.PendingExports.Values.Count(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.EqualTo(1),
            "Should create exactly one new Delete PE");
    }

    /// <summary>
    /// Tests that when EvaluateMvoDeletionAsync replaces an existing non-Delete Pending Export, the
    /// replaced Pending Export is removed from the store together with its attribute value changes;
    /// none of them may leak onto the replacement Delete Pending Export. Pins the delete-path fetch
    /// behaviour across the lean-fetch call-site change (issue #986): child-row disposal on delete
    /// relies on the fetched entity having its AttributeValueChanges loaded.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_WhenCreatePeWithAttributeChangesExists_ReplacementDisposesOldPeAndChangesAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var targetAttribute = targetUserType.Attributes.First(a => a.Type == AttributeDataType.Text);
        var provisionedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Provisioned);

        // Pre-populate with an existing Create PE carrying attribute value changes
        // Must set ConnectedSystemObject navigation property because mock DbSet doesn't auto-load it
        var existingCreatePe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystemObjectId = provisionedCso.Id,
            ConnectedSystemObject = provisionedCso,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        existingCreatePe.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            PendingExportId = existingCreatePe.Id,
            Attribute = targetAttribute,
            AttributeId = targetAttribute.Id,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = "stale value from the replaced Create PE"
        });
        PendingExportsData.Add(existingCreatePe);
        SyncRepo.SeedPendingExport(existingCreatePe);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert: the old Create PE and its changes are gone; the replacement carries none of them
        Assert.That(result.Count, Is.EqualTo(1), "Should return exactly one PE");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete), "Should be a Delete PE");
        Assert.That(SyncRepo.PendingExports.ContainsKey(existingCreatePe.Id), Is.False,
            "The replaced Create PE should be removed from the store");
        Assert.That(SyncRepo.PendingExports.Count, Is.EqualTo(1), "Only the replacement Delete PE should remain");
        Assert.That(result[0].AttributeValueChanges.Any(avc => avc.StringValue == "stale value from the replaced Create PE"),
            Is.False, "No attribute value change from the replaced PE may leak onto the replacement");
    }

    /// <summary>
    /// Tests that a supplied export evaluation cache is honoured (issue #655): the cache's rules
    /// drive the deprovisioning decisions and no Synchronisation Rule reload hits the repository.
    /// The repository's rules deliberately match nothing; only the cache carries a matching
    /// Delete-action rule.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionsAsync_WithProvidedCache_UsesCacheRulesAsync()
    {
        // Arrange: repository rules untouched (they point at the source system, matching nothing)
        var mvo = MetaverseObjectsData[0];
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var joinedCso = ArrangeJoinedTargetCso(mvo, ConnectedSystemObjectJoinType.Joined);

        var cacheOnlyRule = new SyncRule
        {
            Id = 98,
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Name = "Cache-Only User Export Synchronisation Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetUserType.Id,
            ConnectedSystemObjectType = targetUserType,
            MetaverseObjectTypeId = mvUserType.Id,
            MetaverseObjectType = mvUserType,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };
        var cache = new ExportEvaluationCache(
            new Dictionary<int, List<SyncRule>> { [mvUserType.Id] = [cacheOnlyRule] },
            new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>(),
            Array.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(x => (x.ConnectedSystemObject.Id, x.AttributeId)),
            new List<int> { targetSystem.Id });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionsAsync([mvo], cache);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1), "Expected the cache's Delete-action rule to drive the deprovisioning decision");
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(joinedCso.Id));
        Assert.That(SyncRepo.GetAllSyncRulesCallCount, Is.EqualTo(0),
            "No Synchronisation Rule reload may hit the repository when a cache is supplied");
    }

    /// <summary>
    /// Tests the set-based deletion evaluation (issue #993) with a genuinely mixed batch: three
    /// MVOs whose CSOs fall under a Delete-action export Synchronisation Rule and are respectively
    /// fresh (no Pending Export), carrying an existing Delete Pending Export (must be reused), and
    /// carrying an existing Create Pending Export (must be replaced), plus a fourth MVO whose CSO
    /// has a different object type that no export rule matches (disconnect only, no delete PE).
    /// The per-CSO collision policy must be applied independently within the one call, and every
    /// CSO must end up disconnected from its MVO.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionsAsync_MixedCollisionStates_AppliesPolicyPerCsoAsync()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var sourceGroupType = ConnectedSystemObjectTypesData.Single(t => t.Name == "SOURCE_GROUP");
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        ArrangeDeletionExportRule(OutboundDeprovisionAction.Delete);

        var mvos = new List<MetaverseObject>();
        var csos = new List<ConnectedSystemObject>();
        for (var i = 0; i < 4; i++)
        {
            var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvUserType };
            // the fourth CSO's object type is not matched by any export rule: disconnect only, no delete PE
            var csoType = i < 3 ? targetUserType : sourceGroupType;
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = targetSystem.Id,
                Type = csoType,
                TypeId = csoType.Id,
                MetaverseObject = mvo,
                MetaverseObjectId = mvo.Id,
                JoinType = i < 3 ? ConnectedSystemObjectJoinType.Provisioned : ConnectedSystemObjectJoinType.Joined,
                DateJoined = DateTime.UtcNow
            };
            mvo.ConnectedSystemObjects.Add(cso);
            MetaverseObjectsData.Add(mvo);
            ConnectedSystemObjectsData.Add(cso);
            SyncRepo.SeedMetaverseObject(mvo);
            SyncRepo.SeedConnectedSystemObject(cso);
            mvos.Add(mvo);
            csos.Add(cso);
        }

        // CSO 1 carries an existing Delete PE (must be reused)
        var existingDeletePe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystemObjectId = csos[1].Id,
            ConnectedSystemObject = csos[1],
            ChangeType = PendingExportChangeType.Delete,
            Status = PendingExportStatus.Exported,
            SourceMetaverseObjectId = mvos[1].Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        SyncRepo.SeedPendingExport(existingDeletePe);

        // CSO 2 carries an existing Create PE (must be replaced with a Delete PE)
        var existingCreatePe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystemObjectId = csos[2].Id,
            ConnectedSystemObject = csos[2],
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvos[2].Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        SyncRepo.SeedPendingExport(existingCreatePe);

        // Act: one set-based call for the whole batch
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionsAsync(mvos);

        // Assert: three Delete PEs (fresh create, reuse, replacement); the unmatched CSO gets none
        Assert.That(result, Has.Count.EqualTo(3), "Expected one Delete PE per CSO matched by the Delete-action rule");
        Assert.That(result.All(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.True);

        var freshPe = result.SingleOrDefault(pe => pe.ConnectedSystemObjectId == csos[0].Id);
        Assert.That(freshPe, Is.Not.Null, "Expected a new Delete PE for the fresh CSO");

        var reusedPe = result.SingleOrDefault(pe => pe.ConnectedSystemObjectId == csos[1].Id);
        Assert.That(reusedPe, Is.Not.Null);
        Assert.That(reusedPe!.Id, Is.EqualTo(existingDeletePe.Id), "The existing Delete PE must be reused, not duplicated");

        var replacementPe = result.SingleOrDefault(pe => pe.ConnectedSystemObjectId == csos[2].Id);
        Assert.That(replacementPe, Is.Not.Null);
        Assert.That(replacementPe!.Id, Is.Not.EqualTo(existingCreatePe.Id), "The Create PE must be replaced by a new Delete PE");
        Assert.That(SyncRepo.PendingExports.ContainsKey(existingCreatePe.Id), Is.False, "The replaced Create PE must be removed");

        Assert.That(result.Any(pe => pe.ConnectedSystemObjectId == csos[3].Id), Is.False,
            "The CSO with no matching export rule must not get a delete PE");

        // Assert: every CSO is disconnected from its MVO
        foreach (var cso in csos)
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, $"CSO {cso.Id} must be disconnected from its MVO");
            Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
            Assert.That(cso.DateJoined, Is.Null);
        }
    }

    #region EvaluateOutOfScopeExportsAsync (cascade) — Delete-PE collision handling

    /// <summary>
    /// Pushes the MVO out of the export rule's scope (so the cascade fires) and configures
    /// the rule with <c>OutboundDeprovisionAction.Delete</c>. Returns the provisioned CSO so
    /// callers can pre-seed an existing PendingExport on it.
    /// </summary>
    private ConnectedSystemObject ArrangeCascadeDeleteWithOutOfScopeMvo(MetaverseObject mvo)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        // Scope criterion that the MVO cannot satisfy (its EmployeeId is "E123").
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "OUT-OF-SCOPE-MARKER"
                }
            }
        });

        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var provisionedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };

        ConnectedSystemObjectsData.Add(provisionedCso);
        SyncRepo.SeedConnectedSystemObject(provisionedCso);
        return provisionedCso;
    }

    /// <summary>
    /// Tests the bug repro: when the cascade fires and the target CSO still has an
    /// <c>Exported</c>-status PE from a prior export (the next confirming import hasn't
    /// reconciled it away yet), creating a Delete PE used to throw a unique-constraint
    /// violation against PostgreSQL's PendingExports index. After the fix, the cascade
    /// reuses the existing Delete PE rather than duplicating it.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenDeletePeAlreadyExistsOnTargetCso_ReusesItAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var cso = ArrangeCascadeDeleteWithOutOfScopeMvo(mvo);

        var existingDeletePe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = cso.ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Delete,
            Status = PendingExportStatus.Exported,
            SourceMetaverseObjectId = mvo.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        PendingExportsData.Add(existingDeletePe);
        SyncRepo.SeedPendingExport(existingDeletePe);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Cascade should still report exactly one PE");
        Assert.That(result[0].Id, Is.EqualTo(existingDeletePe.Id), "Cascade should reuse the existing Delete PE, not duplicate it");
        Assert.That(SyncRepo.PendingExports.Count, Is.EqualTo(1), "Repo must still contain exactly one PE");
    }

    /// <summary>
    /// When a non-Delete PE (typically a stale Create from provisioning that never
    /// completed an export round-trip) is attached to the target CSO at cascade time,
    /// it is deleted and replaced with a Delete PE.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenCreatePeExistsOnTargetCso_ReplacesWithDeletePeAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var cso = ArrangeCascadeDeleteWithOutOfScopeMvo(mvo);

        var existingCreatePe = new PendingExport
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
        PendingExportsData.Add(existingCreatePe);
        SyncRepo.SeedPendingExport(existingCreatePe);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Cascade should produce exactly one PE");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete), "Cascade should produce a Delete PE");
        Assert.That(result[0].Id, Is.Not.EqualTo(existingCreatePe.Id), "The Create PE should have been replaced");
        Assert.That(SyncRepo.PendingExports.Values.Count(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.EqualTo(1),
            "Repo should contain exactly one Delete PE");
        Assert.That(SyncRepo.PendingExports.Values.Any(pe => pe.Id == existingCreatePe.Id), Is.False,
            "The original Create PE should have been removed");
    }

    /// <summary>
    /// Baseline cascade behaviour: no existing PE, a fresh Delete PE is created.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenNoExistingPe_CreatesNewDeletePeAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var cso = ArrangeCascadeDeleteWithOutOfScopeMvo(mvo);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1), "Cascade should produce exactly one PE");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(cso.Id));
        Assert.That(result[0].SourceMetaverseObjectId, Is.EqualTo(mvo.Id));
    }

    #endregion

    /// <summary>
    /// Tests that MVOs without a type set are handled gracefully.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenMvoHasNoType_ReturnsEmptyListAsync()
    {
        // Arrange
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = null! // No type set
        };

        var changedAttributes = new List<MetaverseObjectAttributeValue>();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no PendingExports when MVO has no type");
    }

    /// <summary>
    /// Tests that scoping criteria are evaluated correctly - MVO in scope.
    /// </summary>
    [Test]
    public void IsMvoInScopeForExportRule_WhenMvoMatchesCriteria_ReturnsTrue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");

        // Add scoping criteria: EmployeeId equals "E123"
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "E123"
                }
            }
        });

        // Act
        var result = Jim.ExportEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "Expected MVO to be in scope when criteria matches");
    }

    /// <summary>
    /// Tests that scoping criteria are evaluated correctly - MVO not in scope.
    /// </summary>
    [Test]
    public void IsMvoInScopeForExportRule_WhenMvoDoesNotMatchCriteria_ReturnsFalse()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");

        // Add scoping criteria: EmployeeId equals "DIFFERENT_VALUE"
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "DIFFERENT_VALUE"
                }
            }
        });

        // Act
        var result = Jim.ExportEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.False, "Expected MVO to be out of scope when criteria does not match");
    }

    /// <summary>
    /// Tests that when no scoping criteria exist, all MVOs are in scope.
    /// </summary>
    [Test]
    public void IsMvoInScopeForExportRule_WhenNoScopingCriteria_ReturnsTrue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");

        // Ensure no scoping criteria
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Act
        var result = Jim.ExportEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "Expected MVO to be in scope when no criteria defined");
    }

    #region Provisioning Flow End-to-End Tests

    /// <summary>
    /// End-to-end test: When MVO changes and no CSO exists for the target system,
    /// a new CSO should be created with Status=PendingProvisioning and JoinType=Provisioned,
    /// and a Create PendingExport should be generated.
    /// This tests the happy path of provisioning flow.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenNoCsoExistsAndProvisioningEnabled_CreatesPendingProvisioningCsoAndCreateExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Configure export rule for provisioning
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Ensure no CSO exists for this MVO in the target system
        ConnectedSystemObjectsData.RemoveAll(cso => cso.MetaverseObjectId == mvo.Id && cso.ConnectedSystemId == targetSystem.Id);

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - CSO should be created with PendingProvisioning status
        // CSOs are now created via SyncRepo.CreateConnectedSystemObjectAsync, not the mock DbSet
        var createdCsos = SyncRepo.ConnectedSystemObjects.Values.ToList();
        Assert.That(createdCsos, Has.Count.EqualTo(1), "Expected exactly one CSO to be created");
        var newCso = createdCsos[0];
        Assert.That(newCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "CSO should have PendingProvisioning status before export");
        Assert.That(newCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned),
            "CSO should have Provisioned JoinType");
        Assert.That(newCso.MetaverseObjectId, Is.EqualTo(mvo.Id),
            "CSO should be linked to the MVO");
        Assert.That(newCso.ConnectedSystemId, Is.EqualTo(targetSystem.Id),
            "CSO should be in the target system");

        // Assert - PendingExport should be created with Create change type
        Assert.That(result, Has.Count.EqualTo(1), "Expected exactly one PendingExport");
        var pendingExport = result[0];
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "PendingExport should be a Create operation");
        Assert.That(pendingExport.ConnectedSystemObjectId, Is.EqualTo(newCso.Id),
            "PendingExport should reference the newly created CSO");
    }

    /// <summary>
    /// Tests that a thrown outbound (export) attribute-flow expression is surfaced as a
    /// SyncExpressionEvaluationException rather than being silently swallowed (#842).
    /// </summary>
    [Test]
    public void EvaluateExportRulesAsync_WhenExpressionThrows_ThrowsSyncExpressionEvaluationExceptionAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // add an expression-based export mapping whose expression cannot be parsed, so the evaluator throws
        var targetCsAttribute = targetUserType.Attributes.First();
        var badExpressionMapping = new SyncRuleMapping
        {
            Id = 9001,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetCsAttribute,
            TargetConnectedSystemAttributeId = targetCsAttribute.Id
        };
        badExpressionMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 9001,
            Expression = "@@@ not a valid expression @@@",
            Order = 1
        });
        exportRule.AttributeFlowRules.Add(badExpressionMapping);

        // Ensure no CSO exists for this MVO so provisioning (Create) includes all mapped attributes
        ConnectedSystemObjectsData.RemoveAll(cso => cso.MetaverseObjectId == mvo.Id && cso.ConnectedSystemId == targetSystem.Id);

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act + Assert — the failure is surfaced, not swallowed
        var ex = Assert.ThrowsAsync<SyncExpressionEvaluationException>(async () =>
            await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes));
        Assert.That(ex!.TargetAttributeName, Is.EqualTo(targetCsAttribute.Name));
        Assert.That(ex.Expression, Is.EqualTo("@@@ not a valid expression @@@"));
    }

    /// <summary>
    /// End-to-end test: When MVO changes and an existing CSO already exists and is joined,
    /// an Update PendingExport should be generated (not Create).
    /// This tests the scenario where provisioning finds an existing joined object.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenCsoAlreadyExistsAndJoined_CreatesUpdateExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create an existing CSO that is already joined to the MVO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined, // Already joined via import
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-1),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);

        // Configure export rule
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add a mapping so attribute changes are generated
        var displayNameCsAttr = targetUserType.Attributes.SingleOrDefault(a => a.Name == "displayName");
        var displayNameMvAttr = mvUserType.Attributes.SingleOrDefault(a => a.Name == Constants.BuiltInAttributes.DisplayName);

        if (displayNameCsAttr != null && displayNameMvAttr != null)
        {
            exportRule.AttributeFlowRules.Clear();
            var mapping = new SyncRuleMapping
            {
                Id = 1,
                TargetConnectedSystemAttribute = displayNameCsAttr,
                TargetConnectedSystemAttributeId = displayNameCsAttr.Id
            };
            mapping.Sources.Add(new SyncRuleMappingSource
            {
                Id = 1,
                MetaverseAttribute = displayNameMvAttr,
                MetaverseAttributeId = displayNameMvAttr.Id
            });
            exportRule.AttributeFlowRules.Add(mapping);
        }

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No new CSO should be created (only the pre-seeded existingCso should be in SyncRepo)
        // CSOs are now created via SyncRepo.CreateConnectedSystemObjectAsync, not the mock DbSet
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(1), "No new CSO should be created when one already exists");

        // Assert - existing CSO should remain unchanged
        Assert.That(existingCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined),
            "Existing CSO JoinType should remain Joined");
        Assert.That(existingCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            "Existing CSO status should remain Normal");

        // Assert - PendingExport should be Update (not Create) if there are attribute changes
        // Note: May be 0 if no mapped attributes have changes
        if (result.Count > 0)
        {
            Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Update),
                "PendingExport should be Update, not Create, when CSO already exists");
            Assert.That(result[0].ConnectedSystemObject?.Id, Is.EqualTo(existingCso.Id),
                "PendingExport should reference the existing CSO");
        }
    }

    /// <summary>
    /// End-to-end test: When provisioning is disabled on the export rule and no CSO exists,
    /// no CSO or PendingExport should be created.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenProvisioningDisabledAndNoCso_DoesNotCreateCsoOrExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Configure export rule with provisioning DISABLED
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = false; // Provisioning disabled
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Ensure no CSO exists
        ConnectedSystemObjectsData.RemoveAll(cso => cso.MetaverseObjectId == mvo.Id && cso.ConnectedSystemId == targetSystem.Id);

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No CSO should be created when provisioning is disabled
        // CSOs are now created via SyncRepo.CreateConnectedSystemObjectAsync, not the mock DbSet
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(0),
            "No CSO should be created when ProvisionToConnectedSystem is false");

        // Assert - No PendingExport should be created
        Assert.That(result, Has.Count.EqualTo(0),
            "No PendingExport should be created when no CSO exists and provisioning is disabled");
    }

    /// <summary>
    /// End-to-end test: When MVO changes and CSO exists in PendingProvisioning state,
    /// the existing PendingProvisioning CSO should be used (not a new one created).
    /// This tests the scenario where export evaluation runs twice before export execution.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenCsoInPendingProvisioning_UsesExistingCsoAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create an existing CSO in PendingProvisioning state (from previous export evaluation)
        var pendingProvisioningCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.PendingProvisioning, // Not yet exported
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(pendingProvisioningCso);
        SyncRepo.SeedConnectedSystemObject(pendingProvisioningCso);

        // Configure export rule
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No new CSO should be created (should use existing PendingProvisioning CSO)
        // CSOs are now created via SyncRepo.CreateConnectedSystemObjectAsync, not the mock DbSet
        Assert.That(SyncRepo.ConnectedSystemObjects.Count, Is.EqualTo(1),
            "No new CSO should be created when one already exists in PendingProvisioning state");

        // The existing CSO should still be PendingProvisioning
        Assert.That(pendingProvisioningCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "Existing CSO should remain in PendingProvisioning state");

        // If a Pending Export is created, it should be Create (PendingProvisioning means object doesn't exist in target yet)
        if (result.Count > 0)
        {
            Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Create),
                "PendingExport should be Create when CSO is PendingProvisioning (object doesn't exist in target system yet)");
            // Check FK property (not navigation property, which isn't populated to avoid EF Core issues)
            Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(pendingProvisioningCso.Id),
                "PendingExport should reference the existing PendingProvisioning CSO");
        }
    }

    /// <summary>
    /// Q1.Expression: Tests that expression-based export mappings correctly evaluate and create Pending Exports.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WithExpressionBasedMapping_CreatesCorrectPendingExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        // Set up MVO attribute values that will be used in the expression
        var displayNameAttr = mvo.Type.Attributes.Single(a => a.Name == "Display Name");
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Test User"
        });

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Add a DN attribute to the target system
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 999,
            ConnectedSystemObjectType = targetUserType,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        targetUserType.Attributes.Add(dnAttr);

        // Configure export rule with expression-based mapping
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.AttributeFlowRules.Clear();

        // Add expression-based mapping for DN generation
        var expressionMapping = new SyncRuleMapping
        {
            Id = 888,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = dnAttr,
            TargetConnectedSystemAttributeId = dnAttr.Id
        };
        expressionMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 777,
            Order = 1,
            Expression = "\"CN=\" + EscapeDN(mv[\"Display Name\"]) + \",CN=Users,DC=testdomain,DC=local\""
        });
        exportRule.AttributeFlowRules.Add(expressionMapping);

        // CSOs are now created via SyncRepo.CreateConnectedSystemObjectAsync, not the mock DbSet

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1), "Should create one Pending Export");
        var pendingExport = result[0];

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "Should be a Create operation since no CSO exists");

        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(1),
            "Should have one attribute change for the expression-based DN");

        var dnChange = pendingExport.AttributeValueChanges.First();
        Assert.That(dnChange.AttributeId, Is.EqualTo(dnAttr.Id),
            "Attribute change should be for the DN attribute");

        Assert.That(dnChange.StringValue, Is.EqualTo("CN=Test User,CN=Users,DC=testdomain,DC=local"),
            "DN should be correctly generated from the expression using Display Name");

        // Verify a PendingProvisioning CSO was created via SyncRepo
        var createdCsos = SyncRepo.ConnectedSystemObjects.Values.ToList();
        Assert.That(createdCsos, Has.Count.EqualTo(1),
            "Should create one PendingProvisioning CSO for the new provision");
        Assert.That(createdCsos[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "CSO should be in PendingProvisioning state");
    }

    #endregion

    #region Out-of-Scope Deprovisioning Tests

    /// <summary>
    /// Tests that when MVO falls out of scope and OutboundDeprovisionAction is Disconnect,
    /// the CSO is disconnected from MVO but no delete Pending Export is created.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WithDisconnectAction_DisconnectsCsoAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;
        mvo.Origin = MetaverseObjectOrigin.Projected;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO that was previously in scope
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with scoping criteria that MVO no longer matches
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria: EmployeeId = "NOT_MATCHING" (MVO has "E123")
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "NOT_MATCHING_VALUE"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - No delete Pending Export should be created for Disconnect action
        Assert.That(result, Has.Count.EqualTo(0), "Disconnect action should not create a delete Pending Export");

        // Assert - CSO should be disconnected from MVO
        Assert.That(existingCso.MetaverseObject, Is.Null, "CSO should be disconnected from MVO");
        Assert.That(existingCso.MetaverseObjectId, Is.Null, "CSO MetaverseObjectId should be null");
        Assert.That(existingCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined), "CSO JoinType should be NotJoined");

        // Assert - MVO should no longer have the CSO in its collection
        Assert.That(mvo.ConnectedSystemObjects, Does.Not.Contain(existingCso), "MVO should not contain the disconnected CSO");
    }

    /// <summary>
    /// Tests that when MVO falls out of scope and OutboundDeprovisionAction is Delete,
    /// a delete Pending Export is created.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WithDeleteAction_CreatesDeletePendingExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;
        mvo.Origin = MetaverseObjectOrigin.Projected;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO that was previously in scope
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with Delete action
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria that MVO no longer matches
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "NOT_MATCHING_VALUE"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - Delete Pending Export should be created
        Assert.That(result, Has.Count.EqualTo(1), "Delete action should create a delete Pending Export");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete), "Pending Export should be a Delete operation");
        Assert.That(result[0].ConnectedSystemObjectId, Is.EqualTo(existingCso.Id), "Pending Export should reference the CSO");
    }

    /// <summary>
    /// Tests that when MVO is still in scope, no deprovisioning occurs.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenMvoStillInScope_NoDeprovisioningAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with scoping criteria that MVO DOES match
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete; // Even with Delete action
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria that MVO matches (E123)
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "E123"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - No deprovisioning should occur when MVO is in scope
        Assert.That(result, Has.Count.EqualTo(0), "No deprovisioning should occur when MVO is in scope");
        Assert.That(existingCso.MetaverseObject, Is.Not.Null, "CSO should still be joined to MVO");
        Assert.That(existingCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "CSO should still be Joined");
    }

    /// <summary>
    /// Tests that when MVO falls out of scope and is the last connector,
    /// LastConnectorDisconnectedDate is set on the MVO for Disconnect action.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenLastConnectorDisconnected_SetsLastConnectorDateAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvUserType.DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected;
        mvo.Type = mvUserType;
        mvo.Origin = MetaverseObjectOrigin.Projected;
        mvo.LastConnectorDisconnectedDate = null;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO - this will be the only connector
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);
        mvo.ConnectedSystemObjects.Clear();
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with Disconnect action
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria that MVO no longer matches
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "NOT_MATCHING_VALUE"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - LastConnectorDisconnectedDate should be set
        Assert.That(mvo.LastConnectorDisconnectedDate, Is.Not.Null, "LastConnectorDisconnectedDate should be set when last connector disconnected");
        Assert.That(mvo.ConnectedSystemObjects, Has.Count.EqualTo(0), "MVO should have no connectors after disconnect");
    }

    #endregion

    #region Create vs Update Attribute Inclusion Tests

    /// <summary>
    /// Tests that Create operations include ALL mapped attributes, not just changed ones.
    /// This ensures new objects are fully provisioned with all their attribute values.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_ForCreateOperation_IncludesAllMappedAttributesAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        // Set up multiple MVO attribute values
        var displayNameAttr = mvo.Type.Attributes.Single(a => a.Name == "Display Name");
        var employeeIdAttr = mvo.Type.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        mvo.AttributeValues.Clear();
        var displayNameValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Test User"
        };
        var employeeIdValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "E12345"
        };
        mvo.AttributeValues.Add(displayNameValue);
        mvo.AttributeValues.Add(employeeIdValue);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Add target attributes
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1001,
            ConnectedSystemObjectType = targetUserType,
            Name = "displayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var targetEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1002,
            ConnectedSystemObjectType = targetUserType,
            Name = "employeeID",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        targetUserType.Attributes.Add(targetDisplayNameAttr);
        targetUserType.Attributes.Add(targetEmployeeIdAttr);

        // Configure export rule with multiple mappings
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.AttributeFlowRules.Clear();

        // Add mapping for displayName
        var displayNameMapping = new SyncRuleMapping
        {
            Id = 2001,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id
        };
        displayNameMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 3001,
            Order = 1,
            MetaverseAttribute = displayNameAttr,
            MetaverseAttributeId = displayNameAttr.Id
        });
        exportRule.AttributeFlowRules.Add(displayNameMapping);

        // Add mapping for employeeID
        var employeeIdMapping = new SyncRuleMapping
        {
            Id = 2002,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = targetEmployeeIdAttr.Id
        };
        employeeIdMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 3002,
            Order = 1,
            MetaverseAttribute = employeeIdAttr,
            MetaverseAttributeId = employeeIdAttr.Id
        });
        exportRule.AttributeFlowRules.Add(employeeIdMapping);

        // Ensure no CSO exists (so this will be a Create)
        ConnectedSystemObjectsData.RemoveAll(cso => cso.MetaverseObjectId == mvo.Id && cso.ConnectedSystemId == targetSystem.Id);

        // Track CSOs created
        MockDbSetConnectedSystemObjects.Setup(set => set.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject entity) =>
            {
                if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
                ConnectedSystemObjectsData.Add(entity);
            });

        // Only pass ONE changed attribute (displayName), but both should be included for Create
        var changedAttributes = new List<MetaverseObjectAttributeValue> { displayNameValue };

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1), "Should create one Pending Export");
        var pendingExport = result[0];

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "Should be a Create operation since no CSO exists");

        // CRITICAL: For Create, ALL mapped attributes should be included, not just the changed one
        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(2),
            "Create operation should include ALL mapped attributes (2), not just changed ones (1)");

        var displayNameChange = pendingExport.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == targetDisplayNameAttr.Id);
        var employeeIdChange = pendingExport.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == targetEmployeeIdAttr.Id);

        Assert.That(displayNameChange, Is.Not.Null, "displayName attribute should be included");
        Assert.That(displayNameChange!.StringValue, Is.EqualTo("Test User"));

        Assert.That(employeeIdChange, Is.Not.Null, "employeeID attribute should be included (even though not in changedAttributes)");
        Assert.That(employeeIdChange!.StringValue, Is.EqualTo("E12345"));
    }

    /// <summary>
    /// Tests that Update operations include ONLY the changed attributes, not all mapped attributes.
    /// This ensures updates are efficient and only modify what actually changed.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_ForUpdateOperation_IncludesOnlyChangedAttributesAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        // Set up multiple MVO attribute values
        var displayNameAttr = mvo.Type.Attributes.Single(a => a.Name == "Display Name");
        var employeeIdAttr = mvo.Type.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        mvo.AttributeValues.Clear();
        var displayNameValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Updated User Name"
        };
        var employeeIdValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "E12345"
        };
        mvo.AttributeValues.Add(displayNameValue);
        mvo.AttributeValues.Add(employeeIdValue);

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Add target attributes
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1001,
            ConnectedSystemObjectType = targetUserType,
            Name = "displayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var targetEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1002,
            ConnectedSystemObjectType = targetUserType,
            Name = "employeeID",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        targetUserType.Attributes.Add(targetDisplayNameAttr);
        targetUserType.Attributes.Add(targetEmployeeIdAttr);

        // Create an existing CSO (so this will be an Update, not Create)
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-1),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);

        // Configure export rule with multiple mappings
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.AttributeFlowRules.Clear();

        // Add mapping for displayName
        var displayNameMapping = new SyncRuleMapping
        {
            Id = 2001,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id
        };
        displayNameMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 3001,
            Order = 1,
            MetaverseAttribute = displayNameAttr,
            MetaverseAttributeId = displayNameAttr.Id
        });
        exportRule.AttributeFlowRules.Add(displayNameMapping);

        // Add mapping for employeeID
        var employeeIdMapping = new SyncRuleMapping
        {
            Id = 2002,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = targetEmployeeIdAttr.Id
        };
        employeeIdMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 3002,
            Order = 1,
            MetaverseAttribute = employeeIdAttr,
            MetaverseAttributeId = employeeIdAttr.Id
        });
        exportRule.AttributeFlowRules.Add(employeeIdMapping);

        // Only pass ONE changed attribute (displayName)
        // For Update, only this one should be included
        var changedAttributes = new List<MetaverseObjectAttributeValue> { displayNameValue };

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1), "Should create one Pending Export");
        var pendingExport = result[0];

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "Should be an Update operation since CSO exists");

        // CRITICAL: For Update, ONLY the changed attribute should be included
        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(1),
            "Update operation should include ONLY changed attributes (1), not all mapped ones (2)");

        var displayNameChange = pendingExport.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == targetDisplayNameAttr.Id);
        var employeeIdChange = pendingExport.AttributeValueChanges.FirstOrDefault(c => c.AttributeId == targetEmployeeIdAttr.Id);

        Assert.That(displayNameChange, Is.Not.Null, "displayName attribute should be included (it changed)");
        Assert.That(displayNameChange!.StringValue, Is.EqualTo("Updated User Name"));

        Assert.That(employeeIdChange, Is.Null, "employeeID attribute should NOT be included (it didn't change)");
    }

    /// <summary>
    /// Tests that Update operations with NO changed attributes that have mappings
    /// do not create a Pending Export.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_ForUpdateWithNoMappedChanges_DoesNotCreateExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create an existing CSO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-1),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        SyncRepo.SeedConnectedSystemObject(existingCso);

        // Configure export rule with a mapping for displayName
        var displayNameAttr = mvo.Type.Attributes.Single(a => a.Name == "Display Name");
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1001,
            ConnectedSystemObjectType = targetUserType,
            Name = "displayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        targetUserType.Attributes.Add(targetDisplayNameAttr);

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.AttributeFlowRules.Clear();

        // Add mapping for displayName only
        var displayNameMapping = new SyncRuleMapping
        {
            Id = 2001,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id
        };
        displayNameMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 3001,
            Order = 1,
            MetaverseAttribute = displayNameAttr,
            MetaverseAttributeId = displayNameAttr.Id
        });
        exportRule.AttributeFlowRules.Add(displayNameMapping);

        // Pass an attribute that is NOT mapped (Employee ID changed, but it's not in the export rule mappings)
        var employeeIdAttr = mvo.Type.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var unmappedChangedValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "NewEmployeeId"
        };
        var changedAttributes = new List<MetaverseObjectAttributeValue> { unmappedChangedValue };

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No Pending Export should be created for Update with no mapped attribute changes
        Assert.That(result, Has.Count.EqualTo(0),
            "No Pending Export should be created when changed attributes don't have export mappings");
    }

    #endregion
}
