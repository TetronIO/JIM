// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
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
    private const int CsGroupTypeId = 70;
    private const int CsMemberAttributeId = 80;
    private const int CsDnAttributeId = 81;

    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
    private MetaverseObjectType MvGroupType { get; set; } = null!;
    private MetaverseAttribute MvMemberAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsMemberAttribute { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute CsDnAttribute { get; set; } = null!;

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
        MvGroupType = new MetaverseObjectType
        {
            Id = MvGroupTypeId,
            Name = "Group",
            Attributes = [MvMemberAttribute]
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
    #endregion
}
