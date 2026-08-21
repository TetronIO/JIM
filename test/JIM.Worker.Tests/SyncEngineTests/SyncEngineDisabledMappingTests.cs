// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// A disabled Attribute Flow mapping (#1485) must not flow inbound: the whole point of disabling one is that
/// nothing runs over an attribute the source no longer offers (or has redefined) while the administrator
/// reworks the configuration. Pure unit tests against SyncEngine.FlowInboundAttributes, no mocking.
/// </summary>
public class SyncEngineDisabledMappingTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void FlowInboundAttributes_DisabledMapping_DoesNotFlowAndReportsNoError()
    {
        // A disabled mapping is a deliberate administrator (or refresh decision) choice, so skipping it is
        // correct behaviour, not an error: nothing flows and nothing is reported against the object.
        var (cso, mvo, syncRule, csoType) = BuildInboundScenario(enabled: false);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty,
                "A disabled mapping must not contribute values.");
            Assert.That(errors, Is.Empty,
                "A deliberate disable is not a per-object error; the run-level summary is where skips are counted.");
        }
    }

    [Test]
    public void FlowInboundAttributes_EnabledMapping_StillFlows()
    {
        // Regression guard on the same arrangement: the gate must key on Enabled, not suppress everything.
        var (cso, mvo, syncRule, csoType) = BuildInboundScenario(enabled: true);

        var errors = _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
            Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("John Doe"));
            Assert.That(errors, Is.Empty);
        }
    }

    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule Rule, ConnectedSystemObjectType CsoType) BuildInboundScenario(bool enabled)
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "displayName", Type = AttributeDataType.Text };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "cn", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

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

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr, Enabled = enabled };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, ConnectedSystemAttribute = csoAttr, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType);
    }
}
