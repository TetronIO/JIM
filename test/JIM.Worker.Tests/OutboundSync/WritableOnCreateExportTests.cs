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
/// Proves the <see cref="AttributeWritability.WritableOnCreate"/> contract on the export path: a
/// Connected System attribute declared writable only on creation flows into a Create Pending Export
/// and is excluded from every Update Pending Export, even when the source value has changed.
/// Rewriting such an attribute (a relational primary key, a directory's relative distinguished name)
/// would sever the link between the Connected System Object and the object it represents, so this
/// exclusion is a synchronisation integrity guard rather than an optimisation.
/// </summary>
[TestFixture]
public class WritableOnCreateExportTests
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
    /// A mapping targeting a Writable On Create attribute must contribute to the initial provisioning
    /// (Create) export: the value has to be supplied when the object is created, or it never can be.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_WritableOnCreateTarget_OnCreate_IncludesChange()
    {
        // Arrange
        var (mvo, exportRule, _) = BuildDirectMappingScenario(AttributeWritability.WritableOnCreate, "Initial Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "A Writable On Create attribute must flow during the initial provisioning (Create) export");
        Assert.That(changes[0].StringValue, Is.EqualTo("Initial Value"));
    }

    /// <summary>
    /// The regression guard that matters most: a Writable On Create attribute whose source value has
    /// changed must still be excluded from an Update export. Emitting the change would rewrite the
    /// Connected System's identifier for the object.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_WritableOnCreateTarget_OnUpdateWithChangedValue_ExcludesChange()
    {
        // Arrange
        var (mvo, exportRule, changedAttributes) = BuildDirectMappingScenario(AttributeWritability.WritableOnCreate, "Changed Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Is.Empty,
            "A Writable On Create attribute must never reach an Update Pending Export, even when its value changed");
    }

    /// <summary>
    /// An expression-based mapping targeting a Writable On Create attribute must be excluded from Update
    /// exports too. Expression mappings normally re-evaluate on every Update, so this proves the exclusion
    /// is applied before evaluation rather than downstream of it.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_WritableOnCreateExpressionTarget_OnUpdate_ExcludesChange()
    {
        // Arrange
        var (mvo, exportRule) = BuildExpressionMappingScenario(AttributeWritability.WritableOnCreate, "Changed Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, mvo.AttributeValues.ToList(), PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Is.Empty,
            "An expression-based mapping targeting a Writable On Create attribute must not flow on Update exports");
    }

    /// <summary>
    /// Regression guard: an ordinary Writable target must keep flowing on Update exports.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_WritableTarget_OnUpdate_IncludesChange()
    {
        // Arrange
        var (mvo, exportRule, changedAttributes) = BuildDirectMappingScenario(AttributeWritability.Writable, "Changed Value");

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "A Writable attribute must keep flowing on Update exports");
        Assert.That(changes[0].StringValue, Is.EqualTo("Changed Value"));
    }

    /// <summary>
    /// A rule mixing a Writable On Create mapping with an ordinary one must exclude only the former on
    /// Update; sibling mappings keep flowing.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_MixedTargets_OnUpdate_ExcludesOnlyWritableOnCreate()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var emailMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.Email);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);
        var targetMailAttr = GetTargetAttribute(MockTargetSystemAttributeNames.Mail);
        targetDisplayNameAttr.Writability = AttributeWritability.WritableOnCreate;

        var exportRule = new SyncRule { Id = 1, Name = "Mixed Writability Export Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(100, exportRule, displayNameMvAttr, targetDisplayNameAttr));
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(101, exportRule, emailMvAttr, targetMailAttr));

        var mvo = CreateMvo(mvUserType,
            CreateStringAttributeValue(displayNameMvAttr, "Changed Name"),
            CreateStringAttributeValue(emailMvAttr, "user@example.com"));
        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "Only the ordinary mapping must flow on Update; the Writable On Create mapping is excluded");
        Assert.That(changes[0].AttributeId, Is.EqualTo(targetMailAttr.Id));
        Assert.That(changes[0].StringValue, Is.EqualTo("user@example.com"));
    }

    /// <summary>
    /// The exclusion must survive both gates together: a Writable On Create target on a mapping that is
    /// also Initial Export Only still flows on Create.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_WritableOnCreateAndInitialExportOnly_OnCreate_IncludesChange()
    {
        // Arrange
        var (mvo, exportRule, _) = BuildDirectMappingScenario(
            AttributeWritability.WritableOnCreate, "Initial Value", initialExportOnly: true);

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(1),
            "Both gates only exclude on Update; a Create export still carries the value");
    }

    /// <summary>
    /// <see cref="SyncRuleMapping.FlowsOnUpdateExport"/> is the single predicate both the export
    /// evaluation and Drift Correction paths consult, so its contract is asserted directly.
    /// </summary>
    [Test]
    public void FlowsOnUpdateExport_ReflectsWritabilityAndInitialExportOnly()
    {
        // Arrange
        var writable = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "Writable", Writability = AttributeWritability.Writable };
        var writableOnCreate = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "Key", Writability = AttributeWritability.WritableOnCreate };
        var readOnly = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "ReadOnly", Writability = AttributeWritability.ReadOnly };

        // Act & Assert
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new SyncRuleMapping { TargetConnectedSystemAttribute = writable }.FlowsOnUpdateExport(), Is.True);
            Assert.That(new SyncRuleMapping { TargetConnectedSystemAttribute = writableOnCreate }.FlowsOnUpdateExport(), Is.False);
            Assert.That(new SyncRuleMapping { TargetConnectedSystemAttribute = readOnly }.FlowsOnUpdateExport(), Is.True,
                "A read-only target is refused at authoring time, so the update-flow predicate has no opinion on it");
            Assert.That(new SyncRuleMapping { TargetConnectedSystemAttribute = writable, InitialExportOnly = true }.FlowsOnUpdateExport(), Is.False);
            Assert.That(new SyncRuleMapping().FlowsOnUpdateExport(), Is.True,
                "An import mapping has no Connected System target and is unaffected");
        }
    }

    #region helpers
    private ConnectedSystemObjectTypeAttribute GetTargetAttribute(MockTargetSystemAttributeNames name)
    {
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        return targetUserType.Attributes.Single(a => a.Name == name.ToString());
    }

    private (MetaverseObject Mvo, SyncRule ExportRule, List<MetaverseObjectAttributeValue> ChangedAttributes) BuildDirectMappingScenario(
        AttributeWritability writability, string displayName, bool initialExportOnly = false)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);
        targetDisplayNameAttr.Writability = writability;

        var exportRule = new SyncRule { Id = 1, Name = "Writable On Create Test Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(CreateDirectMapping(100, exportRule, displayNameMvAttr, targetDisplayNameAttr, initialExportOnly));

        var mvo = CreateMvo(mvUserType, CreateStringAttributeValue(displayNameMvAttr, displayName));
        return (mvo, exportRule, mvo.AttributeValues.ToList());
    }

    private (MetaverseObject Mvo, SyncRule ExportRule) BuildExpressionMappingScenario(AttributeWritability writability, string displayName)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var targetDisplayNameAttr = GetTargetAttribute(MockTargetSystemAttributeNames.DisplayName);
        targetDisplayNameAttr.Writability = writability;

        var exportRule = new SyncRule { Id = 1, Name = "Writable On Create Expression Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
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
        int id, SyncRule rule, MetaverseAttribute sourceAttr, ConnectedSystemObjectTypeAttribute targetAttr, bool initialExportOnly = false)
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
