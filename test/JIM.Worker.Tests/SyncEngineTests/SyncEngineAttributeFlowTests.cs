// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.FlowInboundAttributes and ApplyPendingAttributeChanges — no mocking, no database.
/// </summary>
public class SyncEngineAttributeFlowTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void ApplyPendingAttributeChanges_NoPendingChanges_DoesNothing()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { StringValue = "existing" });

        _engine.ApplyPendingAttributeChanges(mvo);

        Assert.That(mvo.AttributeValues.Count, Is.EqualTo(1));
    }

    [Test]
    public void ApplyPendingAttributeChanges_AppliesAdditions()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var newValue = new MetaverseObjectAttributeValue { StringValue = "new" };
        mvo.PendingAttributeValueAdditions.Add(newValue);

        _engine.ApplyPendingAttributeChanges(mvo);

        Assert.That(mvo.AttributeValues, Contains.Item(newValue));
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
    }

    [Test]
    public void ApplyPendingAttributeChanges_AppliesRemovals()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var existingValue = new MetaverseObjectAttributeValue { StringValue = "old" };
        mvo.AttributeValues.Add(existingValue);
        mvo.PendingAttributeValueRemovals.Add(existingValue);

        _engine.ApplyPendingAttributeChanges(mvo);

        Assert.That(mvo.AttributeValues, Does.Not.Contain(existingValue));
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
    }

    [Test]
    public void ApplyPendingAttributeChanges_AppliesRemovalsThenAdditions()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var attr = new MetaverseAttribute { Id = 1, Name = "displayName" };

        var oldValue = new MetaverseObjectAttributeValue { Attribute = attr, AttributeId = 1, StringValue = "Old Name" };
        var newValue = new MetaverseObjectAttributeValue { Attribute = attr, AttributeId = 1, StringValue = "New Name" };

        mvo.AttributeValues.Add(oldValue);
        mvo.PendingAttributeValueRemovals.Add(oldValue);
        mvo.PendingAttributeValueAdditions.Add(newValue);

        _engine.ApplyPendingAttributeChanges(mvo);

        Assert.That(mvo.AttributeValues.Count, Is.EqualTo(1));
        Assert.That(mvo.AttributeValues.First().StringValue, Is.EqualTo("New Name"));
    }

    [Test]
    public void FlowInboundAttributes_TextAttribute_FlowsValue()
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "displayName", Type = AttributeDataType.Text };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "cn", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Attributes = [csoAttr]
        };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            TypeId = 1,
            ConnectedSystemId = 5,
            MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 200,
            StringValue = "John Doe"
        });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("John Doe"));
        Assert.That(mvo.PendingAttributeValueAdditions.First().ContributedBySystemId, Is.EqualTo(5));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_NullMvo_DoesNotThrow()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = null };
        var syncRule = new SyncRule { AttributeFlowRules = [] };

        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, Array.Empty<ConnectedSystemObjectType>()));
    }

    #region Multi-valued to single-valued (#435): more than one value to a single-valued target errors

    [Test]
    public void FlowInboundAttributes_TextMvaToSva_DoesNotFlowAndGeneratesError()
    {
        // Arrange — multi-valued CS attribute with 3 values flowing to a single-valued MV attribute
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "alice@example.com" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "bob@example.com" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "carol@example.com" });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — no value flowed
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);

        // Assert — an error was raised carrying the attribute names and the value count
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].SourceAttributeName, Is.EqualTo("mail"));
        Assert.That(errors[0].TargetAttributeName, Is.EqualTo("mail"));
        Assert.That(errors[0].ValueCount, Is.EqualTo(3));
    }

    [Test]
    public void FlowInboundAttributes_NumberMvaToSva_DoesNotFlowAndGeneratesError()
    {
        // Arrange
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "employeeNumber", Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "employeeNumber", Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, IntValue = 42 });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, IntValue = 99 });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — no value flowed, error raised
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].ValueCount, Is.EqualTo(2));
    }

    [Test]
    public void FlowInboundAttributes_GuidMvaToSva_DoesNotFlowAndGeneratesError()
    {
        // Arrange
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "objectGuid", Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "objectGuid", Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, GuidValue = guid1 });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, GuidValue = guid2 });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [Test]
    public void FlowInboundAttributes_BinaryMvaToSva_DoesNotFlowAndGeneratesError()
    {
        // Arrange
        var bytes1 = new byte[] { 1, 2, 3 };
        var bytes2 = new byte[] { 4, 5, 6 };
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "photo", Type = AttributeDataType.Binary,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "photo", Type = AttributeDataType.Binary,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, ByteValue = bytes1 });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, ByteValue = bytes2 });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [Test]
    public void FlowInboundAttributes_SingleCsoValue_ToSva_Flows_NoError()
    {
        // Arrange — only one value, so it flows to the single-valued target with no error
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "only@example.com" });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — value flows normally, no error
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("only@example.com"));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_TextMvaToSva_DuplicateValues_FlowsSingleValue_NoError()
    {
        // Arrange — two source values that are identical, so the de-duplicated effective count is one:
        // this must flow the single value and NOT error (the trigger is de-duplicated count, not raw count).
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "alice@example.com" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "alice@example.com" });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — the single distinct value flows, no error
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("alice@example.com"));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_TextMvaToSva_ValuesCollapseToOneUnderCaseNormalisation_FlowsSingleValue_NoError()
    {
        // Arrange — two values that differ only by case, with lower-case normalisation, collapse to one
        // distinct effective value, so the flow succeeds and does not error.
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "ALICE@example.com" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "alice@example.com" });

        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = mvoAttr,
            CaseNormalisation = InboundCaseNormalisation.Lower
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — collapses to one distinct value, flows, no error
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("alice@example.com"));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_MvaToMva_FlowsAll_NoError()
    {
        // Arrange — multi-valued to multi-valued should flow all values with no error
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "emails", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "mail", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "alice@example.com" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, StringValue = "bob@example.com" });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1
        });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        // Act
        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — both values flow, no error
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(2));
        Assert.That(errors, Is.Empty);
    }

    #endregion

    #region Decimal attribute flow (#1046)

    /// <summary>
    /// Builds a CSO joined to an MVO with a single direct inbound mapping from a Decimal CS attribute
    /// to a Decimal MV attribute. The caller supplies the CSO and MVO values.
    /// </summary>
    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType, MetaverseAttribute MvoAttr)
        BuildDecimalScenario(
            decimal[] csoValues,
            decimal[] mvoValues,
            AttributePlurality targetPlurality = AttributePlurality.SingleValued,
            AttributePlurality sourcePlurality = AttributePlurality.SingleValued)
    {
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "salary", Type = AttributeDataType.Decimal, AttributePlurality = targetPlurality
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "salary", Type = AttributeDataType.Decimal, AttributePlurality = sourcePlurality
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var mvoValue in mvoValues)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = mvoAttr, AttributeId = 100, DecimalValue = mvoValue });

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        foreach (var csoValue in csoValues)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, DecimalValue = csoValue });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType, mvoAttr);
    }

    [Test]
    public void FlowInboundAttributes_DecimalAttribute_FlowsValue()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDecimalScenario(csoValues: [1234.56m], mvoValues: []);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(1234.56m));
        Assert.That(mvo.PendingAttributeValueAdditions.First().ContributedBySystemId, Is.EqualTo(5));
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_DecimalAttribute_ValueChanged_ReplacesValue()
    {
        var (cso, mvo, syncRule, csoType, _) = BuildDecimalScenario(csoValues: [2.2m], mvoValues: [1.1m]);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueRemovals.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueRemovals.First().DecimalValue, Is.EqualTo(1.1m));
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(2.2m));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_DecimalAttribute_CsoValueGone_RemovesMvoValue()
    {
        // No CSO value for the attribute: the historic clear behaviour (no priority context) removes the MVO value.
        var (cso, mvo, syncRule, csoType, _) = BuildDecimalScenario(csoValues: [], mvoValues: [1.1m]);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueRemovals.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_DecimalAttribute_ScaleOnlyDifference_StagesNoChange()
    {
        // 1.10 on the CSO and 1.1 on the MVO are numerically equal; a scale-only difference must not
        // stage a removal and re-addition (no phantom churn).
        var (cso, mvo, syncRule, csoType, _) = BuildDecimalScenario(csoValues: [1.10m], mvoValues: [1.1m]);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_DecimalMvaToMva_SetDiff_StagesCorrectAddsAndRemoves()
    {
        // MVO holds {1.5, 2.5}; CSO holds {2.50, 3.5}. 2.50 numerically matches 2.5 (kept),
        // 1.5 is obsolete (removed), 3.5 is new (added).
        var (cso, mvo, syncRule, csoType, _) = BuildDecimalScenario(
            csoValues: [2.50m, 3.5m],
            mvoValues: [1.5m, 2.5m],
            targetPlurality: AttributePlurality.MultiValued,
            sourcePlurality: AttributePlurality.MultiValued);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueRemovals.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueRemovals.First().DecimalValue, Is.EqualTo(1.5m));
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(3.5m));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_DecimalMvaToSva_DoesNotFlowAndGeneratesError()
    {
        // Multiple decimal source values flowing to a single-valued target is an error (#435).
        var (cso, mvo, syncRule, csoType, _) = BuildDecimalScenario(
            csoValues: [1.5m, 2.5m],
            mvoValues: [],
            sourcePlurality: AttributePlurality.MultiValued);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].ValueCount, Is.EqualTo(2));
    }

    #endregion

    #region LongNumber attribute flow

    /// <summary>
    /// Builds a CSO joined to an MVO with a single direct inbound mapping from a LongNumber CS attribute
    /// to a LongNumber MV attribute. The caller supplies the CSO and MVO values.
    /// </summary>
    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType)
        BuildLongNumberScenario(
            long[] csoValues,
            long[] mvoValues,
            AttributePlurality targetPlurality = AttributePlurality.SingleValued,
            AttributePlurality sourcePlurality = AttributePlurality.SingleValued)
    {
        var mvoAttr = new MetaverseAttribute
        {
            Id = 100, Name = "usnChanged", Type = AttributeDataType.LongNumber, AttributePlurality = targetPlurality
        };
        var csoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 200, Name = "usnChanged", Type = AttributeDataType.LongNumber, AttributePlurality = sourcePlurality
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var mvoValue in mvoValues)
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = mvoAttr, AttributeId = 100, LongValue = mvoValue });

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo
        };
        foreach (var csoValue in csoValues)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, LongValue = csoValue });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType);
    }

    [Test]
    public void FlowInboundAttributes_LongNumberAttribute_FlowsValue()
    {
        // A value beyond int range proves the flow is lossless end to end.
        var (cso, mvo, syncRule, csoType) = BuildLongNumberScenario(csoValues: [9999999999L], mvoValues: []);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().LongValue, Is.EqualTo(9999999999L));
        Assert.That(mvo.PendingAttributeValueAdditions.First().ContributedBySystemId, Is.EqualTo(5));
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_LongNumberAttribute_ValueChanged_ReplacesValue()
    {
        var (cso, mvo, syncRule, csoType) = BuildLongNumberScenario(csoValues: [222L], mvoValues: [111L]);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueRemovals.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueRemovals.First().LongValue, Is.EqualTo(111L));
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().LongValue, Is.EqualTo(222L));
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_LongNumberAttribute_ValueUnchanged_StagesNoChange()
    {
        var (cso, mvo, syncRule, csoType) = BuildLongNumberScenario(csoValues: [9999999999L], mvoValues: [9999999999L]);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_LongNumberMvaToSva_DoesNotFlowAndGeneratesError()
    {
        // Multiple long source values flowing to a single-valued target is an error (#435).
        var (cso, mvo, syncRule, csoType) = BuildLongNumberScenario(
            csoValues: [1L, 2L],
            mvoValues: [],
            sourcePlurality: AttributePlurality.MultiValued);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].ValueCount, Is.EqualTo(2));
    }

    #endregion
}
