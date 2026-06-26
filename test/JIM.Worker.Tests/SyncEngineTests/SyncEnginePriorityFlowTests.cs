// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for attribute priority resolution wired into the inbound attribute-flow engine (#91):
/// provenance stamping (<see cref="MetaverseObjectAttributeValue.ContributedBySyncRuleId"/>) and the inline
/// incumbent-comparison gate (a losing contribution never reaches the Metaverse Object). No mocking, no database.
/// </summary>
public class SyncEnginePriorityFlowTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    private static ConnectedSystemObjectType TextCsoType(int csoAttrId = 200, string name = "attr") => new()
    {
        Id = 1,
        Attributes = [new ConnectedSystemObjectTypeAttribute { Id = csoAttrId, Name = name, Type = AttributeDataType.Text }]
    };

    private static (ConnectedSystemObject cso, MetaverseObject mvo) JoinedPair(string sourceValue, int connectedSystemId = 5, int csoAttrId = 200)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            TypeId = 1,
            ConnectedSystemId = connectedSystemId,
            MetaverseObject = mvo
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = csoAttrId, StringValue = sourceValue });
        return (cso, mvo);
    }

    private static SyncRule SingleMappingRule(MetaverseAttribute target, int syncRuleId, int csoAttrId = 200, int priority = int.MaxValue)
    {
        var mapping = new SyncRuleMapping { Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = priority };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = csoAttrId, Order = 1 });
        return new SyncRule { Id = syncRuleId, AttributeFlowRules = [mapping] };
    }

    [Test]
    public void FlowInboundAttributes_WrittenValue_IsStampedWithContributingSyncRuleId()
    {
        // The winning contribution must record which sync rule contributed it (provenance), not just the system.
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "department", Type = AttributeDataType.Text };
        var (cso, mvo) = JoinedPair("Engineering");
        var syncRule = SingleMappingRule(mvoAttr, syncRuleId: 7);

        _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { TextCsoType() });

        Assert.That(mvo.PendingAttributeValueAdditions.Count, Is.EqualTo(1));
        var written = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(written.StringValue, Is.EqualTo("Engineering"));
        Assert.That(written.ContributedBySyncRuleId, Is.EqualTo(7), "the winning sync rule id must be stamped on the value");
        Assert.That(written.ContributedBySystemId, Is.EqualTo(5), "the contributing system id is retained alongside the rule id");
    }
}
