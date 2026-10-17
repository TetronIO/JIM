// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Export evaluation never stages a change to an attribute on the credential denylist, on any Connector (#2091). A
/// mapping that predates the denylist entry (a <c>password</c> Attribute Flow on a SCIM, SQL or File system) would
/// otherwise keep staging the secret into Pending Exports until an administrator refreshed the schema; nothing is
/// staged, the object's other attributes export, and the execution item says why.
/// </summary>
[TestFixture]
public class ExportCredentialAttributeTests
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

    [TestCase(PendingExportChangeType.Create)]
    [TestCase(PendingExportChangeType.Update)]
    public void CreateAttributeValueChanges_MappingToADeniedAttribute_StagesNothingForItAndRecordsWhy(PendingExportChangeType changeType)
    {
        var secret = MvAttribute(1, "initialSecret");
        var title = MvAttribute(2, "title");
        var password = CsAttribute(10, "Password");
        var csTitle = CsAttribute(11, "title");
        var rule = ExportRule((secret, password), (title, csTitle));
        var mvo = Mvo(TextValue(secret, "S3cret!"), TextValue(title, "Engineer"));
        var flowErrors = new List<AttributeFlowError>();

        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo, rule, changeType == PendingExportChangeType.Update ? mvo.AttributeValues.ToList() : [], changeType,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _,
            flowErrors: flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.Attribute.Name), Is.EqualTo(new[] { "title" }));
            Assert.That(changes.Select(c => c.StringValue), Does.Not.Contain("S3cret!"));
            Assert.That(flowErrors, Has.Count.EqualTo(1));
            Assert.That(flowErrors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.CredentialAttributeTarget));
            Assert.That(flowErrors[0].TargetAttributeName, Is.EqualTo("Password"));
            Assert.That(flowErrors[0].Value, Is.Null, "the error never carries the value");
        }
    }

    [Test]
    public void CreateAttributeValueChanges_ExpressionMappingToADeniedAttribute_IsNotEvaluatedOrStaged()
    {
        var title = MvAttribute(2, "title");
        var password = CsAttribute(10, "password");
        var rule = new SyncRule { Id = 1, Name = "Export", Direction = SyncRuleDirection.Export };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = rule,
            TargetConnectedSystemAttribute = password,
            TargetConnectedSystemAttributeId = password.Id,
            Sources = { new SyncRuleMappingSource { Id = 200, Order = 0, Expression = "\"Welcome1\"" } }
        });
        var flowErrors = new List<AttributeFlowError>();

        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            Mvo(TextValue(title, "Engineer")), rule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, csoAlreadyCurrentCount: out _,
            flowErrors: flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty);
            Assert.That(flowErrors.Select(e => e.Kind), Is.EqualTo(new[] { AttributeFlowErrorKind.CredentialAttributeTarget }));
        }
    }

    #region helpers
    private static MetaverseAttribute MvAttribute(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued
    };

    private static ConnectedSystemObjectTypeAttribute CsAttribute(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued
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

    private static SyncRule ExportRule(params (MetaverseAttribute Source, ConnectedSystemObjectTypeAttribute Target)[] mappings)
    {
        var rule = new SyncRule { Id = 1, Name = "Export", Direction = SyncRuleDirection.Export };
        var id = 100;
        foreach (var (source, target) in mappings)
        {
            rule.AttributeFlowRules.Add(new SyncRuleMapping
            {
                Id = id++,
                SyncRule = rule,
                TargetConnectedSystemAttribute = target,
                TargetConnectedSystemAttributeId = target.Id,
                Sources = { new SyncRuleMappingSource { Id = id++, Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
            });
        }
        return rule;
    }
    #endregion
}
