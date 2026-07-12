// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Pins the merge-and-replace behaviour of
/// <c>ExportEvaluationServer.CreateOrUpdatePendingExportWithNoNetChangeAsync</c> (the
/// "GetPendingExportByCsoIdForMerge" fallback branch, issue #986) end-to-end via the public
/// <see cref="JIM.Application.Servers.ExportEvaluationServer.EvaluateExportRulesWithNoNetChangeDetectionAsync"/>
/// entry point. These characterise the merge outcomes that must stay byte-identical regardless of
/// which repository method (<c>GetPendingExportByConnectedSystemObjectIdAsync</c> or the lean
/// <c>GetPendingExportByConnectedSystemObjectIdForMergeAsync</c>) supplies the existing database
/// Pending Export - synchronisation integrity depends on the merge/delete-and-recreate outcome being
/// unaffected by the fetch-shape optimisation.
/// </summary>
/// <remarks>
/// The <see cref="JIM.InMemoryData.SyncRepository"/> fake used here has no Include-shape concept (see
/// its own <c>GetPendingExportByConnectedSystemObjectIdForMergeAsync</c> comment), so these tests
/// cannot themselves distinguish the heavy fetch from the lean one - that distinction is proven
/// separately, against real PostgreSQL, in <c>PendingExportMergeFetchDatabaseTests</c>. What these
/// tests pin is the merge *outcome*: the business logic these two fetch methods feed.
/// </remarks>
public class PendingExportMergeSemanticsTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private List<ConnectedSystemObjectAttributeValue> ConnectedSystemObjectAttributeValuesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectAttributeValue>> MockDbSetConnectedSystemObjectAttributeValues { get; set; } = null!;
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

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        var mockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        ConnectedSystemObjectAttributeValuesData = new List<ConnectedSystemObjectAttributeValue>();
        MockDbSetConnectedSystemObjectAttributeValues = ConnectedSystemObjectAttributeValuesData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectAttributeValues).Returns(MockDbSetConnectedSystemObjectAttributeValues.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(mockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        SyncRepo = TestUtilities.CreateSyncRepository();
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    /// <summary>
    /// Builds a single-valued export Synchronisation Rule mapping Employee ID (source) to the
    /// target system's Employee ID attribute, plus a confirmed (Normal-status) target CSO and its
    /// export evaluation cache. Shared arrangement for the single-valued pinning scenarios.
    /// </summary>
    private (SyncRule ExportSyncRule, MetaverseObject Mvo, ConnectedSystemObject TargetCso,
        ConnectedSystemObjectTypeAttribute TargetEmployeeIdAttr, ExportEvaluationCache Cache) ArrangeSingleValuedScenario(
        ConnectedSystemObjectStatus targetCsoStatus = ConnectedSystemObjectStatus.Normal)
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var employeeIdMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var targetEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());

        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.MetaverseObjectTypeId = mvUserType.Id;
        exportSyncRule.AttributeFlowRules.Clear();
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = targetEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 200,
                Order = 0,
                MetaverseAttribute = employeeIdMvAttr,
                MetaverseAttributeId = employeeIdMvAttr.Id
            }}
        });

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            Status = targetCsoStatus,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo
        };

        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>> { { mvUserType.Id, new List<SyncRule> { exportSyncRule } } };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> { { (mvo.Id, targetSystem.Id), targetCso } };
        var csoAttributeValues = Enumerable.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        var cache = new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup, csoAttributeValues, new List<int> { targetSystem.Id });

        return (exportSyncRule, mvo, targetCso, targetEmployeeIdAttr, cache);
    }

    private static PendingExportAttributeValueChange CreateChange(
        ConnectedSystemObjectTypeAttribute attribute, PendingExportAttributeChangeType changeType, string stringValue) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = attribute,
        AttributeId = attribute.Id,
        StringValue = stringValue,
        ChangeType = changeType
    };

    /// <summary>
    /// Merge into a database Pending Export that has both an export-eval-overlapping change and a
    /// drift-only change: the drift-only change on an unrelated attribute must survive the merge
    /// alongside the fresh export evaluation change.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_ExistingDbPendingExportWithDriftOnlyChange_MergesBothAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();
        var driftOnlyAttr = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER")
            .Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var dbPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetCso.ConnectedSystem,
            ConnectedSystemId = targetCso.ConnectedSystemId,
            ConnectedSystemObject = targetCso,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        dbPendingExport.AttributeValueChanges.Add(CreateChange(driftOnlyAttr, PendingExportAttributeChangeType.Update, "Drift Display Name"));
        SyncRepo.SeedPendingExport(dbPendingExport);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E999";
        var changedAttributes = new List<MetaverseObjectAttributeValue> { newEmployeeIdValue };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem: null, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var merged = result.PendingExports.Single();
        Assert.That(merged.Id, Is.Not.EqualTo(dbPendingExport.Id), "Delete-and-recreate must produce a new PE, not reuse the old one's Id.");
        Assert.That(merged.AttributeValueChanges, Has.Count.EqualTo(2), "Export eval change plus the surviving drift-only change.");
        Assert.That(merged.AttributeValueChanges.Any(c => c.AttributeId == targetEmployeeIdAttr.Id && c.StringValue == "E999"), Is.True,
            "Fresh export evaluation change must be present.");
        Assert.That(merged.AttributeValueChanges.Any(c => c.AttributeId == driftOnlyAttr.Id && c.StringValue == "Drift Display Name"), Is.True,
            "Drift-only change on an unrelated attribute must survive the merge.");
    }

    /// <summary>
    /// A stale database Pending Export left over from before the CSO's secondary external ID was
    /// confirmed (Status transitioned PendingProvisioning -> Normal) has ChangeType Create. Once the
    /// CSO is confirmed, the merge-and-replace must produce a PE typed Update (the freshly computed
    /// changeType), not silently keep the stale PE's Create typing.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_StaleCreatePendingExportOnConfirmedCso_ReplacementIsTypedUpdateAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario(ConnectedSystemObjectStatus.Normal);

        var dbPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetCso.ConnectedSystem,
            ConnectedSystemId = targetCso.ConnectedSystemId,
            ConnectedSystemObject = targetCso,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        dbPendingExport.AttributeValueChanges.Add(CreateChange(targetEmployeeIdAttr, PendingExportAttributeChangeType.Update, "E123"));
        SyncRepo.SeedPendingExport(dbPendingExport);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E456";
        var changedAttributes = new List<MetaverseObjectAttributeValue> { newEmployeeIdValue };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem: null, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var merged = result.PendingExports.Single();
        Assert.That(merged.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "CSO is confirmed (Normal status): the replacement PE must reflect the current evaluation (Update), not the stale DB PE's Create typing.");
        Assert.That(merged.AttributeValueChanges.Single().StringValue, Is.EqualTo("E456"));
    }

    /// <summary>
    /// A stale database Pending Export has an old value for a single-valued attribute; the fresh
    /// export evaluation supersedes it with a new value. Only the new value must survive - this is
    /// the exact shape that caused the LDAP "SINGLE-VALUE attribute specified more than once" error
    /// before merge-key deduplication existed.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_SupersedingChangeOnSameAttribute_OnlyNewValueSurvivesAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();

        var dbPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetCso.ConnectedSystem,
            ConnectedSystemId = targetCso.ConnectedSystemId,
            ConnectedSystemObject = targetCso,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        dbPendingExport.AttributeValueChanges.Add(CreateChange(targetEmployeeIdAttr, PendingExportAttributeChangeType.Update, "E123-OLD"));
        SyncRepo.SeedPendingExport(dbPendingExport);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E123-NEW";
        var changedAttributes = new List<MetaverseObjectAttributeValue> { newEmployeeIdValue };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem: null, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var merged = result.PendingExports.Single();
        Assert.That(merged.AttributeValueChanges, Has.Count.EqualTo(1),
            "Single-valued attribute merge keys by attribute ID only: the stale value must be replaced, not accumulated.");
        Assert.That(merged.AttributeValueChanges.Single().StringValue, Is.EqualTo("E123-NEW"));
    }

    /// <summary>
    /// Multi-valued reference add + remove sequence: the database Pending Export has drift Remove
    /// entries for two members; export evaluation re-adds one of them (should win, replacing the
    /// drift Remove with an Add for that value) and adds a brand-new member (distinct value,
    /// preserved alongside the untouched drift Remove for the other member).
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_MultiValuedReferenceAddAndRemoveSequence_MergesAtValueLevelAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvGroupType = MetaverseObjectTypesData.Single(q => q.Name == "Group");

        var mvMemberAttr = new MetaverseAttribute
        {
            Id = 9001, Name = "TestMember", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, BuiltIn = false
        };
        mvGroupType.Attributes.Add(mvMemberAttr);

        var targetMemberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 9002, Name = "member", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, ConnectedSystemObjectType = targetUserType, Selected = true
        };
        targetUserType.Attributes.Add(targetMemberAttr);

        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.MetaverseObjectTypeId = mvGroupType.Id;
        exportSyncRule.AttributeFlowRules.Clear();
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetMemberAttr,
            TargetConnectedSystemAttributeId = targetMemberAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 200,
                Order = 0,
                MetaverseAttribute = mvMemberAttr,
                MetaverseAttributeId = mvMemberAttr.Id
            }}
        });

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvGroupType };
        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo
        };

        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>> { { mvGroupType.Id, new List<SyncRule> { exportSyncRule } } };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> { { (mvo.Id, targetSystem.Id), targetCso } };
        var csoAttributeValues = Enumerable.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        var cache = new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup, csoAttributeValues, new List<int> { targetSystem.Id });

        // Database PE: drift-detected removals for Alice and Bob.
        var dbPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetCso.ConnectedSystem,
            ConnectedSystemId = targetCso.ConnectedSystemId,
            ConnectedSystemObject = targetCso,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        dbPendingExport.AttributeValueChanges.Add(CreateChange(targetMemberAttr, PendingExportAttributeChangeType.Remove, "CN=Alice,DC=test"));
        dbPendingExport.AttributeValueChanges.Add(CreateChange(targetMemberAttr, PendingExportAttributeChangeType.Remove, "CN=Bob,DC=test"));
        SyncRepo.SeedPendingExport(dbPendingExport);

        // Export evaluation: Alice is re-added (should win over the drift Remove), Carol is a new member.
        var aliceValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = mvMemberAttr, AttributeId = mvMemberAttr.Id,
            StringValue = "CN=Alice,DC=test"
        };
        var carolValue = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = mvMemberAttr, AttributeId = mvMemberAttr.Id,
            StringValue = "CN=Carol,DC=test"
        };
        mvo.AttributeValues.Add(aliceValue);
        mvo.AttributeValues.Add(carolValue);
        var changedAttributes = new List<MetaverseObjectAttributeValue> { aliceValue, carolValue };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem: null, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var merged = result.PendingExports.Single();
        Assert.That(merged.AttributeValueChanges, Has.Count.EqualTo(3),
            "Alice (superseded to Add), Carol (new Add) and Bob (untouched drift Remove) are all distinct values.");

        var aliceChange = merged.AttributeValueChanges.Single(c => c.StringValue == "CN=Alice,DC=test");
        Assert.That(aliceChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Add),
            "Export eval re-adding Alice must win over the drift Remove for the same value.");

        var bobChange = merged.AttributeValueChanges.Single(c => c.StringValue == "CN=Bob,DC=test");
        Assert.That(bobChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove),
            "Bob's drift Remove is untouched by this export evaluation and must survive unchanged.");

        var carolChange = merged.AttributeValueChanges.Single(c => c.StringValue == "CN=Carol,DC=test");
        Assert.That(carolChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Add));
    }

    /// <summary>
    /// No existing database Pending Export for the CSO: the create path must be entirely unaffected
    /// by the merge-fetch change (no merge/delete/replace branch is taken at all).
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_NoExistingDbPendingExport_CreatesNewWithoutMergeAsync()
    {
        var (_, mvo, _, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E777";
        var changedAttributes = new List<MetaverseObjectAttributeValue> { newEmployeeIdValue };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem: null, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var created = result.PendingExports.Single();
        Assert.That(created.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(created.AttributeValueChanges, Has.Count.EqualTo(1));
        Assert.That(created.AttributeValueChanges.Single().AttributeId, Is.EqualTo(targetEmployeeIdAttr.Id));
        Assert.That(created.AttributeValueChanges.Single().StringValue, Is.EqualTo("E777"));
    }
}
