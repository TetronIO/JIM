// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Proves export evaluation (#435) refuses to export a multi-valued Metaverse source attribute to a
/// single-valued Connected System attribute when the Metaverse Object holds more than one value: no
/// Pending Export change is generated for that attribute and an <see cref="AttributeFlowError"/> is
/// recorded, while the object's other attributes are unaffected. A single value exports normally.
/// </summary>
[TestFixture]
public class ExportMultiValuedToSingleValuedTests
{
    private JimApplication Jim { get; set; } = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        // CreateAttributeValueChanges never queries the database, so a bare mocked context suffices.
        var mockJimDbContext = new Mock<JimDbContext>();
        Jim = new JimApplication(new PostgresDataRepository(mockJimDbContext.Object), syncRepository: new SyncRepository());
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [Test]
    public void CreateAttributeValueChanges_TextMvaToSva_MultipleValues_EmitsNoChangeAndRecordsError()
    {
        // Arrange — a multi-valued Metaverse source with 3 values mapping to a single-valued CS target
        var sourceAttr = MvAttribute("mail", AttributePlurality.MultiValued);
        var targetAttr = CsAttribute("mail", AttributePlurality.SingleValued);
        var exportRule = ExportRule(sourceAttr, targetAttr);
        var mvo = Mvo(
            TextValue(sourceAttr, "alice@example.com"),
            TextValue(sourceAttr, "bob@example.com"),
            TextValue(sourceAttr, "carol@example.com"));

        var flowErrors = new List<AttributeFlowError>();

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _,
            flowErrors: flowErrors);

        // Assert — nothing exported for this attribute, and the error carries the attribute names and count
        Assert.That(changes, Is.Empty);
        Assert.That(flowErrors, Has.Count.EqualTo(1));
        Assert.That(flowErrors[0].SourceAttributeName, Is.EqualTo("mail"));
        Assert.That(flowErrors[0].TargetAttributeName, Is.EqualTo("mail"));
        Assert.That(flowErrors[0].ValueCount, Is.EqualTo(3));
    }

    [Test]
    public void CreateAttributeValueChanges_TextMvaToSva_SingleValue_ExportsNormally_NoError()
    {
        // Arrange — the source is multi-valued but holds only one value, so it exports to the single-valued target
        var sourceAttr = MvAttribute("mail", AttributePlurality.MultiValued);
        var targetAttr = CsAttribute("mail", AttributePlurality.SingleValued);
        var exportRule = ExportRule(sourceAttr, targetAttr);
        var mvo = Mvo(TextValue(sourceAttr, "only@example.com"));

        var flowErrors = new List<AttributeFlowError>();

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _,
            flowErrors: flowErrors);

        // Assert — the single value exports, no error
        Assert.That(changes, Has.Count.EqualTo(1));
        Assert.That(changes[0].StringValue, Is.EqualTo("only@example.com"));
        Assert.That(flowErrors, Is.Empty);
    }

    [Test]
    public void CreateAttributeValueChanges_TextMvaToMva_MultipleValues_ExportsAll_NoError()
    {
        // Arrange — multi-valued source to a multi-valued target: all values export, no error
        var sourceAttr = MvAttribute("proxyAddresses", AttributePlurality.MultiValued);
        var targetAttr = CsAttribute("proxyAddresses", AttributePlurality.MultiValued);
        var exportRule = ExportRule(sourceAttr, targetAttr);
        var mvo = Mvo(
            TextValue(sourceAttr, "smtp:alice@example.com"),
            TextValue(sourceAttr, "smtp:alice@contoso.com"));

        var flowErrors = new List<AttributeFlowError>();

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, exportRule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _,
            flowErrors: flowErrors);

        // Assert
        Assert.That(changes, Has.Count.EqualTo(2));
        Assert.That(flowErrors, Is.Empty);
    }

    #region helpers
    private static MetaverseAttribute MvAttribute(string name, AttributePlurality plurality) => new()
    {
        Id = 100 + name.Length,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = plurality
    };

    private static ConnectedSystemObjectTypeAttribute CsAttribute(string name, AttributePlurality plurality) => new()
    {
        Id = 200 + name.Length,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = plurality
    };

    private static MetaverseObjectAttributeValue TextValue(MetaverseAttribute attribute, string value) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attribute.Id,
        Attribute = attribute,
        StringValue = value
    };

    private static MetaverseObject Mvo(params MetaverseObjectAttributeValue[] attributeValues)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var attributeValue in attributeValues)
        {
            attributeValue.MetaverseObject = mvo;
            mvo.AttributeValues.Add(attributeValue);
        }
        return mvo;
    }

    private static SyncRule ExportRule(MetaverseAttribute sourceAttr, ConnectedSystemObjectTypeAttribute targetAttr)
    {
        var rule = new SyncRule { Id = 1, Name = "MVA to SVA Export Rule", Direction = SyncRuleDirection.Export };
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
