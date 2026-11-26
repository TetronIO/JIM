// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Inbound attribute flow into an Accumulate attribute (#3059): every contributor adds its values, nothing a contributor
/// stops supplying is removed, and each distinct value is held once, keeping the contributor that first supplied it.
/// Pure unit tests over the engine; no database.
/// </summary>
public class SyncEngineAccumulateFlowTests
{
    private const int MvoTypeId = 10;
    private const int TextCsoAttrId = 200;
    private const int NumberCsoAttrId = 201;

    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new Application.Servers.SyncEngine();

    private static MetaverseAttribute AuditLog() => new()
    {
        Id = 100,
        Name = "Account Audit Log",
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.MultiValued,
        ContributionMode = AttributeContributionMode.Accumulate
    };

    private static MetaverseAttribute AccountNumbers() => new()
    {
        Id = 101,
        Name = "Account Numbers",
        Type = AttributeDataType.Number,
        AttributePlurality = AttributePlurality.MultiValued,
        ContributionMode = AttributeContributionMode.Accumulate
    };

    private static List<ConnectedSystemObjectType> ObjectTypes() =>
    [
        new()
        {
            Id = 1,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = TextCsoAttrId, Name = "identifiers", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new ConnectedSystemObjectTypeAttribute { Id = NumberCsoAttrId, Name = "numbers", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.MultiValued }
            ]
        }
    ];

    private static SyncRule Rule(int syncRuleId, int priority, MetaverseAttribute target, int csoAttrId = TextCsoAttrId, bool nullIsValue = false, string? expression = null)
    {
        var mapping = new SyncRuleMapping
        {
            Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = priority, NullIsValue = nullIsValue
        };
        mapping.Sources.Add(expression == null
            ? new SyncRuleMappingSource { ConnectedSystemAttributeId = csoAttrId, Order = 1 }
            : new SyncRuleMappingSource { Expression = expression, Order = 1 });
        return new SyncRule
        {
            Id = syncRuleId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [mapping]
        };
    }

    private static ConnectedSystemObject Cso(MetaverseObject mvo, int connectedSystemId, params string[] values)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = connectedSystemId, MetaverseObject = mvo };
        foreach (var value in values)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = TextCsoAttrId, StringValue = value });
        return cso;
    }

    private static void Hold(MetaverseObject mvo, MetaverseAttribute attr, string value, int syncRuleId, int systemId) =>
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = attr, AttributeId = attr.Id, StringValue = value, ContributedBySyncRuleId = syncRuleId, ContributedBySystemId = systemId
        });

    [Test]
    public void FlowInboundAttributes_LowerPriorityContributor_AddsBesideTheIncumbentAndTakesNothingOver()
    {
        var log = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, log);
        var entra = Rule(syncRuleId: 2, priority: 2, log);
        var context = new AttributePriorityContext([ad, entra]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1, systemId: 9);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "Entra|9a2e|objectId=9a2e"), entra, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "Accumulate never removes a value");
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => (v.StringValue, v.ContributedBySyncRuleId)),
                Is.EqualTo(new[] { ("Entra|9a2e|objectId=9a2e", (int?)2) }), "a lower-priority contributor still adds its value");
            Assert.That(mvo.AttributeValues.Single().ContributedBySyncRuleId, Is.EqualTo(1), "the incumbent keeps its contributor");
            Assert.That(mvo.HasPendingProvenanceChanges, Is.False);
        }
    }

    [Test]
    public void FlowInboundAttributes_SourceValueRenamed_AddsTheNewValueAndKeepsTheOld()
    {
        var log = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, log);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "AD|5f1c|sAMAccountName=jsmith2"), ad, ObjectTypes(), priorityContext: new AttributePriorityContext([ad]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EqualTo(new[] { "AD|5f1c|sAMAccountName=jsmith2" }));
        }
    }

    [Test]
    public void FlowInboundAttributes_ValueAlreadyHeldFromAnotherContributor_IsNotAddedAgainAndKeepsTheFirstContributor()
    {
        var log = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, log);
        var mail = Rule(syncRuleId: 2, priority: 2, log);
        var context = new AttributePriorityContext([ad, mail]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "b@corp.example", syncRuleId: 2, systemId: 6);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "b@corp.example"), ad, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.AttributeValues.Single().ContributedBySyncRuleId, Is.EqualTo(2), "the first contributor stays recorded");
            Assert.That(mvo.HasPendingProvenanceChanges, Is.False);
        }
    }

    [Test]
    public void FlowInboundAttributes_ValuesCompareExactly_ADifferentCaseIsASecondValue()
    {
        var log = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, log);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "jo@corp.example", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "Jo@corp.example"), ad, ObjectTypes(), priorityContext: new AttributePriorityContext([ad]));

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EqualTo(new[] { "Jo@corp.example" }));
    }

    [Test]
    public void FlowInboundAttributes_TwoRulesInOnePassSupplyTheSameValue_AddsItOnceForTheFirst()
    {
        // Two import rules on the same Connected System run in the same pass, so the second must see the first's
        // pending addition as held, or the object would gain two rows for one value.
        var log = AuditLog();
        var first = Rule(syncRuleId: 1, priority: 1, log);
        var second = Rule(syncRuleId: 2, priority: 2, log);
        var context = new AttributePriorityContext([first, second]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = Cso(mvo, 5, "AD|5f1c|sAMAccountName=jsmith");

        _engine.FlowInboundAttributes(cso, first, ObjectTypes(), priorityContext: context);
        _engine.FlowInboundAttributes(cso, second, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => (v.StringValue, v.ContributedBySyncRuleId)),
            Is.EqualTo(new[] { ("AD|5f1c|sAMAccountName=jsmith", (int?)1) }));
    }

    [Test]
    public void FlowInboundAttributes_HigherPriorityRuleInSamePass_DoesNotDiscardALowerRulesPendingValue()
    {
        // Under Priority, a winning rule discards a losing rule's pending additions for the attribute (#1199). Under
        // Accumulate every contributor's values stand.
        var log = AuditLog();
        var high = Rule(syncRuleId: 1, priority: 1, log);
        var low = Rule(syncRuleId: 2, priority: 2, log);
        var context = new AttributePriorityContext([high, low]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        _engine.FlowInboundAttributes(Cso(mvo, 5, "from-low"), low, ObjectTypes(), priorityContext: context);
        _engine.FlowInboundAttributes(Cso(mvo, 5, "from-high"), high, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EquivalentTo(new[] { "from-low", "from-high" }));
    }

    [Test]
    public void FlowInboundAttributes_ConnectedNoValueWithNullIsValue_RemovesNothingAndWritesNoMarker()
    {
        var log = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, log, nullIsValue: true);
        var context = new AttributePriorityContext([ad]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5), ad, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "\"Null is a value\" has no effect on an Accumulate attribute");
        }
    }

    [Test]
    public void FlowInboundAttributes_ConnectedNoValueWithoutPriorityContext_RemovesNothing()
    {
        // Without a priority context a no-value contribution historically clears the attribute; Accumulate never does.
        var log = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, log);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5), ad, ObjectTypes());

        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_TypedValueChanged_AddsTheNewValueAndKeepsTheOld()
    {
        var numbers = AccountNumbers();
        var rule = Rule(syncRuleId: 1, priority: 1, numbers, csoAttrId: NumberCsoAttrId);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = numbers, AttributeId = numbers.Id, IntValue = 1, ContributedBySyncRuleId = 1, ContributedBySystemId = 5 });
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = NumberCsoAttrId, IntValue = 2 });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = NumberCsoAttrId, IntValue = 3 });

        _engine.FlowInboundAttributes(cso, rule, ObjectTypes(), priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.IntValue), Is.EquivalentTo(new int?[] { 2, 3 }));
        }
    }

    [Test]
    public void FlowInboundAttributes_TypedSingleSourceValueChanged_AddsTheNewValueAndKeepsTheOld()
    {
        // The typed writer takes a separate path for a single source value.
        var numbers = AccountNumbers();
        var rule = Rule(syncRuleId: 1, priority: 1, numbers, csoAttrId: NumberCsoAttrId);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = numbers, AttributeId = numbers.Id, IntValue = 1, ContributedBySyncRuleId = 1, ContributedBySystemId = 5 });
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = NumberCsoAttrId, IntValue = 2 });

        _engine.FlowInboundAttributes(cso, rule, ObjectTypes(), priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.IntValue), Is.EqualTo(new int?[] { 2 }));
        }
    }

    [Test]
    public void FlowInboundAttributes_ExpressionResultChanged_AddsTheNewValuesAndKeepsTheOld()
    {
        var log = AuditLog();
        var rule = Rule(syncRuleId: 1, priority: 1, log, expression: "Split(\"...\", \";\")");
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, log, "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1, systemId: 5);
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>()))
            .Returns(new[] { "AD|5f1c|sAMAccountName=jsmith2", "AD|5f1c|userPrincipalName=jo@corp.example" });

        _engine.FlowInboundAttributes(Cso(mvo, 5, "unused"), rule, ObjectTypes(), expressionEvaluator: evaluator.Object,
            priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => (v.StringValue, v.ContributedBySyncRuleId)), Is.EquivalentTo(new[]
            {
                ("AD|5f1c|sAMAccountName=jsmith2", (int?)1),
                ("AD|5f1c|userPrincipalName=jo@corp.example", (int?)1)
            }));
        }
    }

    [Test]
    public void FlowInboundAttributes_PriorityAttribute_StillReplacesTheSet()
    {
        // Guard: the default mode is unchanged.
        var dept = new MetaverseAttribute { Id = 102, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var rule = Rule(syncRuleId: 1, priority: 1, dept);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, dept, "old", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "new"), rule, ObjectTypes(), priorityContext: new AttributePriorityContext([rule]));

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Is.EqualTo(new[] { "old" }));
    }
}
