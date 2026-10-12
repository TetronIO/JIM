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
/// <c>GetPendingExportLightweightByConnectedSystemObjectIdAsync</c>) supplies the existing database
/// Pending Export - synchronisation integrity depends on the merge/delete-and-recreate outcome being
/// unaffected by the fetch-shape optimisation.
/// </summary>
/// <remarks>
/// The <see cref="JIM.InMemoryData.SyncRepository"/> fake used here has no Include-shape concept (see
/// its own <c>GetPendingExportLightweightByConnectedSystemObjectIdAsync</c> comment), so these tests
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

    /// <summary>
    /// A group export Synchronisation Rule flowing a multi-valued member attribute to a joined target object that
    /// already holds <paramref name="targetMembers"/>, for a Metaverse Object whose members are
    /// <paramref name="mvoMembers"/>, all of them reported as changed.
    /// </summary>
    private (SyncRule ExportRule, MetaverseObject Mvo, ConnectedSystemObject TargetCso, ConnectedSystemObjectTypeAttribute MemberAttr,
        ExportEvaluationCache Cache, List<MetaverseObjectAttributeValue> ChangedAttributes) ArrangeMembershipScenario(
        string[] targetMembers, string[] mvoMembers)
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

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.MetaverseObjectTypeId = mvGroupType.Id;
        exportRule.AttributeFlowRules.Clear();
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetMemberAttr,
            TargetConnectedSystemAttributeId = targetMemberAttr.Id,
            Sources = { new SyncRuleMappingSource { Id = 200, Order = 0, MetaverseAttribute = mvMemberAttr, MetaverseAttributeId = mvMemberAttr.Id } }
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
        var targetValues = targetMembers.Select(member => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = targetCso, Attribute = targetMemberAttr, AttributeId = targetMemberAttr.Id, StringValue = member
        }).ToList();
        targetCso.AttributeValues.AddRange(targetValues);

        var changedAttributes = mvoMembers.Select(member => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = mvMemberAttr, AttributeId = mvMemberAttr.Id, StringValue = member
        }).ToList();
        mvo.AttributeValues.AddRange(changedAttributes);

        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>> { { mvGroupType.Id, new List<SyncRule> { exportRule } } };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> { { (mvo.Id, targetSystem.Id), targetCso } };
        var csoAttributeValues = targetValues.ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        var cache = new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup, csoAttributeValues, new List<int> { targetSystem.Id });

        return (exportRule, mvo, targetCso, targetMemberAttr, cache, changedAttributes);
    }

    private static PendingExport NewUpdatePendingExport(ConnectedSystemObject targetCso) => new()
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
            mvo, changedAttributes, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var merged = result.PendingExports.Single();
        Assert.That(merged.Id, Is.EqualTo(dbPendingExport.Id),
            "A replacement for an export still waiting keeps its id, so every earlier run's outcome still names it (#2100).");
        Assert.That(merged.AttributeValueChanges, Has.Count.EqualTo(2), "Export eval change plus the surviving drift-only change.");
        Assert.That(merged.AttributeValueChanges.Any(c => c.AttributeId == targetEmployeeIdAttr.Id && c.StringValue == "E999"), Is.True,
            "Fresh export evaluation change must be present.");
        Assert.That(merged.AttributeValueChanges.Any(c => c.AttributeId == driftOnlyAttr.Id && c.StringValue == "Drift Display Name"), Is.True,
            "Drift-only change on an unrelated attribute must survive the merge.");
    }

    /// <summary>
    /// A surviving change keeps its place in the export lifecycle through the delete-and-recreate (#2039 Phase 0).
    /// One already sent and awaiting its confirming import must not go back to Pending, or it is sent again with its
    /// attempt count restarted: a re-sent multi-valued Add then meets a value already present.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_SurvivingChangeAwaitingConfirmation_KeepsItsStatusAttemptsAndTimesAsync()
    {
        var (_, mvo, targetCso, _, cache) = ArrangeSingleValuedScenario();
        var survivingAttr = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER")
            .Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var exportedAt = new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);

        var dbPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetCso.ConnectedSystem,
            ConnectedSystemId = targetCso.ConnectedSystemId,
            ConnectedSystemObject = targetCso,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow
        };
        var awaitingConfirmation = CreateChange(survivingAttr, PendingExportAttributeChangeType.Update, "Sent Display Name");
        awaitingConfirmation.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        awaitingConfirmation.ExportAttemptCount = 2;
        awaitingConfirmation.LastExportedAt = exportedAt;
        awaitingConfirmation.LastImportedValue = "Old Display Name";
        dbPendingExport.AttributeValueChanges.Add(awaitingConfirmation);
        SyncRepo.SeedPendingExport(dbPendingExport);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E999";

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, [newEmployeeIdValue], cache);

        var survivor = result.PendingExports.Single().AttributeValueChanges.Single(c => c.AttributeId == survivingAttr.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(survivor.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "A change already sent must not be re-sent because another attribute changed");
            Assert.That(survivor.ExportAttemptCount, Is.EqualTo(2));
            Assert.That(survivor.LastExportedAt, Is.EqualTo(exportedAt));
            Assert.That(survivor.LastImportedValue, Is.EqualTo("Old Display Name"));
        }
    }

    /// <summary>
    /// A held change (written with an interim value until an Initial Password is delivered, #2039 Phase 2) survives a
    /// rebuild caused by an unrelated attribute with its hold intact, or the next import would judge it as an
    /// ordinary change, retry it, and Active Directory would refuse to enable an account that has no password.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_HeldChangeSurvivesAnUnrelatedChange_KeepsAwaitsInitialPasswordAsync()
    {
        var (_, mvo, targetCso, _, cache) = ArrangeSingleValuedScenario();
        var heldAttr = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER")
            .Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var dbPendingExport = SeedExportedPendingExport(targetCso, HeldChange(heldAttr, "Enabled"));

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E999";

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, [newEmployeeIdValue], cache);

        var rebuilt = result.PendingExports.Single();
        Assert.That(rebuilt.Id, Is.Not.EqualTo(dbPendingExport.Id), "precondition: the merge-replace branch rebuilt the export");
        var survivor = rebuilt.AttributeValueChanges.Single(c => c.AttributeId == heldAttr.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(survivor.AwaitsInitialPassword, Is.True);
            Assert.That(survivor.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation));
        }
    }

    /// <summary>
    /// Re-evaluation restating exactly the value a held change already asserts is no change at all: the incoming
    /// change is dropped and the held one kept, so the Synchronisation Rule's enabled value is not re-sent to an
    /// account that still has no password (#2039 Phase 2). Expression mappings are re-evaluated on any Metaverse
    /// Object change, so this is the ordinary path for every HR change between creation and the password's delivery.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_IncomingChangeEqualToAHeldChange_KeepsTheHeldChangeAndDropsTheIncomingOneAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();
        var held = HeldChange(targetEmployeeIdAttr, "E999");
        SeedExportedPendingExport(targetCso, held);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        employeeIdValue.StringValue = "E999";

        await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, [employeeIdValue], cache);

        var changesForAttribute = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObjectId == targetCso.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.AttributeId == targetEmployeeIdAttr.Id)
            .ToList();
        Assert.That(changesForAttribute, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changesForAttribute[0].Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedPendingConfirmation),
                "nothing for this attribute may be queued to send");
            Assert.That(changesForAttribute[0].AwaitsInitialPassword, Is.True);
            Assert.That(changesForAttribute[0].ExportAttemptCount, Is.EqualTo(held.ExportAttemptCount));
        }
    }

    /// <summary>
    /// When every newly evaluated change merely restates a held change and nothing queued is withdrawn, the evaluation
    /// changes nothing, so it leaves the persisted Pending Export exactly as it is, as the no-change path does. Deleting
    /// and recreating it would re-stamp the item that queued it over the provisioning that did, and report a staged
    /// export on every synchronisation until the Initial Password arrives.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_OnlyRestatementsOfAHeldChange_LeavesThePersistedPendingExportUntouchedAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();
        var held = HeldChange(targetEmployeeIdAttr, "E999");
        var persisted = SeedExportedPendingExport(targetCso, held);
        var provisioningItemId = Guid.NewGuid();
        persisted.RecordStager(provisioningItemId);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        employeeIdValue.StringValue = "E999";

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, [employeeIdValue], cache);

        var forObject = SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == targetCso.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PendingExports, Is.Empty, "nothing is staged: the evaluation changed nothing");
            Assert.That(forObject.Select(pe => pe.Id), Is.EqualTo(new[] { persisted.Id }), "the persisted Pending Export is kept, not rebuilt");
            Assert.That(forObject.Single().QueuedByRunProfileExecutionItemId, Is.EqualTo(provisioningItemId),
                "the item that queued it still names its cause");
            Assert.That(forObject.Single().StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { provisioningItemId }),
                "an evaluation that stages nothing is not a stager");
            Assert.That(forObject.Single().AttributeValueChanges, Is.EquivalentTo(new[] { held }));
        }
    }

    /// <summary>
    /// The leaver during the hold (#2039): the held change still asserts the enabled value, but the person has left, so
    /// the Synchronisation Rule now yields the disabled value, which the target already holds because the account was
    /// created disabled. The held change no longer reflects the Metaverse, so it is withdrawn like any queued change the
    /// target makes unnecessary. Kept, it would be released once the password is delivered or expires, and the next
    /// export would enable the leaver's account.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_LeaverDuringTheHoldWhenTheTargetHoldsTheInterimValue_WithdrawsTheHeldChangeAsync()
    {
        var (exportRule, mvo, targetCso, targetEmployeeIdAttr, _) = ArrangeSingleValuedScenario();
        var interim = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = targetCso, Attribute = targetEmployeeIdAttr, AttributeId = targetEmployeeIdAttr.Id,
            StringValue = "DISABLED"
        };
        targetCso.AttributeValues.Add(interim);
        var cache = new ExportEvaluationCache(
            new Dictionary<int, List<SyncRule>> { { mvo.Type!.Id, new List<SyncRule> { exportRule } } },
            new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> { { (mvo.Id, targetCso.ConnectedSystemId), targetCso } },
            new[] { interim }.ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId)),
            new List<int> { targetCso.ConnectedSystemId });
        SeedExportedPendingExport(targetCso, HeldChange(targetEmployeeIdAttr, "ENABLED"));

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        employeeIdValue.StringValue = "DISABLED";

        await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, [employeeIdValue], cache);

        var changesForAttribute = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObjectId == targetCso.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.AttributeId == targetEmployeeIdAttr.Id);
        Assert.That(changesForAttribute, Is.Empty, "nothing remains that would enable the leaver's account");
    }

    /// <summary>
    /// The Metaverse Object now wants a different value from the one held (the account was disabled before its
    /// password arrived): that is a genuine change, so it supersedes the held one exactly as any newer evaluation
    /// supersedes a staged change.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_IncomingChangeDifferentFromAHeldChange_SupersedesTheHeldChangeAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();
        SeedExportedPendingExport(targetCso, HeldChange(targetEmployeeIdAttr, "E999"));

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        employeeIdValue.StringValue = "E555";

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, [employeeIdValue], cache);

        var change = result.PendingExports.Single().AttributeValueChanges.Single(c => c.AttributeId == targetEmployeeIdAttr.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.StringValue, Is.EqualTo("E555"));
            Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
            Assert.That(change.AwaitsInitialPassword, Is.False);
        }
    }

    /// <summary>
    /// The guard is inert for a change that is not held: an equal incoming change supersedes an ordinary change
    /// awaiting confirmation exactly as before.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_IncomingChangeEqualToAnUnheldChangeAwaitingConfirmation_SupersedesItAsBeforeAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();
        var unheld = HeldChange(targetEmployeeIdAttr, "E999");
        unheld.AwaitsInitialPassword = false;
        SeedExportedPendingExport(targetCso, unheld);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        employeeIdValue.StringValue = "E999";

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, [employeeIdValue], cache);

        var change = result.PendingExports.Single().AttributeValueChanges.Single(c => c.AttributeId == targetEmployeeIdAttr.Id);
        Assert.That(change.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending));
    }

    /// <summary>
    /// The in-memory merge branch applies the same guard: a Pending Export already staged on this page (one the
    /// merge-replace branch rebuilt, carrying a held change) keeps that change against an equal restatement.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_InMemoryMergeIntoAnExportHoldingAChange_KeepsItAgainstAnEqualRestatementAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();
        var held = HeldChange(targetEmployeeIdAttr, "E999");
        var stagedOnThisPage = NewUpdatePendingExport(targetCso);
        stagedOnThisPage.AttributeValueChanges.Add(held);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        employeeIdValue.StringValue = "E999";

        await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, [employeeIdValue], cache, deferSave: true, existingPendingExports: [stagedOnThisPage]);

        Assert.That(stagedOnThisPage.AttributeValueChanges.Where(c => c.AttributeId == targetEmployeeIdAttr.Id), Is.EquivalentTo(new[] { held }),
            "the held change stands and nothing for its attribute is queued to send");
    }

    /// <summary>
    /// A change sent with an interim value and awaiting its confirming import while an Initial Password is owed
    /// (#2039 Phase 2).
    /// </summary>
    private static PendingExportAttributeValueChange HeldChange(ConnectedSystemObjectTypeAttribute attribute, string value)
    {
        var change = CreateChange(attribute, PendingExportAttributeChangeType.Update, value);
        change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        change.ExportAttemptCount = 1;
        change.LastExportedAt = new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);
        change.AwaitsInitialPassword = true;
        return change;
    }

    private PendingExport SeedExportedPendingExport(ConnectedSystemObject targetCso, params PendingExportAttributeValueChange[] changes)
    {
        var pendingExport = NewUpdatePendingExport(targetCso);
        pendingExport.Status = PendingExportStatus.Exported;
        pendingExport.AttributeValueChanges.AddRange(changes);
        SyncRepo.SeedPendingExport(pendingExport);
        return pendingExport;
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
            mvo, changedAttributes, cache);

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
            mvo, changedAttributes, cache);

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
            mvo, changedAttributes, cache);

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
    /// The in-memory merge path (#2001): a Pending Export staged earlier this page queues the removal of a member the
    /// target still holds, and the Metaverse now wants that member again while adding another. The queued removal is
    /// withdrawn from the staged export and reported, so the run can record it; the new member is merged in.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_InMemoryPendingExportQueuesARemovalTheTargetNoLongerNeeds_ReportsTheWithdrawalAsync()
    {
        var (exportRule, mvo, targetCso, memberAttr, cache, changedAttributes) = ArrangeMembershipScenario(
            targetMembers: ["CN=Bob,DC=test"], mvoMembers: ["CN=Bob,DC=test", "CN=Carol,DC=test"]);
        var staged = NewUpdatePendingExport(targetCso);
        staged.AttributeValueChanges.Add(CreateChange(memberAttr, PendingExportAttributeChangeType.Remove, "CN=Bob,DC=test"));
        var inMemoryBatch = new List<PendingExport> { staged };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, cache, deferSave: true, existingPendingExports: inMemoryBatch);

        var withdrawal = result.Withdrawals.SingleOrDefault();
        Assert.That(withdrawal, Is.Not.Null, "the withdrawal is reported");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawal!.ConnectedSystemObjectId, Is.EqualTo(targetCso.Id));
            Assert.That(withdrawal!.ConnectedSystemId, Is.EqualTo(targetCso.ConnectedSystemId));
            Assert.That(withdrawal!.SyncRuleId, Is.EqualTo(exportRule.Id));
            Assert.That(withdrawal!.SyncRuleName, Is.EqualTo(exportRule.Name));
            Assert.That(withdrawal!.MetaverseObjectId, Is.EqualTo(mvo.Id));
            Assert.That(withdrawal!.Changes.Select(c => $"{c.ChangeType} {c.StringValue}"), Is.EqualTo(new[] { "Remove CN=Bob,DC=test" }));
            Assert.That(staged.AttributeValueChanges.Select(c => $"{c.ChangeType} {c.StringValue}"), Is.EqualTo(new[] { "Add CN=Carol,DC=test" }),
                "arrange check: the removal was withdrawn and the new member merged in");
        }
    }

    /// <summary>
    /// The persisted merge path (#2001): of three queued removals, one is for a member the target still holds and the
    /// Metaverse wants again (withdrawn), one is superseded by a newly staged add for the same member (replaced, which
    /// is not a withdrawal) and one is untouched. Only the first is reported.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_PersistedPendingExportMerge_ReportsOnlyTheChangesWithdrawnNotThoseSupersededAsync()
    {
        var (_, mvo, targetCso, memberAttr, cache, changedAttributes) = ArrangeMembershipScenario(
            targetMembers: ["CN=Bob,DC=test"], mvoMembers: ["CN=Bob,DC=test", "CN=Carol,DC=test"]);
        var queued = NewUpdatePendingExport(targetCso);
        queued.AttributeValueChanges.Add(CreateChange(memberAttr, PendingExportAttributeChangeType.Remove, "CN=Bob,DC=test"));
        queued.AttributeValueChanges.Add(CreateChange(memberAttr, PendingExportAttributeChangeType.Remove, "CN=Carol,DC=test"));
        queued.AttributeValueChanges.Add(CreateChange(memberAttr, PendingExportAttributeChangeType.Remove, "CN=Dave,DC=test"));
        SyncRepo.SeedPendingExport(queued);

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, changedAttributes, cache);

        var withdrawal = result.Withdrawals.SingleOrDefault();
        Assert.That(withdrawal, Is.Not.Null, "the withdrawal is reported");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawal!.ConnectedSystemObjectId, Is.EqualTo(targetCso.Id));
            Assert.That(withdrawal!.Changes.Select(c => $"{c.ChangeType} {c.StringValue}"), Is.EqualTo(new[] { "Remove CN=Bob,DC=test" }));
            Assert.That(result.PendingExports.Single().AttributeValueChanges.Select(c => $"{c.ChangeType} {c.StringValue}"),
                Is.EquivalentTo(new[] { "Add CN=Carol,DC=test", "Remove CN=Dave,DC=test" }), "arrange check: the replacement export");
        }
    }

    /// <summary>
    /// Nothing queued: an evaluation that finds a member already current withdraws nothing and reports nothing.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_AlreadyCurrentWithNothingQueued_ReportsNoWithdrawalAsync()
    {
        var (_, mvo, _, _, cache, changedAttributes) = ArrangeMembershipScenario(
            targetMembers: ["CN=Bob,DC=test"], mvoMembers: ["CN=Bob,DC=test"]);

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(mvo, changedAttributes, cache);

        Assert.That(result.Withdrawals, Is.Empty);
    }

    /// <summary>
    /// A stale Delete Pending Export (staged when the Metaverse Object left the export rule's scope)
    /// must be superseded when the object is evaluated back in scope WITH a relevant attribute delta:
    /// the merge path replaces the Delete with an Update and the stale Delete is removed (#1018 pin).
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_ExistingDeletePendingExportWithRelevantDelta_SupersededByUpdateAsync()
    {
        var (_, mvo, targetCso, targetEmployeeIdAttr, cache) = ArrangeSingleValuedScenario();

        var staleDeletePendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetCso.ConnectedSystem,
            ConnectedSystemId = targetCso.ConnectedSystemId,
            ConnectedSystemObject = targetCso,
            ConnectedSystemObjectId = targetCso.Id,
            ChangeType = PendingExportChangeType.Delete,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        var scopeExitRun = Guid.NewGuid();
        staleDeletePendingExport.RecordStager(scopeExitRun);
        SyncRepo.SeedPendingExport(staleDeletePendingExport);

        var employeeIdMvAttr = mvo.Type!.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var newEmployeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        newEmployeeIdValue.StringValue = "E321";
        var changedAttributes = new List<MetaverseObjectAttributeValue> { newEmployeeIdValue };

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var replacement = result.PendingExports.Single();
        Assert.That(replacement.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "An in-scope evaluation with a relevant attribute delta must supersede the stale Delete with an Update.");
        Assert.That(replacement.AttributeValueChanges.Single().StringValue, Is.EqualTo("E321"),
            "The replacement Update must carry the fresh export evaluation change.");
        Assert.That(replacement.AttributeValueChanges.Single().AttributeId, Is.EqualTo(targetEmployeeIdAttr.Id));
        Assert.That(SyncRepo.PendingExports.ContainsKey(staleDeletePendingExport.Id), Is.False,
            "The stale Delete Pending Export must be removed by the merge (Delete gone, Update staged).");
        Assert.That(replacement.Stagers(), Has.No.Member(scopeExitRun),
            "A Delete superseded by an Update is withdrawn, not continued: the run that queued the Delete did not stage the Update (#2100).");
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
            mvo, changedAttributes, cache);

        Assert.That(result.PendingExports, Has.Count.EqualTo(1));
        var created = result.PendingExports.Single();
        Assert.That(created.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(created.AttributeValueChanges, Has.Count.EqualTo(1));
        Assert.That(created.AttributeValueChanges.Single().AttributeId, Is.EqualTo(targetEmployeeIdAttr.Id));
        Assert.That(created.AttributeValueChanges.Single().StringValue, Is.EqualTo("E777"));
    }

    #region Provisioning a brand-new CSO skips the fallback database lookup

    /// <summary>
    /// Spy repository counting calls to the fallback merge lookup
    /// (<c>GetPendingExportLightweightByConnectedSystemObjectIdAsync</c>) that the "Fallback: check if a
    /// Pending Export exists in the database" branch of
    /// <c>CreateOrUpdatePendingExportWithNoNetChangeAsync</c> uses.
    /// </summary>
    private sealed class FallbackLookupCountingSyncRepository : SyncRepository
    {
        public int FallbackLookupCallCount;

        public override Task<PendingExport?> GetPendingExportLightweightByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
        {
            Interlocked.Increment(ref FallbackLookupCallCount);
            return base.GetPendingExportLightweightByConnectedSystemObjectIdAsync(connectedSystemObjectId);
        }
    }

    /// <summary>
    /// A brand-new provisioning CSO is created moments earlier, in memory, by
    /// <c>CreatePendingProvisioningCsoAsync</c>, with a freshly minted <c>Guid.NewGuid()</c>: no
    /// persisted Pending Export can possibly reference it yet. Profiling recorded 100,098 such no-op
    /// fallback lookups on one run; the fallback database lookup must be skipped entirely whenever the
    /// CSO being exported to was just created by this same call (<c>createdNewCso</c>).
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_ProvisioningBrandNewCso_SkipsTheDatabaseFallbackLookupAsync()
    {
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var employeeIdMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);
        var targetEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());

        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportSyncRule.ConnectedSystemObjectType = targetUserType;
        exportSyncRule.MetaverseObjectTypeId = mvUserType.Id;
        exportSyncRule.ProvisionToConnectedSystem = true;
        exportSyncRule.ObjectScopingCriteriaGroups.Clear();
        exportSyncRule.ObjectMatchingRules = new List<ObjectMatchingRule>();
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

        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>> { { mvUserType.Id, new List<SyncRule> { exportSyncRule } } };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>(); // no existing CSO: forces ProvisionNewCso
        var csoAttributeValues = Enumerable.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        var cache = new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup, csoAttributeValues, new List<int> { targetSystem.Id });

        var spyRepo = new FallbackLookupCountingSyncRepository();
        var localSyncRepo = TestUtilities.CreateSyncRepository(repository: spyRepo);
        using var localJim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: localSyncRepo);

        var employeeIdValue = mvo.AttributeValues.Single(av => av.AttributeId == employeeIdMvAttr.Id);
        var changedAttributes = new List<MetaverseObjectAttributeValue> { employeeIdValue };

        var result = await localJim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, cache);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PendingExports, Has.Count.EqualTo(1));
            Assert.That(result.PendingExports.Single().ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(spyRepo.FallbackLookupCallCount, Is.EqualTo(0),
                "A brand-new provisioning CSO cannot have a persisted Pending Export; the fallback database lookup must be skipped entirely.");
        }
    }

    #endregion
}
