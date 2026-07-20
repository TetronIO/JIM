// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves export evaluation never emits a change for a valueless "ghost" reference attribute row
/// (#1019): a row whose ReferenceValue/ReferenceValueId and every payload column are null, as left
/// on surviving referencing objects by pre-fix Metaverse Object deletions. Emitting one staged an
/// all-null Add/Update on a later Create Pending Export. The deliberate all-null clearing change
/// for single-valued removals must be unaffected.
/// </summary>
[TestFixture]
public class GhostReferenceRowExportTests
{
    private JimApplication Jim { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        // CreateAttributeValueChanges never queries the database, so a bare mocked context suffices.
        var mockJimDbContext = new Mock<JimDbContext>();
        Jim = new JimApplication(new PostgresDataRepository(mockJimDbContext.Object), syncRepository: new SyncRepository());

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    /// <summary>
    /// A group provisioned to a new target system carries two real member rows and one ghost row.
    /// Only the real members may produce changes; the ghost must be skipped, not staged as an
    /// all-null Add.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_GhostMultiValuedReferenceRow_OnCreate_EmitsNoChangeForGhost()
    {
        // Arrange
        var mvGroupType = MetaverseObjectTypesData.Single(t => t.Name == "Group");
        var memberMvAttr = mvGroupType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Member);
        var targetReferenceAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Manager);
        // A multi-valued member source must map to a multi-valued target; otherwise the MVA->SVA guard (#435)
        // rightly refuses to export more than one value. Ghost-row skipping is the behaviour under test here.
        targetReferenceAttr.AttributePlurality = AttributePlurality.MultiValued;
        var exportRule = CreateExportRuleWithDirectMapping(memberMvAttr, targetReferenceAttr);

        var memberId1 = Guid.NewGuid();
        var memberId2 = Guid.NewGuid();
        var mvo = CreateMvo(mvGroupType,
            CreateMemberRow(memberMvAttr, memberId1),
            CreateMemberRow(memberMvAttr, memberId2),
            CreateGhostRow(memberMvAttr));

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(2),
            "The ghost member row must not produce a change; only the two real members may");
        Assert.That(changes.Select(c => c.UnresolvedReferenceValue),
            Is.EquivalentTo(new[] { memberId1.ToString(), memberId2.ToString() }));
    }

    /// <summary>
    /// A user provisioned to a new target system whose only Manager row is a ghost must produce no
    /// Manager change at all (absence of a reference, not an all-null Update).
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_GhostSingleValuedReferenceRow_OnCreate_EmitsNoChange()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var managerMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Manager);
        var targetManagerAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Manager);
        var exportRule = CreateExportRuleWithDirectMapping(managerMvAttr, targetManagerAttr);

        var mvo = CreateMvo(mvUserType, CreateGhostRow(managerMvAttr));

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Is.Empty,
            "A ghost Manager row carries nothing exportable and must not stage an all-null change");
    }

    /// <summary>
    /// Pin: the deliberate all-null clearing change for a single-valued reference removal (the
    /// removed row still carries its old ReferenceValueId) must survive the ghost guard.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_SingleValuedReferenceRemoval_StillEmitsNullClearingChange()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var managerMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Manager);
        var targetManagerAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Manager);
        var exportRule = CreateExportRuleWithDirectMapping(managerMvAttr, targetManagerAttr);

        var mvo = CreateMvo(mvUserType); // post-removal state: no Manager row remains
        var removedManagerRow = CreateMemberRow(managerMvAttr, Guid.NewGuid());
        removedManagerRow.MetaverseObject = mvo;

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [removedManagerRow], PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _,
            removedAttributes: [removedManagerRow]);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "A single-valued reference removal must still stage its null-clearing change");
        Assert.That(changes[0].ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
        Assert.That(changes[0].UnresolvedReferenceValue, Is.Null,
            "The clearing change must carry no value, telling the target system to clear the attribute");
        Assert.That(changes[0].StringValue, Is.Null);
    }

    #region helpers
    private ConnectedSystemObjectTypeAttribute GetTargetAttribute(MockTargetSystemAttributeNames name)
    {
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        return targetUserType.Attributes.Single(a => a.Name == name.ToString());
    }

    private static MetaverseObjectAttributeValue CreateMemberRow(MetaverseAttribute attribute, Guid referencedMvoId) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attribute.Id,
        Attribute = attribute,
        ReferenceValueId = referencedMvoId,
        ReferenceValue = null
    };

    private static MetaverseObjectAttributeValue CreateGhostRow(MetaverseAttribute attribute) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attribute.Id,
        Attribute = attribute,
        ReferenceValueId = null,
        ReferenceValue = null
    };

    private static MetaverseObject CreateMvo(MetaverseObjectType type, params MetaverseObjectAttributeValue[] attributeValues)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = type
        };
        foreach (var attributeValue in attributeValues)
        {
            attributeValue.MetaverseObject = mvo;
            mvo.AttributeValues.Add(attributeValue);
        }
        return mvo;
    }

    private static SyncRule CreateExportRuleWithDirectMapping(MetaverseAttribute sourceAttr, ConnectedSystemObjectTypeAttribute targetAttr)
    {
        var rule = new SyncRule { Id = 1, Name = "Ghost Reference Row Export Rule", Direction = SyncRuleDirection.Export };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = rule,
            TargetConnectedSystemAttribute = targetAttr,
            TargetConnectedSystemAttributeId = targetAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 200,
                    Order = 0,
                    MetaverseAttribute = sourceAttr,
                    MetaverseAttributeId = sourceAttr.Id
                }
            }
        });
        return rule;
    }
    #endregion
}
