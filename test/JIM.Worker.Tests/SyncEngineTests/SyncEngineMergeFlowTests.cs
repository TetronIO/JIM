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
/// Inbound attribute flow into a Merge attribute (#3059): every contributor adds its values, a contributor that stops
/// supplying a value withdraws only its own copy, and a value another contributor still supplies is re-tagged to it
/// rather than removed and added again. Pure unit tests over the engine; no database.
/// </summary>
public class SyncEngineMergeFlowTests
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
        Name = "Email Addresses",
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.MultiValued,
        ContributionMode = AttributeContributionMode.Merge
    };

    private static MetaverseAttribute AccountNumbers() => new()
    {
        Id = 101,
        Name = "Account Numbers",
        Type = AttributeDataType.Number,
        AttributePlurality = AttributePlurality.MultiValued,
        ContributionMode = AttributeContributionMode.Merge
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

    private static MetaverseObjectAttributeValue Hold(MetaverseObject mvo, MetaverseAttribute attr, string value, int syncRuleId, int systemId)
    {
        var av = new MetaverseObjectAttributeValue
        {
            Attribute = attr, AttributeId = attr.Id, StringValue = value, ContributedBySyncRuleId = syncRuleId, ContributedBySystemId = systemId
        };
        mvo.AttributeValues.Add(av);
        return av;
    }

    [Test]
    public void FlowInboundAttributes_LowerPriorityContributor_AddsBesideTheIncumbentAndTakesNothingOver()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mail = Rule(syncRuleId: 2, priority: 2, emails);
        var context = new AttributePriorityContext([ad, mail]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 9);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "c@corp.example"), mail, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "a contributor never withdraws another's value");
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => (v.StringValue, v.ContributedBySyncRuleId)),
                Is.EqualTo(new[] { ("c@corp.example", (int?)2) }));
            Assert.That(mvo.AttributeValues.Single().ContributedBySyncRuleId, Is.EqualTo(1));
            Assert.That(mvo.HasPendingProvenanceChanges, Is.False);
        }
    }

    [Test]
    public void FlowInboundAttributes_ContributorDropsAValue_WithdrawsOnlyItsOwnCopy()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mail = Rule(syncRuleId: 2, priority: 2, emails);
        var context = new AttributePriorityContext([ad, mail]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var adA = Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 5);
        Hold(mvo, emails, "b@corp.example", syncRuleId: 1, systemId: 5);
        Hold(mvo, emails, "c@corp.example", syncRuleId: 2, systemId: 6);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "b@corp.example"), ad, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { adA }), "only AD's own copy of the dropped value goes");
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_SourceValueRenamed_AddsTheNewValueAndWithdrawsItsOwnOld()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var old = Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "a2@corp.example"), ad, ObjectTypes(), priorityContext: new AttributePriorityContext([ad]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { old }));
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EqualTo(new[] { "a2@corp.example" }));
        }
    }

    [Test]
    public void FlowInboundAttributes_ValueAlreadyHeldFromAnotherContributor_IsNotAddedAgainAndStaysWithItsContributor()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mail = Rule(syncRuleId: 2, priority: 2, emails);
        var context = new AttributePriorityContext([ad, mail]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, emails, "b@corp.example", syncRuleId: 2, systemId: 6);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "b@corp.example"), ad, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.AttributeValues.Single().ContributedBySyncRuleId, Is.EqualTo(2));
        }
    }

    [Test]
    public void FlowInboundAttributes_ConnectedNoValueWithNullIsValue_WithdrawsOnlyItsOwnValuesAndWritesNoMarker()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails, nullIsValue: true);
        var mail = Rule(syncRuleId: 2, priority: 2, emails);
        var context = new AttributePriorityContext([ad, mail]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var adValue = Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 5);
        Hold(mvo, emails, "c@corp.example", syncRuleId: 2, systemId: 6);

        _engine.FlowInboundAttributes(Cso(mvo, 5), ad, ObjectTypes(), priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { adValue }));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "\"Null is a value\" has no effect on a Merge attribute");
        }
    }

    [Test]
    public void FlowInboundAttributes_TwoRulesInOnePassSupplyTheSameValue_AddsItOnceForTheFirst()
    {
        var emails = AuditLog();
        var first = Rule(syncRuleId: 1, priority: 1, emails);
        var second = Rule(syncRuleId: 2, priority: 2, emails);
        var context = new AttributePriorityContext([first, second]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = Cso(mvo, 5, "a@corp.example");

        _engine.FlowInboundAttributes(cso, first, ObjectTypes(), priorityContext: context);
        _engine.FlowInboundAttributes(cso, second, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => (v.StringValue, v.ContributedBySyncRuleId)),
            Is.EqualTo(new[] { ("a@corp.example", (int?)1) }));
    }

    [Test]
    public void FlowInboundAttributes_TypedValueChanged_AddsTheNewValueAndWithdrawsItsOwnOld()
    {
        var numbers = AccountNumbers();
        var rule = Rule(syncRuleId: 1, priority: 1, numbers, csoAttrId: NumberCsoAttrId);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var old = new MetaverseObjectAttributeValue { Attribute = numbers, AttributeId = numbers.Id, IntValue = 1, ContributedBySyncRuleId = 1, ContributedBySystemId = 5 };
        mvo.AttributeValues.Add(old);
        var other = new MetaverseObjectAttributeValue { Attribute = numbers, AttributeId = numbers.Id, IntValue = 9, ContributedBySyncRuleId = 2, ContributedBySystemId = 6 };
        mvo.AttributeValues.Add(other);
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = NumberCsoAttrId, IntValue = 2 });

        _engine.FlowInboundAttributes(cso, rule, ObjectTypes(), priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { old }));
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.IntValue), Is.EqualTo(new int?[] { 2 }));
        }
    }

    [Test]
    public void FlowInboundAttributes_ExpressionResultChanged_AddsTheNewValuesAndWithdrawsItsOwnOld()
    {
        var emails = AuditLog();
        var rule = Rule(syncRuleId: 1, priority: 1, emails, expression: "Split(\"...\", \";\")");
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var old = Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 5);
        Hold(mvo, emails, "c@corp.example", syncRuleId: 2, systemId: 6);
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>()))
            .Returns(new[] { "a2@corp.example" });

        _engine.FlowInboundAttributes(Cso(mvo, 5, "unused"), rule, ObjectTypes(), expressionEvaluator: evaluator.Object,
            priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { old }));
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EqualTo(new[] { "a2@corp.example" }));
        }
    }

    [Test]
    public void CollapseMergeRetags_AWithdrawnValueAnotherContributorAdds_IsRetaggedInPlaceNotRemovedAndAdded()
    {
        var emails = AuditLog();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var held = Hold(mvo, emails, "b@corp.example", syncRuleId: 1, systemId: 5);
        mvo.PendingAttributeValueRemovals.Add(held);
        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
        {
            Attribute = emails, AttributeId = emails.Id, StringValue = "b@corp.example", ContributedBySyncRuleId = 2, ContributedBySystemId = 6,
            ContributedByKind = ValueContributorKind.SynchronisationRule
        });

        Application.Servers.SyncEngine.CollapseMergeRetags(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "no value change reaches export");
            Assert.That((held.ContributedBySyncRuleId, held.ContributedBySystemId), Is.EqualTo(((int?)2, (int?)6)), "the row now names the contributor still supplying it");
            Assert.That(mvo.HasPendingProvenanceChanges, Is.True);
        }
    }

    [Test]
    public void CollapseMergeRetags_ALaterRuleWithdrawsAValueAnEarlierRuleSupplied_IsRetaggedToTheEarlierRule()
    {
        // Two rules on one Connected System: the aliases rule flows first and finds b held by the emails rule, so adds
        // nothing; the emails rule then drops b. The order the rules flow in must not lose b.
        var emails = AuditLog();
        var emailsRule = Rule(syncRuleId: 1, priority: 1, emails);
        var aliasesRule = Rule(syncRuleId: 2, priority: 2, emails);
        var context = new AttributePriorityContext([emailsRule, aliasesRule]);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var held = Hold(mvo, emails, "b@corp.example", syncRuleId: 1, systemId: 5);
        var cso = Cso(mvo, 5, "b@corp.example");

        _engine.FlowInboundAttributes(cso, aliasesRule, ObjectTypes(), priorityContext: context);
        _engine.FlowInboundAttributes(Cso(mvo, 5), emailsRule, ObjectTypes(), priorityContext: context);
        Application.Servers.SyncEngine.CollapseMergeRetags(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(held.ContributedBySyncRuleId, Is.EqualTo(2));
            Assert.That(mvo.PendingMergeSupplies, Is.Empty, "the supplies are consumed");
        }
    }

    [Test]
    public void CollapseMergeRetags_NoWithdrawal_DropsTheSuppliesAndChangesNothing()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mail = Rule(syncRuleId: 2, priority: 2, emails);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var held = Hold(mvo, emails, "b@corp.example", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 6, "b@corp.example"), mail, ObjectTypes(), priorityContext: new AttributePriorityContext([ad, mail]));
        Application.Servers.SyncEngine.CollapseMergeRetags(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.ContributedBySyncRuleId, Is.EqualTo(1));
            Assert.That(mvo.HasPendingProvenanceChanges, Is.False);
            Assert.That(mvo.PendingMergeSupplies, Is.Empty);
        }
    }

    [Test]
    public void CollapseMergeRetags_PriorityAttribute_IsLeftAlone()
    {
        var dept = new MetaverseAttribute { Id = 102, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var held = Hold(mvo, dept, "b@corp.example", syncRuleId: 1, systemId: 5);
        mvo.PendingAttributeValueRemovals.Add(held);
        mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
        {
            Attribute = dept, AttributeId = dept.Id, StringValue = "b@corp.example", ContributedBySyncRuleId = 2, ContributedBySystemId = 6
        });

        Application.Servers.SyncEngine.CollapseMergeRetags(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Has.Count.EqualTo(1));
            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void MayWithdrawMergeValues_RuleNoLongerSuppliesAValueItHolds_IsTrue()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 5);
        Hold(mvo, emails, "b@corp.example", syncRuleId: 1, systemId: 5);

        Assert.That(Application.Servers.SyncEngine.MayWithdrawMergeValues(Cso(mvo, 5, "a@corp.example"), [ad]), Is.True);
    }

    [Test]
    public void MayWithdrawMergeValues_RuleStillSuppliesEveryValueItHolds_IsFalse()
    {
        var emails = AuditLog();
        var ad = Rule(syncRuleId: 1, priority: 1, emails);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, emails, "a@corp.example", syncRuleId: 1, systemId: 5);
        Hold(mvo, emails, "c@corp.example", syncRuleId: 2, systemId: 6);

        Assert.That(Application.Servers.SyncEngine.MayWithdrawMergeValues(Cso(mvo, 5, "a@corp.example", "z@corp.example"), [ad]), Is.False,
            "a value another rule holds, and a new value, withdraw nothing");
    }

    [Test]
    public void MayWithdrawMergeValues_PriorityAttributeOrNoMetaverseObject_IsFalse()
    {
        var proxies = new MetaverseAttribute { Id = 102, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var rule = Rule(syncRuleId: 1, priority: 1, proxies);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, proxies, "a@corp.example", syncRuleId: 1, systemId: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Application.Servers.SyncEngine.MayWithdrawMergeValues(Cso(mvo, 5), [rule]), Is.False, "only Merge withdrawals are predicted");
            Assert.That(Application.Servers.SyncEngine.MayWithdrawMergeValues(new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 }, [rule]), Is.False);
        }
    }

    [Test]
    public void FlowInboundAttributes_PriorityAttribute_StillReplacesTheSet()
    {
        var dept = new MetaverseAttribute { Id = 102, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var rule = Rule(syncRuleId: 1, priority: 1, dept);
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        Hold(mvo, dept, "old", syncRuleId: 1, systemId: 5);

        _engine.FlowInboundAttributes(Cso(mvo, 5, "new"), rule, ObjectTypes(), priorityContext: new AttributePriorityContext([rule]));

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Is.EqualTo(new[] { "old" }));
    }
}
