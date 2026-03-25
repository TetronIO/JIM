using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
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

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("John Doe"));
        Assert.That(mvo.PendingAttributeValueAdditions.First().ContributedBySystemId, Is.EqualTo(5));
    }

    [Test]
    public void FlowInboundAttributes_NullMvo_DoesNotThrow()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = null };
        var syncRule = new SyncRule { AttributeFlowRules = [] };

        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, Array.Empty<ConnectedSystemObjectType>()));
    }
}
