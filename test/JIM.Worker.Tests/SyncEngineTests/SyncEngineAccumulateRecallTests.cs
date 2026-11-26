// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The engine's own recall paths leave an Accumulate attribute's values in place (#3059): a mapping deleted, and a rule
/// the object has left while another rule still covers it. Priority values on the same object are still recalled, so each
/// test proves the path ran. No database.
/// </summary>
public class SyncEngineAccumulateRecallTests
{
    private const int MvoTypeId = 10;
    private const int SystemId = 5;

    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new Application.Servers.SyncEngine();

    private static MetaverseAttribute AuditLog() => new()
    {
        Id = 100, Name = "Account Audit Log", Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.MultiValued, ContributionMode = AttributeContributionMode.Accumulate
    };

    private static MetaverseAttribute Department() => new() { Id = 101, Name = "department", Type = AttributeDataType.Text };

    private static MetaverseAttribute JobTitle() => new() { Id = 102, Name = "jobTitle", Type = AttributeDataType.Text };

    private static SyncRule ImportRule(int syncRuleId, MetaverseAttribute target, bool scoped = false)
    {
        var mapping = new SyncRuleMapping { Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = 1 };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 200, Order = 1 });
        var rule = new SyncRule
        {
            Id = syncRuleId,
            Name = $"Rule {syncRuleId}",
            ConnectedSystemId = SystemId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect,
            AttributeFlowRules = [mapping]
        };
        if (scoped)
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup());
        return rule;
    }

    private static MetaverseObject Mvo() => new() { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = MvoTypeId } };

    private static ConnectedSystemObject Cso(MetaverseObject mvo) => new()
    {
        Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = SystemId, MetaverseObject = mvo,
        Type = new ConnectedSystemObjectType { Id = 1, RemoveContributedAttributesOnObsoletion = true }
    };

    private static MetaverseObjectAttributeValue Hold(MetaverseObject mvo, MetaverseAttribute attr, string value, int syncRuleId)
    {
        var av = new MetaverseObjectAttributeValue
        {
            Attribute = attr, AttributeId = attr.Id, StringValue = value, ContributedBySyncRuleId = syncRuleId, ContributedBySystemId = SystemId
        };
        mvo.AttributeValues.Add(av);
        return av;
    }

    [Test]
    public void IsRecallable_PriorityValue_IsTrue() =>
        Assert.That(ContributedValueRecall.IsRecallable(new MetaverseObjectAttributeValue { Attribute = Department(), AttributeId = 101 }), Is.True);

    [Test]
    public void IsRecallable_AccumulateValue_IsFalse() =>
        Assert.That(ContributedValueRecall.IsRecallable(new MetaverseObjectAttributeValue { Attribute = AuditLog(), AttributeId = 100 }), Is.False);

    [Test]
    public void IsRecallable_AttributeNotLoaded_ThrowsRatherThanGuessing()
    {
        // Guessing "recallable" would delete values an Accumulate attribute exists to keep; guessing "not recallable"
        // would leave a departed source's values behind. Neither is acceptable, so the caller's object fails instead.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContributedValueRecall.IsRecallable(new MetaverseObjectAttributeValue { AttributeId = 100 }));
        Assert.That(ex!.Message, Does.Contain("100"));
    }

    [Test]
    public void RecallOrphanedContributions_MappingDeleted_KeepsAccumulateValuesAndRecallsTheRest()
    {
        // Rule 1 no longer maps either attribute (its only mapping now targets jobTitle).
        var context = new AttributePriorityContext([ImportRule(1, JobTitle())]);
        var mvo = Mvo();
        var accumulated = Hold(mvo, AuditLog(), "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1);
        var department = Hold(mvo, Department(), "Engineering", syncRuleId: 1);

        var recalled = _engine.RecallOrphanedContributions(Cso(mvo), context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recalled, Is.EqualTo(new[] { department }));
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Not.Contain(accumulated));
        }
    }

    [Test]
    public void WithdrawDepartedRuleContributions_KeepsAccumulateValuesAndWithdrawsTheRest()
    {
        var mvo = Mvo();
        var accumulated = Hold(mvo, AuditLog(), "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1);
        var department = Hold(mvo, Department(), "Engineering", syncRuleId: 1);

        var withdrawn = _engine.WithdrawDepartedRuleContributions(Cso(mvo), [1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withdrawn, Is.EqualTo(new[] { department }));
            Assert.That(mvo.PendingAttributeValueRemovals, Does.Not.Contain(accumulated));
        }
    }

    [Test]
    public void GetDepartedImportRules_RuleHoldingOnlyAccumulateValues_IsNotReportedAsDeparting()
    {
        // A departed rule is reported because it has values to withdraw; one whose values all accumulate has none.
        var departed = ImportRule(1, AuditLog(), scoped: true);
        var mvo = Mvo();
        Hold(mvo, AuditLog(), "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1);

        var result = Application.Servers.SyncEngine.GetDepartedImportRules(Cso(mvo), [departed], []);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetDepartedImportRules_CountsOnlyTheValuesThatWillBeWithdrawn()
    {
        var departed = ImportRule(1, Department(), scoped: true);
        var mvo = Mvo();
        Hold(mvo, AuditLog(), "AD|5f1c|sAMAccountName=jsmith", syncRuleId: 1);
        Hold(mvo, Department(), "Engineering", syncRuleId: 1);

        var result = Application.Servers.SyncEngine.GetDepartedImportRules(Cso(mvo), [departed], []);

        Assert.That(result.Single().ContributedValueCount, Is.EqualTo(1));
    }
}
