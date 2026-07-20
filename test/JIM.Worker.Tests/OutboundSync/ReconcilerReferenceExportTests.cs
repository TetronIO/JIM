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
/// Proves export evaluation handles reconciler-shaped Metaverse Objects (#892).
/// The Temporal Scope Reconciler apply step loads flagged Metaverse Objects via
/// GetMetaverseObjectsByIdsNoTrackingAsync, which includes AttributeValues.Attribute but
/// deliberately not AttributeValues.ReferenceValue (including it would join a full Metaverse
/// Object row per reference; a large group would drag tens of thousands of rows). Reference
/// attribute values on that path therefore arrive with a null ReferenceValue navigation and
/// only the ReferenceValueId FK scalar populated. These tests pin that export evaluation
/// produces the same output for that shape as for the tracked, navigation-populated shape
/// the per-page sync flow supplies; a reconciler-driven provision must not silently drop
/// reference attributes, because nothing re-flags an already-provisioned object to heal them.
/// </summary>
[TestFixture]
public class ReconcilerReferenceExportTests
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
    /// Reconciler shape, single-valued reference, direct Attribute Flow: a provision (Create) with
    /// empty changedAttributes must source the Manager reference from the MVO's current values and
    /// emit the referenced Metaverse Object ID as an unresolved reference, via the FK scalar.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_ReconcilerShapedSingleValuedReference_ExportsViaFkScalar()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var managerMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Manager);
        var targetManagerAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Manager);
        var exportRule = CreateExportRuleWithDirectMapping(managerMvAttr, targetManagerAttr);

        var managerMvoId = Guid.NewGuid();
        var mvo = CreateReconcilerShapedMvo(mvUserType, new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = managerMvAttr.Id,
            Attribute = managerMvAttr,
            ReferenceValueId = managerMvoId,
            ReferenceValue = null
        });

        // Act: empty changedAttributes and no existing CSO is exactly the reconciler apply-step shape.
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "A reconciler-driven provision must export the single-valued reference attribute");
        Assert.That(changes[0].UnresolvedReferenceValue, Is.EqualTo(managerMvoId.ToString()),
            "The referenced Metaverse Object ID must come from the ReferenceValueId FK scalar when the navigation is null");
    }

    /// <summary>
    /// Reconciler shape, multi-valued reference, direct Attribute Flow: a provision (Create) with
    /// empty changedAttributes must source every membership reference from the MVO's current values
    /// via the FK scalar, one unresolved reference per value.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_ReconcilerShapedMultiValuedReference_ExportsAllValuesViaFkScalar()
    {
        // Arrange
        var mvGroupType = MetaverseObjectTypesData.Single(t => t.Name == "Group");
        var memberMvAttr = mvGroupType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Member);
        var targetManagerAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Manager);
        // A multi-valued member source must map to a multi-valued target; otherwise the MVA->SVA guard (#435)
        // rightly refuses to export more than one value. This test exercises FK-scalar reference export.
        targetManagerAttr.AttributePlurality = AttributePlurality.MultiValued;
        var exportRule = CreateExportRuleWithDirectMapping(memberMvAttr, targetManagerAttr);

        var memberId1 = Guid.NewGuid();
        var memberId2 = Guid.NewGuid();
        var mvo = CreateReconcilerShapedMvo(mvGroupType,
            new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = memberMvAttr.Id,
                Attribute = memberMvAttr,
                ReferenceValueId = memberId1,
                ReferenceValue = null
            },
            new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = memberMvAttr.Id,
                Attribute = memberMvAttr,
                ReferenceValueId = memberId2,
                ReferenceValue = null
            });

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(2),
            "A reconciler-driven provision must export every multi-valued reference value");
        Assert.That(changes.Select(c => c.UnresolvedReferenceValue),
            Is.EquivalentTo(new[] { memberId1.ToString(), memberId2.ToString() }),
            "Each referenced Metaverse Object ID must come from the ReferenceValueId FK scalar when the navigation is null");
    }

    /// <summary>
    /// Reconciler shape, expression flow sourcing a reference attribute: the expression context must
    /// see the referenced Metaverse Object ID via the FK scalar when the ReferenceValue navigation is
    /// null, so mv["Manager"] evaluates identically on the reconciler path and the per-page path.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_ReconcilerShapedExpressionOverReference_EvaluatesViaFkScalar()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var managerMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Manager);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);
        var exportRule = CreateExportRuleWithExpressionMapping("mv[\"Manager\"]", targetDisplayNameAttr);

        var managerMvoId = Guid.NewGuid();
        var mvo = CreateReconcilerShapedMvo(mvUserType, new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = managerMvAttr.Id,
            Attribute = managerMvAttr,
            ReferenceValueId = managerMvoId,
            ReferenceValue = null
        });

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "An expression sourcing a reference attribute must still produce a value on the reconciler path");
        Assert.That(changes[0].StringValue, Is.EqualTo(managerMvoId.ToString()),
            "The expression context must fall back to the ReferenceValueId FK scalar when the navigation is null");
    }

    /// <summary>
    /// Tracked shape, expression flow sourcing a reference attribute: pins the per-page behaviour
    /// (ReferenceValue navigation populated) so the FK-scalar fallback cannot regress it.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_TrackedExpressionOverReference_EvaluatesViaNavigation()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var managerMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Manager);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);
        var exportRule = CreateExportRuleWithExpressionMapping("mv[\"Manager\"]", targetDisplayNameAttr);

        var managerMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvUserType };
        var mvo = CreateReconcilerShapedMvo(mvUserType, new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = managerMvAttr.Id,
            Attribute = managerMvAttr,
            ReferenceValue = managerMvo,
            ReferenceValueId = managerMvo.Id
        });

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1));
        Assert.That(changes[0].StringValue, Is.EqualTo(managerMvo.Id.ToString()));
    }

    #region helpers
    private ConnectedSystemObjectTypeAttribute GetTargetAttribute(MockTargetSystemAttributeNames name)
    {
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        return targetUserType.Attributes.Single(a => a.Name == name.ToString());
    }

    private static MetaverseObject CreateReconcilerShapedMvo(MetaverseObjectType type, params MetaverseObjectAttributeValue[] attributeValues)
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
        var rule = new SyncRule { Id = 1, Name = "Reconciler Reference Export Rule", Direction = SyncRuleDirection.Export };
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

    private static SyncRule CreateExportRuleWithExpressionMapping(string expression, ConnectedSystemObjectTypeAttribute targetAttr)
    {
        var rule = new SyncRule { Id = 1, Name = "Reconciler Reference Export Rule", Direction = SyncRuleDirection.Export };
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
                    Expression = expression
                }
            }
        });
        return rule;
    }
    #endregion
}
