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

        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("John Doe"));
        Assert.That(mvo.PendingAttributeValueAdditions.First().ContributedBySystemId, Is.EqualTo(5));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_NullMvo_DoesNotThrow()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = null };
        var syncRule = new SyncRule { AttributeFlowRules = [] };

        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, Array.Empty<ConnectedSystemObjectType>()));
    }

    #region Multi-valued to single-valued truncation (#435)

    [Test]
    public void FlowInboundAttributes_TextMvaToSva_SelectsFirstValueAndGeneratesWarning()
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
        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — only the first value flows
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("alice@example.com"));

        // Assert — a warning was generated
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].SourceAttributeName, Is.EqualTo("mail"));
        Assert.That(warnings[0].TargetAttributeName, Is.EqualTo("mail"));
        Assert.That(warnings[0].ValueCount, Is.EqualTo(3));
        Assert.That(warnings[0].SelectedValue, Is.EqualTo("alice@example.com"));
    }

    [Test]
    public void FlowInboundAttributes_NumberMvaToSva_SelectsFirstValueAndGeneratesWarning()
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
        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — only the first value flows
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().IntValue, Is.EqualTo(42));

        // Assert — a warning was generated
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].ValueCount, Is.EqualTo(2));
    }

    [Test]
    public void FlowInboundAttributes_GuidMvaToSva_SelectsFirstValueAndGeneratesWarning()
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
        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().GuidValue, Is.EqualTo(guid1));
        Assert.That(warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void FlowInboundAttributes_BinaryMvaToSva_SelectsFirstValueAndGeneratesWarning()
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
        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().ByteValue, Is.EqualTo(bytes1));
        Assert.That(warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void FlowInboundAttributes_SingleCsoValue_ToSva_NoWarning()
    {
        // Arrange — only one value, so no truncation warning
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
        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — value flows normally, no warning
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("only@example.com"));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_MvaToMva_NoWarning()
    {
        // Arrange — multi-valued to multi-valued should flow all values with no warning
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
        var warnings = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        // Assert — both values flow, no warning
        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(2));
        Assert.That(warnings, Is.Empty);
    }

    #endregion
}
