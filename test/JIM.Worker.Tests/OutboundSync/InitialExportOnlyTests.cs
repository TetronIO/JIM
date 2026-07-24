// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves the Initial Export Only mapping behaviour (#223): a mapping with InitialExportOnly enabled
/// flows during the initial provisioning (Create) export and is skipped for Update exports, leaving
/// the target attribute unmanaged on the Connected System Object once it is past provisioning.
/// </summary>
[TestFixture]
public class InitialExportOnlyTests
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
    /// A direct Initial Export Only mapping must contribute to the initial provisioning (Create) export
    /// exactly like any other mapping.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_InitialExportOnlyDirectMapping_OnCreate_IncludesChange()
    {
        // Arrange
        var (mvo, exportRule, _) = BuildDirectMappingScenario(initialExportOnly: true, displayName: "Initial Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "An Initial Export Only mapping must flow during the initial provisioning (Create) export");
        Assert.That(changes[0].StringValue, Is.EqualTo("Initial Value"));
    }

    /// <summary>
    /// A direct Initial Export Only mapping must be skipped for Update exports, even when the source
    /// Metaverse attribute changed: the attribute is unmanaged once the object is past provisioning.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_InitialExportOnlyDirectMapping_OnUpdate_SkipsChange()
    {
        // Arrange
        var (mvo, exportRule, changedAttributes) = BuildDirectMappingScenario(initialExportOnly: true, displayName: "Updated Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Is.Empty,
            "An Initial Export Only mapping must not flow on Update exports; the attribute is unmanaged after provisioning");
    }

    /// <summary>
    /// Regression guard: with Initial Export Only disabled (the default), Update exports must flow
    /// the mapping exactly as before.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_DefaultMapping_OnUpdate_IncludesChange()
    {
        // Arrange
        var (mvo, exportRule, changedAttributes) = BuildDirectMappingScenario(initialExportOnly: false, displayName: "Updated Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "A mapping without Initial Export Only must keep flowing on Update exports");
        Assert.That(changes[0].StringValue, Is.EqualTo("Updated Value"));
    }

    /// <summary>
    /// An expression-based Initial Export Only mapping must contribute to the Create export.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_InitialExportOnlyExpressionMapping_OnCreate_IncludesChange()
    {
        // Arrange
        var (mvo, exportRule) = BuildExpressionMappingScenario(initialExportOnly: true, displayName: "Initial Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "An expression-based Initial Export Only mapping must flow during the Create export");
        Assert.That(changes[0].StringValue, Is.EqualTo("Initial Value"));
    }

    /// <summary>
    /// An expression-based Initial Export Only mapping must be skipped for Update exports. Expression
    /// mappings normally always re-evaluate on Update, so this proves the gate applies before evaluation.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_InitialExportOnlyExpressionMapping_OnUpdate_SkipsChange()
    {
        // Arrange
        var (mvo, exportRule) = BuildExpressionMappingScenario(initialExportOnly: true, displayName: "Updated Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, mvo.AttributeValues.ToList(), PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Is.Empty,
            "An expression-based Initial Export Only mapping must not flow on Update exports");
    }

    /// <summary>
    /// A rule mixing Initial Export Only and normal mappings must skip only the Initial Export Only
    /// mapping on Update; sibling mappings keep flowing.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_MixedMappings_OnUpdate_SkipsOnlyInitialExportOnly()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var emailMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Email);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);
        var targetMailAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Mail);

        var exportRule = new SyncRule { Id = 1, Name = "Mixed Mapping Export Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(100, exportRule, displayNameMvAttr, targetDisplayNameAttr, initialExportOnly: true));
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(101, exportRule, emailMvAttr, targetMailAttr, initialExportOnly: false));

        var mvo = CreateMvo(mvUserType,
            CreateStringAttributeValue(displayNameMvAttr, "Updated Name"),
            CreateStringAttributeValue(emailMvAttr, "user@example.com"));
        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "Only the normally-managed mapping must flow on Update; the Initial Export Only mapping is skipped");
        Assert.That(changes[0].AttributeId, Is.EqualTo(targetMailAttr.Id));
        Assert.That(changes[0].StringValue, Is.EqualTo("user@example.com"));
    }

    /// <summary>
    /// A multi-valued Initial Export Only mapping (e.g. group membership) must be skipped for Update
    /// exports: no membership changes flow once the object is past provisioning.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_InitialExportOnlyMultiValuedMapping_OnUpdate_SkipsChanges()
    {
        // Arrange
        var mvGroupType = MetaverseObjectTypesData.Single(t => t.Name == "Group");
        var memberMvAttr = mvGroupType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Member);
        var targetManagerAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Manager);

        var exportRule = new SyncRule { Id = 1, Name = "Multi-Valued Export Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(100, exportRule, memberMvAttr, targetManagerAttr, initialExportOnly: true));

        var mvo = CreateMvo(mvGroupType,
            new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = memberMvAttr.Id,
                Attribute = memberMvAttr,
                ReferenceValueId = Guid.NewGuid()
            });
        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Is.Empty,
            "A multi-valued Initial Export Only mapping must not flow membership changes on Update exports");
    }

    #region helpers
    private ConnectedSystemObjectTypeAttribute GetTargetAttribute(MockTargetSystemAttributeNames name)
    {
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        return targetUserType.Attributes.Single(a => a.Name == name.ToString());
    }

    private (MetaverseObject Mvo, SyncRule ExportRule, List<MetaverseObjectAttributeValue> ChangedAttributes) BuildDirectMappingScenario(
        bool initialExportOnly, string displayName)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);

        var exportRule = new SyncRule { Id = 1, Name = "Initial Export Only Test Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(100, exportRule, displayNameMvAttr, targetDisplayNameAttr, initialExportOnly));

        var mvo = CreateMvo(mvUserType, CreateStringAttributeValue(displayNameMvAttr, displayName));
        return (mvo, exportRule, mvo.AttributeValues.ToList());
    }

    private (MetaverseObject Mvo, SyncRule ExportRule) BuildExpressionMappingScenario(bool initialExportOnly, string displayName)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);

        var exportRule = new SyncRule { Id = 1, Name = "Initial Export Only Expression Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            InitialExportOnly = initialExportOnly,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 200,
                    Order = 0,
                    Expression = "mv[\"Display Name\"]"
                }
            }
        });

        var mvo = CreateMvo(mvUserType, CreateStringAttributeValue(displayNameMvAttr, displayName));
        return (mvo, exportRule);
    }

    private static SyncRuleMapping CreateDirectMapping(
        int id, SyncRule rule, MetaverseAttribute sourceAttr, ConnectedSystemObjectTypeAttribute targetAttr, bool initialExportOnly)
    {
        return new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            TargetConnectedSystemAttribute = targetAttr,
            TargetConnectedSystemAttributeId = targetAttr.Id,
            InitialExportOnly = initialExportOnly,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = id + 100,
                    Order = 0,
                    MetaverseAttribute = sourceAttr,
                    MetaverseAttributeId = sourceAttr.Id
                }
            }
        };
    }

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

    private static MetaverseObjectAttributeValue CreateStringAttributeValue(MetaverseAttribute attribute, string value)
    {
        return new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            StringValue = value
        };
    }
    #endregion
}
