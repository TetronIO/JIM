// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves reference recall on Metaverse Object deletion (#908): when a Metaverse Object is deleted,
/// membership-removal Pending Exports must be staged for the Metaverse Objects that referenced it
/// (for example groups whose Static Members included a deprovisioned leaver), with the removal values
/// PRE-RESOLVED to the referenced object's per-system external ID. Export-time resolution walks
/// MVO -> joined CSO and can never succeed for a deleted Metaverse Object, so staging-time resolution
/// is the only correct option. Without reference recall, a target system without referential integrity
/// keeps the deleted object as a group member forever: the referencing groups' CSOs never change, so
/// the unchanged-skip means no sync ever re-evaluates them (found red by Scenario 8's LeaverCohort step).
/// </summary>
[TestFixture]
public class ReferenceRecallExportTests
{
    private const int TargetSystemId = 5;
    private const int MvGroupTypeId = 50;
    private const int MvMemberAttributeId = 60;
    private const int MvDisplayNameAttributeId = 61;
    private const int MvCategoryAttributeId = 62;
    private const int MvOwnerAttributeId = 63;
    private const int CsGroupTypeId = 70;
    private const int CsMemberAttributeId = 80;
    private const int CsDnAttributeId = 81;
    private const int CsCnAttributeId = 82;
    private const int CsOwnerAttributeId = 83;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private MetaverseObjectType MvGroupType { get; set; } = null!;
    private MetaverseAttribute MvMemberAttribute { get; set; } = null!;
    private MetaverseAttribute MvDisplayNameAttribute { get; set; } = null!;
    private MetaverseAttribute MvCategoryAttribute { get; set; } = null!;
    private MetaverseAttribute MvOwnerAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsMemberAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsDnAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsCnAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsOwnerAttribute { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        SyncRepo = new SyncRepository();
        var mockJimDbContext = new Mock<JimDbContext>();
        Jim = new JimApplication(new PostgresDataRepository(mockJimDbContext.Object), syncRepository: SyncRepo);

        MvMemberAttribute = new MetaverseAttribute
        {
            Id = MvMemberAttributeId,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        MvDisplayNameAttribute = new MetaverseAttribute
        {
            Id = MvDisplayNameAttributeId,
            Name = "Display Name",
            Type = AttributeDataType.Text
        };
        MvCategoryAttribute = new MetaverseAttribute
        {
            Id = MvCategoryAttributeId,
            Name = "Group Category",
            Type = AttributeDataType.Text
        };
        MvOwnerAttribute = new MetaverseAttribute
        {
            Id = MvOwnerAttributeId,
            Name = "Owner",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued
        };
        MvGroupType = new MetaverseObjectType
        {
            Id = MvGroupTypeId,
            Name = "Group",
            Attributes = [MvMemberAttribute, MvDisplayNameAttribute, MvCategoryAttribute, MvOwnerAttribute]
        };

        CsMemberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsMemberAttributeId,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
        CsDnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsDnAttributeId,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            IsSecondaryExternalId = true,
            Selected = true
        };
        CsCnAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsCnAttributeId,
            Name = "cn",
            Type = AttributeDataType.Text,
            Selected = true
        };
        CsOwnerAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsOwnerAttributeId,
            Name = "owner",
            Type = AttributeDataType.Reference,
            Selected = true
        };

        var exportRule = new SyncRule
        {
            Id = 900,
            Name = "Target Export Groups",
            Enabled = true,
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = TargetSystemId,
            MetaverseObjectTypeId = MvGroupTypeId,
            AttributeFlowRules =
            {
                new SyncRuleMapping
                {
                    Id = 901,
                    TargetConnectedSystemAttribute = CsMemberAttribute,
                    TargetConnectedSystemAttributeId = CsMemberAttribute.Id,
                    Sources =
                    {
                        new SyncRuleMappingSource
                        {
                            Id = 902,
                            Order = 0,
                            MetaverseAttribute = MvMemberAttribute,
                            MetaverseAttributeId = MvMemberAttribute.Id
                        }
                    }
                }
            }
        };
        SyncRepo.SeedSyncRule(exportRule);
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// The core recall path: a group references a leaver who is provisioned in the target system.
    /// Capturing before deletion and staging after it must produce one Update Pending Export for the
    /// group's target CSO carrying a Remove change whose value is the leaver's target DN (pre-resolved,
    /// no unresolved reference left for export execution to fail on).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_GroupReferencingDeletedMember_StagesPreResolvedRemovalAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var survivorMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = MvGroupType };
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id, survivorMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences:
            [(memberMvo.Id, memberDn), (survivorMvo.Id, "uid=avery.active,ou=People,dc=glitterband,dc=local")]);

        // Act: capture (pre-deletion), delete, stage (post-deletion) — the same order the sync flush uses.
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.ReferencingObjectsEvaluated, Is.EqualTo(1));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1),
            "The referencing group's target CSO must receive a membership-removal Pending Export");
        Assert.That(result.RemovalChangesStaged, Is.EqualTo(1));

        var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(pendingExport.HasUnresolvedReferences, Is.False,
            "The removal must be pre-resolved; a deleted Metaverse Object can never resolve at export time");

        var change = pendingExport.AttributeValueChanges.Single();
        Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
        Assert.That(change.AttributeId, Is.EqualTo(CsMemberAttributeId));
        Assert.That(change.StringValue, Is.EqualTo(memberDn),
            "The Remove change must carry the leaver's resolved target value (the DN), captured before deletion");
        Assert.That(change.UnresolvedReferenceValue, Is.Null);
    }

    /// <summary>
    /// A leaver who was never provisioned to the target has nothing there to remove: no Pending Export
    /// may be staged for the group (an unresolved removal would sit deferred forever).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_MemberNeverProvisionedToTarget_StagesNothingAsync()
    {
        // Arrange: member MVO with NO target CSO; the group's target CSO has no member value for them either.
        var memberMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = MvGroupType };
        SyncRepo.SeedMetaverseObject(memberMvo);
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        SeedGroupTargetCso(groupMvo, memberReferences: []);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0),
            "No removal export must be staged when the deleted object had no presence in the target system");
        Assert.That(SyncRepo.PendingExports, Is.Empty);
    }

    /// <summary>
    /// Burst shape: a group losing several members in the same batch must be evaluated ONCE, producing a
    /// single Update Pending Export carrying all the removals (not one Pending Export per deleted member,
    /// which would both collide on the unique PE-per-CSO constraint and multiply evaluation cost at scale).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_TwoDeletedMembersInSameGroup_OneExportWithBothRemovalsAsync()
    {
        // Arrange
        var (member1, member1Dn) = SeedMemberWithTargetCso("uid=leaver.one,ou=People,dc=glitterband,dc=local");
        var (member2, member2Dn) = SeedMemberWithTargetCso("uid=leaver.two,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(member1.Id, member2.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences:
            [(member1.Id, member1Dn), (member2.Id, member2Dn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([member1.Id, member2.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(member1);
        await SyncRepo.DeleteMetaverseObjectAsync(member2);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [member1.Id, member2.Id]);

        // Assert
        Assert.That(result.ReferencingObjectsEvaluated, Is.EqualTo(1), "The group must be evaluated once, not per deleted member");
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1));
        Assert.That(result.RemovalChangesStaged, Is.EqualTo(2));

        var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(2));
        Assert.That(pendingExport.AttributeValueChanges.Select(c => c.StringValue),
            Is.EquivalentTo(new[] { member1Dn, member2Dn }));
        Assert.That(pendingExport.AttributeValueChanges.Select(c => c.ChangeType),
            Is.All.EqualTo(PendingExportAttributeChangeType.Remove));
    }

    /// <summary>
    /// A referencing object that is itself being deleted must not have removals staged for it: its own
    /// deletion (and its CSOs' delete Pending Exports) supersede any membership updates.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ReferencingObjectAlsoDeleted_StagesNothingAsync()
    {
        // Arrange: the group itself is in the deletion batch alongside the member it references.
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=leaver.solo,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id, groupMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        await SyncRepo.DeleteMetaverseObjectAsync(groupMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id, groupMvo.Id]);

        // Assert
        Assert.That(context.Candidates, Is.Empty,
            "References held by objects that are themselves deletion candidates must be excluded at capture");
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0));
        Assert.That(SyncRepo.PendingExports, Is.Empty);
    }

    /// <summary>
    /// No inbound references means an empty context and a no-op stage: the common case (most deletions
    /// are not referenced by anything) must not pay any evaluation cost.
    /// </summary>
    [Test]
    public async Task CaptureReferenceRecallContext_NoInboundReferences_ReturnsEmptyContextAsync()
    {
        var lonesomeMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = MvGroupType };
        SyncRepo.SeedMetaverseObject(lonesomeMvo);

        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([lonesomeMvo.Id]);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [lonesomeMvo.Id]);

        Assert.That(context.Candidates, Is.Empty);
        Assert.That(result.ReferencingObjectsEvaluated, Is.EqualTo(0));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0));
    }

    #region Set-based fast path (#1003)

    /// <summary>
    /// A group with a pending Delete Pending Export (deprovisioning staged, not yet exported) that
    /// loses a member must keep its Delete Pending Export: the object is being deleted from the
    /// target, so a membership removal is moot, and replacing the Delete with an Update would leave
    /// the group alive in the target system forever.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ExistingDeletePendingExportForGroupCso_PreservesDeleteAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        var deletePendingExport = SeedPendingExport(groupTargetCso.Id, PendingExportChangeType.Delete);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.SkippedDueToExistingDeletePendingExport, Is.EqualTo(1));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0));
        var survivingPendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(survivingPendingExport.Id, Is.EqualTo(deletePendingExport.Id),
            "The Delete Pending Export must survive recall untouched (deprovisioning supersedes membership updates)");
        Assert.That(survivingPendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
    }

    /// <summary>
    /// The Delete-wins rule must hold on the fallback path too (a rule shape that routes the group
    /// type through full evaluation must not resurrect the pre-#1003 merge behaviour that replaced
    /// the Delete Pending Export with a membership-removal Update).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ExistingDeletePendingExportViaFallbackRule_PreservesDeleteAsync()
    {
        // Arrange: an expression mapping that mentions the member attribute routes the type to the fallback.
        AddExpressionMappingMentioningMembers();
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        var deletePendingExport = SeedPendingExport(groupTargetCso.Id, PendingExportChangeType.Delete);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FallbackReferencingObjects, Is.EqualTo(1));
        var survivingPendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(survivingPendingExport.Id, Is.EqualTo(deletePendingExport.Id));
        Assert.That(survivingPendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
    }

    /// <summary>
    /// A group provisioned this run (CSO PendingProvisioning, unexported Create Pending Export)
    /// that loses a member must keep its Create Pending Export: there is nothing in the target to
    /// remove a member from yet, and destroying the Create leaves the CSO stranded in
    /// PendingProvisioning with no export at all.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_GroupCsoPendingProvisioning_LeavesCreatePendingExportUntouchedAsync()
    {
        // Arrange: provisioning-enabled rule, PendingProvisioning group CSO with its Create PE.
        var exportRule = SyncRepo.SyncRules.Values.Single();
        exportRule.ProvisionToConnectedSystem = true;
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        groupTargetCso.Status = ConnectedSystemObjectStatus.PendingProvisioning;
        var createPendingExport = SeedPendingExport(groupTargetCso.Id, PendingExportChangeType.Create);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0),
            "Recall must not provision and must not touch a pending Create export");
        var survivingPendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(survivingPendingExport.Id, Is.EqualTo(createPendingExport.Id),
            "The Create Pending Export must survive recall untouched");
        Assert.That(survivingPendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
    }

    /// <summary>
    /// The PendingProvisioning guard must hold on the fallback path too.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_GroupCsoPendingProvisioningViaFallbackRule_LeavesCreateUntouchedAsync()
    {
        // Arrange
        AddExpressionMappingMentioningMembers();
        var exportRule = SyncRepo.SyncRules.Values.Single(r => r.Id == 900);
        exportRule.ProvisionToConnectedSystem = true;
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        groupTargetCso.Status = ConnectedSystemObjectStatus.PendingProvisioning;
        var createPendingExport = SeedPendingExport(groupTargetCso.Id, PendingExportChangeType.Create);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FallbackReferencingObjects, Is.EqualTo(1));
        var survivingPendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(survivingPendingExport.Id, Is.EqualTo(createPendingExport.Id));
        Assert.That(survivingPendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
    }

    /// <summary>
    /// A rule whose mapping sources the member attribute through an expression cannot be handled by
    /// the direct fast path; the whole type must route through the full-evaluation fallback and the
    /// removal must still be staged (via the rule's direct member mapping).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ExpressionSourcingReferenceAttribute_RoutesToFallbackAndStagesRemovalAsync()
    {
        // Arrange
        AddExpressionMappingMentioningMembers();
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FallbackReferencingObjects, Is.EqualTo(1));
        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(0));
        var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(pendingExport.AttributeValueChanges.Any(avc =>
                avc.ChangeType == PendingExportAttributeChangeType.Remove &&
                avc.AttributeId == CsMemberAttributeId &&
                avc.StringValue == memberDn),
            "The fallback must still stage the pre-resolved membership removal");
    }

    /// <summary>
    /// An expression mapping that does NOT reference the member attribute must not force the type
    /// off the fast path, and the fast path stages ONLY the reference removal: recall no longer
    /// piggybacks incidental drift corrections from re-evaluating unrelated flows (a deliberate
    /// #1003 semantic change; drift correction is drift detection's job).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_UnrelatedExpressionMapping_StaysOnFastPathWithoutPiggybackAsync()
    {
        // Arrange: expression flows Display Name to cn; the group's CSO has no cn value, so the old
        // behaviour would have piggybacked a cn Update onto the recall Pending Export.
        AddUnrelatedExpressionMapping();
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = MvDisplayNameAttribute,
            AttributeId = MvDisplayNameAttribute.Id,
            StringValue = "Team Alpha"
        });
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(1));
        Assert.That(result.FallbackReferencingObjects, Is.EqualTo(0));
        var pendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(1),
            "Only the reference removal may be staged; no piggybacked changes from unrelated flows");
        Assert.That(pendingExport.AttributeValueChanges.Single().StringValue, Is.EqualTo(memberDn));
    }

    /// <summary>
    /// A scoped export rule whose criteria the referencing group does not meet stages nothing:
    /// scope evaluation must survive on the fast path (lean criteria-only attribute load), not be
    /// skipped for speed.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ScopedRuleGroupOutOfScope_StagesNothingAsync()
    {
        // Arrange
        AddScopingCriterion(categoryValue: "distribution");
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        SetGroupCategory(groupMvo, "security");
        SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(1), "Scoped rules must not force the fallback path");
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0),
            "An out-of-scope group must not receive a recall Pending Export");
        Assert.That(SyncRepo.PendingExports, Is.Empty);
    }

    /// <summary>
    /// The in-scope sibling of the test above: the same criteria met must stage the removal via the
    /// fast path.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ScopedRuleGroupInScope_StagesRemovalAsync()
    {
        // Arrange
        AddScopingCriterion(categoryValue: "distribution");
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        SetGroupCategory(groupMvo, "distribution");
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(1));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1));
        var change = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id)
            .AttributeValueChanges.Single();
        Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
        Assert.That(change.StringValue, Is.EqualTo(memberDn));
    }

    /// <summary>
    /// Merging into an existing (unexported) Update Pending Export: recall changes join the
    /// existing ones, an unrelated pending change survives, a stale Add whose unresolved reference
    /// is the deleted Metaverse Object is purged (it could never resolve, and would wedge the whole
    /// Pending Export in deferred-resolution limbo), and HasUnresolvedReferences is recomputed.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ExistingUpdatePendingExportForGroupCso_MergesAndPurgesAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        var existingPendingExport = SeedPendingExport(groupTargetCso.Id, PendingExportChangeType.Update);
        existingPendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = CsDnAttribute,
            AttributeId = CsDnAttribute.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "cn=Team Alpha Renamed,ou=Groups,dc=glitterband,dc=local"
        });
        existingPendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = CsMemberAttribute,
            AttributeId = CsMemberAttribute.Id,
            ChangeType = PendingExportAttributeChangeType.Add,
            UnresolvedReferenceValue = memberMvo.Id.ToString()
        });
        existingPendingExport.HasUnresolvedReferences = true;

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(1));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1));
        var mergedPendingExport = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id);
        Assert.That(mergedPendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
        Assert.That(mergedPendingExport.AttributeValueChanges, Has.Count.EqualTo(2),
            "The removal and the unrelated rename must survive; the unresolvable Add must be purged");
        Assert.That(mergedPendingExport.AttributeValueChanges.Any(avc =>
            avc.ChangeType == PendingExportAttributeChangeType.Remove && avc.StringValue == memberDn));
        Assert.That(mergedPendingExport.AttributeValueChanges.Any(avc =>
            avc.AttributeId == CsDnAttributeId && avc.ChangeType == PendingExportAttributeChangeType.Update));
        Assert.That(mergedPendingExport.HasUnresolvedReferences, Is.False,
            "HasUnresolvedReferences must be recomputed after the purge");
    }

    /// <summary>
    /// A single-valued reference (for example an owner) still pointing at the deleted object must
    /// be cleared with an all-null Update change, the same null-clearing shape normal export
    /// evaluation produces.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_SingleValuedReferenceStillHeld_StagesNullClearingUpdateAsync()
    {
        // Arrange
        AddOwnerMapping();
        var (ownerMvo, ownerDn) = SeedMemberWithTargetCso("uid=oscar.owner,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoOwning(ownerMvo.Id);
        var groupTargetCso = SeedGroupTargetCsoWithOwner(groupMvo, ownerMvo.Id, ownerDn);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([ownerMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(ownerMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [ownerMvo.Id]);

        // Assert
        Assert.That(result.FastPathReferencingObjects, Is.EqualTo(1));
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1));
        var change = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id)
            .AttributeValueChanges.Single();
        Assert.That(change.AttributeId, Is.EqualTo(CsOwnerAttributeId));
        Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
        Assert.That(change.StringValue, Is.Null, "A single-valued reference removal is an all-null (clearing) Update");
        Assert.That(change.UnresolvedReferenceValue, Is.Null);
    }

    /// <summary>
    /// A single-valued reference the target has already re-pointed at someone else must NOT be
    /// cleared: the deleted object is not the value being held, so recall has nothing to remove.
    /// (The pre-#1003 behaviour cleared the attribute whenever it had any value; this pins the
    /// narrower, correct predicate.)
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_SingleValuedReferenceRepointed_StagesNothingAsync()
    {
        // Arrange: the group MVO still references the deleted owner, but the target CSO already
        // holds a different owner value (drifted or re-pointed out of band).
        AddOwnerMapping();
        var (ownerMvo, _) = SeedMemberWithTargetCso("uid=oscar.owner,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoOwning(ownerMvo.Id);
        var groupTargetCso = SeedGroupTargetCsoWithOwner(groupMvo, ownerMvoId: null,
            ownerDn: "uid=nadia.new,ou=People,dc=glitterband,dc=local");

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([ownerMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(ownerMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [ownerMvo.Id]);

        // Assert
        Assert.That(result.PendingExportsStaged, Is.EqualTo(0),
            "The target no longer holds the deleted reference; clearing it would destroy the re-pointed value");
        Assert.That(SyncRepo.PendingExports.Values.Any(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id), Is.False);
    }

    /// <summary>
    /// A target member row that carries only the resolved reference (ReferenceValueId set, no raw
    /// string) must still be matched and removed. The pre-#1003 string-only comparison missed these
    /// rows entirely.
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_MemberRowKeyedByReferenceValueIdOnly_StagesRemovalAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        var memberRow = groupTargetCso.AttributeValues.Single(av => av.AttributeId == CsMemberAttributeId);
        memberRow.UnresolvedReferenceValue = null;

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1),
            "A resolved-only member row references the deleted object and must be removed");
        var change = SyncRepo.PendingExports.Values
            .Single(pe => pe.ConnectedSystemObjectId == groupTargetCso.Id)
            .AttributeValueChanges.Single();
        Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
        Assert.That(change.StringValue, Is.EqualTo(memberDn),
            "The removal value comes from the pre-deletion capture, not the (absent) raw row string");
    }

    /// <summary>
    /// A caller-provided run-scoped cache must be honoured: staging must not reload Synchronisation
    /// Rules per flush (the pre-#1003 behaviour rebuilt the rule cache on every deletion flush).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_ProvidedCache_DoesNotReloadSyncRulesAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);
        var recallCache = await Jim.ExportEvaluation.BuildExportEvaluationCacheAsync(sourceConnectedSystemId: 0);
        var ruleLoadsAfterCacheBuild = SyncRepo.GetAllSyncRulesCallCount;

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id], recallCache);

        // Assert
        Assert.That(result.PendingExportsStaged, Is.EqualTo(1));
        Assert.That(SyncRepo.GetAllSyncRulesCallCount, Is.EqualTo(ruleLoadsAfterCacheBuild),
            "Staging with a provided cache must not reload Synchronisation Rules");
    }

    /// <summary>
    /// The staged Pending Exports and referencing-object display names must be exposed on the
    /// result so the sync flush can fold them into Activity reporting (RPEIs and outcomes).
    /// </summary>
    [Test]
    public async Task StageReferenceRecallExports_StagedPendingExports_ExposedOnResultAsync()
    {
        // Arrange
        var (memberMvo, memberDn) = SeedMemberWithTargetCso("uid=lena.leaver,ou=People,dc=glitterband,dc=local");
        var groupMvo = SeedGroupMvoReferencing(memberMvo.Id);
        groupMvo.CachedDisplayName = "Team Alpha";
        var groupTargetCso = SeedGroupTargetCso(groupMvo, memberReferences: [(memberMvo.Id, memberDn)]);

        // Act
        var context = await Jim.ExportEvaluation.CaptureReferenceRecallContextAsync([memberMvo.Id]);
        await SyncRepo.DeleteMetaverseObjectAsync(memberMvo);
        var result = await Jim.ExportEvaluation.StageReferenceRecallExportsAsync(context, [memberMvo.Id]);

        // Assert
        Assert.That(result.StagedPendingExports, Has.Count.EqualTo(1));
        Assert.That(result.StagedPendingExports.Single().ConnectedSystemObjectId, Is.EqualTo(groupTargetCso.Id));
        Assert.That(result.ReferencingObjectDisplayNames, Contains.Key(groupMvo.Id));
        Assert.That(result.ReferencingObjectDisplayNames[groupMvo.Id], Is.EqualTo("Team Alpha"));
    }

    #endregion

    #region helpers
    /// <summary>
    /// Seeds a member Metaverse Object joined to a provisioned target CSO whose secondary external ID
    /// (DN) attribute carries the given value, mirroring an LDAP-provisioned user.
    /// </summary>
    private (MetaverseObject Mvo, string Dn) SeedMemberWithTargetCso(string dn)
    {
        var memberMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = MvGroupType };
        SyncRepo.SeedMetaverseObject(memberMvo);

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsGroupTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = memberMvo.Id
        };
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = targetCso,
            Attribute = CsDnAttribute,
            AttributeId = CsDnAttribute.Id,
            StringValue = dn
        });
        SyncRepo.SeedConnectedSystemObject(targetCso);

        return (memberMvo, dn);
    }

    /// <summary>
    /// Seeds a group Metaverse Object whose Static Members reference the given Metaverse Object IDs.
    /// Reference rows carry only the FK scalar (no navigation), matching the reconciler-loaded shape.
    /// </summary>
    private MetaverseObject SeedGroupMvoReferencing(params Guid[] referencedMvoIds)
    {
        var groupMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = MvGroupType };
        foreach (var referencedMvoId in referencedMvoIds)
        {
            groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = groupMvo,
                Attribute = MvMemberAttribute,
                AttributeId = MvMemberAttribute.Id,
                ReferenceValueId = referencedMvoId
            });
        }
        SyncRepo.SeedMetaverseObject(groupMvo);
        return groupMvo;
    }

    /// <summary>
    /// Seeds the group's provisioned target CSO with member attribute values referencing the given
    /// member target CSOs. Member values carry the raw DN in UnresolvedReferenceValue, matching what
    /// an LDAP import stores.
    /// </summary>
    private ConnectedSystemObject SeedGroupTargetCso(
        MetaverseObject groupMvo,
        (Guid MemberMvoId, string Dn)[] memberReferences)
    {
        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsGroupTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = groupMvo.Id
        };

        foreach (var (memberMvoId, dn) in memberReferences)
        {
            // Resolve the member's target CSO by its joined MVO id (as an import's reference resolution would).
            var memberCso = SyncRepo.ConnectedSystemObjects.Values
                .FirstOrDefault(c => c.MetaverseObjectId == memberMvoId && c.ConnectedSystemId == TargetSystemId);
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = groupCso,
                Attribute = CsMemberAttribute,
                AttributeId = CsMemberAttribute.Id,
                ReferenceValueId = memberCso?.Id,
                UnresolvedReferenceValue = dn
            });
        }

        SyncRepo.SeedConnectedSystemObject(groupCso);
        return groupCso;
    }

    /// <summary>
    /// Seeds an unexported Pending Export of the given change type for a CSO, mirroring one staged
    /// by an earlier sync (deprovisioning, provisioning, or a prior update).
    /// </summary>
    private PendingExport SeedPendingExport(Guid connectedSystemObjectId, PendingExportChangeType changeType)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            ConnectedSystemObjectId = connectedSystemObjectId,
            ChangeType = changeType,
            Status = PendingExportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        SyncRepo.CreatePendingExportsAsync([pendingExport]).GetAwaiter().GetResult();
        return pendingExport;
    }

    /// <summary>
    /// Adds an expression mapping that references the member attribute by name, which must route
    /// the group type through the full-evaluation fallback (an expression can consume the
    /// reference attribute in ways the direct fast path cannot reproduce).
    /// </summary>
    private void AddExpressionMappingMentioningMembers()
    {
        var exportRule = SyncRepo.SyncRules.Values.Single(r => r.Id == 900);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 903,
            TargetConnectedSystemAttribute = CsCnAttribute,
            TargetConnectedSystemAttributeId = CsCnAttribute.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 904,
                    Order = 0,
                    Expression = "mv[\"Static Members\"] != null ? \"grp\" : \"empty\""
                }
            }
        });
    }

    /// <summary>
    /// Adds an expression mapping that does not touch the member attribute; the type must stay on
    /// the fast path.
    /// </summary>
    private void AddUnrelatedExpressionMapping()
    {
        var exportRule = SyncRepo.SyncRules.Values.Single(r => r.Id == 900);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 905,
            TargetConnectedSystemAttribute = CsCnAttribute,
            TargetConnectedSystemAttributeId = CsCnAttribute.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 906,
                    Order = 0,
                    Expression = "mv[\"Display Name\"]"
                }
            }
        });
    }

    /// <summary>
    /// Scopes the export rule to groups whose Group Category equals the given value.
    /// </summary>
    private void AddScopingCriterion(string categoryValue)
    {
        var exportRule = SyncRepo.SyncRules.Values.Single(r => r.Id == 900);
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Id = 910,
            Type = SearchGroupType.All,
            Criteria =
            {
                new SyncRuleScopingCriteria
                {
                    Id = 911,
                    MetaverseAttribute = MvCategoryAttribute,
                    MetaverseAttributeId = MvCategoryAttribute.Id,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = categoryValue
                }
            }
        });
    }

    /// <summary>
    /// Sets the group's Group Category attribute value (used by scoped-rule tests).
    /// </summary>
    private void SetGroupCategory(MetaverseObject groupMvo, string categoryValue)
    {
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = MvCategoryAttribute,
            AttributeId = MvCategoryAttribute.Id,
            StringValue = categoryValue
        });
    }

    /// <summary>
    /// Adds a single-valued Owner reference mapping to the export rule (Owner -> owner).
    /// </summary>
    private void AddOwnerMapping()
    {
        var exportRule = SyncRepo.SyncRules.Values.Single(r => r.Id == 900);
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 907,
            TargetConnectedSystemAttribute = CsOwnerAttribute,
            TargetConnectedSystemAttributeId = CsOwnerAttribute.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 908,
                    Order = 0,
                    MetaverseAttribute = MvOwnerAttribute,
                    MetaverseAttributeId = MvOwnerAttribute.Id
                }
            }
        });
    }

    /// <summary>
    /// Seeds a group Metaverse Object whose single-valued Owner attribute references the given
    /// Metaverse Object.
    /// </summary>
    private MetaverseObject SeedGroupMvoOwning(Guid ownerMvoId)
    {
        var groupMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = MvGroupType };
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = MvOwnerAttribute,
            AttributeId = MvOwnerAttribute.Id,
            ReferenceValueId = ownerMvoId
        });
        SyncRepo.SeedMetaverseObject(groupMvo);
        return groupMvo;
    }

    /// <summary>
    /// Seeds the group's provisioned target CSO with a single-valued owner attribute value. Pass a
    /// null <paramref name="ownerMvoId"/> for a value that does not resolve to any known CSO (a
    /// drifted or re-pointed owner).
    /// </summary>
    private ConnectedSystemObject SeedGroupTargetCsoWithOwner(MetaverseObject groupMvo, Guid? ownerMvoId, string ownerDn)
    {
        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            TypeId = CsGroupTypeId,
            Status = ConnectedSystemObjectStatus.Normal,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = groupMvo.Id
        };
        var ownerCso = ownerMvoId.HasValue
            ? SyncRepo.ConnectedSystemObjects.Values
                .FirstOrDefault(c => c.MetaverseObjectId == ownerMvoId && c.ConnectedSystemId == TargetSystemId)
            : null;
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso,
            Attribute = CsOwnerAttribute,
            AttributeId = CsOwnerAttribute.Id,
            ReferenceValueId = ownerCso?.Id,
            UnresolvedReferenceValue = ownerDn
        });
        SyncRepo.SeedConnectedSystemObject(groupCso);
        return groupCso;
    }
    #endregion
}
