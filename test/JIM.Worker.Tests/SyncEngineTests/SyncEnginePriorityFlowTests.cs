// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using Moq;
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

    // ----- Inline incumbent-comparison gate (1b) -----

    private const int MvoTypeId = 10;

    private static MetaverseAttribute DeptAttr() => new() { Id = 100, Name = "department", Type = AttributeDataType.Text };

    /// <summary>An enabled import rule with a single text mapping to <paramref name="target"/> at the given priority.</summary>
    private static SyncRule PriorityRule(int syncRuleId, int priority, MetaverseAttribute target, int csoAttrId = 200, bool nullIsValue = false)
    {
        var mapping = new SyncRuleMapping
        {
            Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = priority, NullIsValue = nullIsValue
        };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = csoAttrId, Order = 1 });
        return new SyncRule
        {
            Id = syncRuleId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [mapping]
        };
    }

    private static ConnectedSystemObject CsoJoinedTo(MetaverseObject mvo, string sourceValue, int connectedSystemId, int csoAttrId = 200)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = connectedSystemId, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = csoAttrId, StringValue = sourceValue });
        return cso;
    }

    /// <summary>A joined CSO that contributes no value for the source attribute (the ConnectedNoValue state).</summary>
    private static ConnectedSystemObject CsoJoinedNoValue(MetaverseObject mvo, int connectedSystemId) =>
        new() { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = connectedSystemId, MetaverseObject = mvo };

    /// <summary>An enabled import rule whose single mapping flows from an expression (evaluated by a mocked evaluator).</summary>
    private static SyncRule ExpressionRule(int syncRuleId, int priority, MetaverseAttribute target, bool nullIsValue = false)
    {
        var mapping = new SyncRuleMapping
        {
            Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = target, Priority = priority, NullIsValue = nullIsValue
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "SomeExpression()", Order = 1 });
        return new SyncRule
        {
            Id = syncRuleId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [mapping]
        };
    }

    /// <summary>Seeds an existing winning value on the MVO row, stamped with its contributing rule/system (the incumbent).</summary>
    private static void SeedIncumbent(MetaverseObject mvo, MetaverseAttribute attr, string value, int syncRuleId, int systemId)
    {
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = attr,
            AttributeId = attr.Id,
            StringValue = value,
            ContributedBySyncRuleId = syncRuleId,
            ContributedBySystemId = systemId
        });
    }

    private static List<ConnectedSystemObjectType> ObjectTypes() => new() { TextCsoType() };

    [Test]
    public void FlowInboundAttributes_LowerPriorityContribution_DoesNotOverwriteHigherPriorityIncumbent()
    {
        // The headline case: a lower-priority system must not clobber a higher-priority system's value.
        var dept = DeptAttr();
        var highRule = PriorityRule(syncRuleId: 1, priority: 1, dept);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "Engineering", syncRuleId: 1, systemId: 9);
        var cso = CsoJoinedTo(mvo, "IT", connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, lowRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "a lower-priority contribution must not be written");
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "a lower-priority contribution must not clear the incumbent");
        Assert.That(mvo.AttributeValues.Single().StringValue, Is.EqualTo("Engineering"));
    }

    [Test]
    public void FlowInboundAttributes_HigherPriorityContribution_OverwritesLowerPriorityIncumbent()
    {
        var dept = DeptAttr();
        var highRule = PriorityRule(syncRuleId: 1, priority: 1, dept);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "IT", syncRuleId: 2, systemId: 9);
        var cso = CsoJoinedTo(mvo, "Engineering", connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, highRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Does.Contain("IT"), "the lower-priority incumbent is replaced");
        var added = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(added.StringValue, Is.EqualTo("Engineering"));
        Assert.That(added.ContributedBySyncRuleId, Is.EqualTo(1), "the new winner's provenance is stamped");
    }

    [Test]
    public void FlowInboundAttributes_EqualPriority_LowerMappingIdIncumbentWins()
    {
        // Duplicate priorities are validation-prevented, but resolution must still be deterministic if they occur:
        // canonical order is (Priority, mapping Id), so the lower mapping id wins, matching AttributePriorityService.Resolve.
        var dept = DeptAttr();
        var ruleA = PriorityRule(syncRuleId: 1, priority: 5, dept);
        var ruleB = PriorityRule(syncRuleId: 2, priority: 5, dept);
        var context = new AttributePriorityContext(new[] { ruleA, ruleB });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "FromA", syncRuleId: 1, systemId: 9);
        var cso = CsoJoinedTo(mvo, "FromB", connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, ruleB, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "equal priority resolves by lower mapping id; incumbent (id 1) keeps the attribute");
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_SameRuleUpdatingItself_OverwritesItsOwnIncumbent()
    {
        // The winner updating itself (its value changed) must still flow, even though it is the incumbent.
        var dept = DeptAttr();
        var highRule = PriorityRule(syncRuleId: 1, priority: 1, dept);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "Engineering", syncRuleId: 1, systemId: 5);
        var cso = CsoJoinedTo(mvo, "Platform", connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, highRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Does.Contain("Engineering"));
        Assert.That(mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("Platform"));
    }

    [Test]
    public void FlowInboundAttributes_SingleContributor_FlowsNormallyEvenWithContext()
    {
        // The single-contributor fast path: with one contributor the gate never engages and the value flows as today.
        var dept = DeptAttr();
        var onlyRule = PriorityRule(syncRuleId: 1, priority: 1, dept);
        var context = new AttributePriorityContext(new[] { onlyRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = CsoJoinedTo(mvo, "Engineering", connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, onlyRule, ObjectTypes(), priorityContext: context);

        var added = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(added.StringValue, Is.EqualTo("Engineering"));
        Assert.That(added.ContributedBySyncRuleId, Is.EqualTo(1));
    }

    // ----- ACT node for no-value winners: assert-null / abstain (1c) -----

    [Test]
    public void FlowInboundAttributes_WinnerConnectedNoValueWithNullIsValue_AssertsNullMarkerOverIncumbent()
    {
        // Higher-priority rule has "Null is a value" and contributes no value: it must overwrite the lower-priority
        // incumbent value with an asserted-null marker (the "clears must propagate" case), not fall through.
        var dept = DeptAttr();
        var highRule = PriorityRule(syncRuleId: 1, priority: 1, dept, nullIsValue: true);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "IT", syncRuleId: 2, systemId: 9);
        var cso = CsoJoinedNoValue(mvo, connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, highRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Does.Contain("IT"), "the lower-priority value is cleared");
        var marker = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(marker.NullValue, Is.True, "an asserted-null marker row is written");
        Assert.That(marker.StringValue, Is.Null);
        Assert.That(marker.ContributedBySyncRuleId, Is.EqualTo(1), "the asserting rule's provenance is stamped");
        Assert.That(marker.ContributedBySystemId, Is.EqualTo(5));
    }

    [Test]
    public void FlowInboundAttributes_WinnerConnectedNoValueWithoutNullIsValue_LeavesLowerPriorityIncumbent()
    {
        // Higher-priority rule contributes no value and does NOT assert null: it abstains, so the lower-priority
        // incumbent value must be left in place (fall-through), not cleared.
        var dept = DeptAttr();
        var highRule = PriorityRule(syncRuleId: 1, priority: 1, dept, nullIsValue: false);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "IT", syncRuleId: 2, systemId: 9);
        var cso = CsoJoinedNoValue(mvo, connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, highRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "an abstaining higher-priority rule must not clear the incumbent");
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(mvo.AttributeValues.Single().StringValue, Is.EqualTo("IT"));
    }

    [Test]
    public void FlowInboundAttributes_SoleContributorNullIsValue_AssertsNullMarker()
    {
        // A sole contributor that asserts null replaces its value with a marker row (asserted empty, observable),
        // not just an absent row.
        var dept = DeptAttr();
        var onlyRule = PriorityRule(syncRuleId: 1, priority: 1, dept, nullIsValue: true);
        var context = new AttributePriorityContext(new[] { onlyRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "X", syncRuleId: 1, systemId: 5);
        var cso = CsoJoinedNoValue(mvo, connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, onlyRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Does.Contain("X"));
        var marker = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(marker.NullValue, Is.True);
        Assert.That(marker.ContributedBySyncRuleId, Is.EqualTo(1));
    }

    [Test]
    public void FlowInboundAttributes_WinnerRetractsItsOwnValueWithoutNullIsValue_Clears()
    {
        // The current winner stops contributing (no value, no NullIsValue) and is its own incumbent: its value is
        // cleared. (Re-electing a lower-priority next contributor is the fallback path, handled later.)
        var dept = DeptAttr();
        var highRule = PriorityRule(syncRuleId: 1, priority: 1, dept, nullIsValue: false);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "X", syncRuleId: 1, systemId: 5);
        var cso = CsoJoinedNoValue(mvo, connectedSystemId: 5);

        _engine.FlowInboundAttributes(cso, highRule, ObjectTypes(), priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Does.Contain("X"));
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "no marker when the rule is not asserting null");
    }

    [Test]
    public void FlowInboundAttributes_ExpressionReturnsNull_WithNullIsValue_AssertsNullMarkerOverIncumbent()
    {
        // Expression null is a positive ConnectedNoValue assertion (not "no opinion"), so a higher-priority rule with
        // "Null is a value" must assert null over a lower-priority incumbent rather than clearing unconditionally.
        var dept = DeptAttr();
        var highRule = ExpressionRule(syncRuleId: 1, priority: 1, dept, nullIsValue: true);
        var lowRule = PriorityRule(syncRuleId: 2, priority: 2, dept);
        var context = new AttributePriorityContext(new[] { highRule, lowRule });

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        SeedIncumbent(mvo, dept, "IT", syncRuleId: 2, systemId: 9);
        var cso = CsoJoinedTo(mvo, "ignored", connectedSystemId: 5);

        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns((object?)null);

        _engine.FlowInboundAttributes(cso, highRule, ObjectTypes(), expressionEvaluator: evaluator.Object, priorityContext: context);

        Assert.That(mvo.PendingAttributeValueRemovals.Select(v => v.StringValue), Does.Contain("IT"));
        var marker = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(marker.NullValue, Is.True);
        Assert.That(marker.ContributedBySyncRuleId, Is.EqualTo(1));
    }
}
