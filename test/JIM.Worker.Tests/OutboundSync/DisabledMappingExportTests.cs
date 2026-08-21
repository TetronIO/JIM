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
/// A disabled Attribute Flow mapping (#1485) must not flow on export, on Create as much as on Update:
/// unlike Initial Export Only (which is "flow at provisioning, then stop"), disabled means the mapping does
/// not run at all until an administrator re-enables it.
/// </summary>
[TestFixture]
public class DisabledMappingExportTests
{
    private JimApplication Jim { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;

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

    [Test]
    public void CreateAttributeValueChanges_DisabledMapping_OnCreate_SkipsChange()
    {
        var (mvo, exportRule, _) = BuildDirectMappingScenario(enabled: false, displayName: "Initial Value");

        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        Assert.That(changes, Is.Empty,
            "A disabled mapping must not flow at provisioning; disabled means the mapping does not run at all.");
    }

    [Test]
    public void CreateAttributeValueChanges_DisabledMapping_OnUpdate_SkipsChange()
    {
        var (mvo, exportRule, changedAttributes) = BuildDirectMappingScenario(enabled: false, displayName: "Updated Value");

        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void CreateAttributeValueChanges_EnabledMapping_OnUpdate_StillFlows()
    {
        var (mvo, exportRule, changedAttributes) = BuildDirectMappingScenario(enabled: true, displayName: "Updated Value");

        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, changedAttributes, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1), "The gate must key on Enabled, not suppress every mapping.");
            Assert.That(changes[0].StringValue, Is.EqualTo("Updated Value"));
        }
    }

    private (MetaverseObject Mvo, SyncRule ExportRule, List<MetaverseObjectAttributeValue> ChangedAttributes) BuildDirectMappingScenario(
        bool enabled, string displayName)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var targetDisplayNameAttr = ConnectedSystemObjectTypesData
            .SelectMany(t => t.Attributes)
            .First(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());

        var exportRule = new SyncRule { Id = 1, Name = "Disabled Mapping Test Rule", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Enabled = enabled,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Id = 200,
                    Order = 0,
                    MetaverseAttribute = displayNameMvAttr,
                    MetaverseAttributeId = displayNameMvAttr.Id
                }
            }
        });

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvUserType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = displayNameMvAttr,
            AttributeId = displayNameMvAttr.Id,
            StringValue = displayName
        });

        return (mvo, exportRule, mvo.AttributeValues.ToList());
    }
}
