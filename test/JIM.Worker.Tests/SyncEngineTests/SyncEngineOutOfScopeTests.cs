// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.DetermineOutOfScopeAction — no mocking, no database.
/// </summary>
public class SyncEngineOutOfScopeTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void DetermineOutOfScopeAction_NoImportRules_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new() { Direction = SyncRuleDirection.Export, Enabled = true, ConnectedSystemObjectTypeId = 1 }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_ImportRuleWithDisconnect_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_ImportRuleWithRemainJoined_ReturnsRemainJoined()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
    }

    [Test]
    public void DetermineOutOfScopeAction_WrongObjectType_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 99,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_DisabledRule_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = false,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    // #2823: when an object leaves the last import rule covering it, the rules that lost it decide, and Remain Joined
    // wins. "Lost it" means the rules whose values it holds from this system, plus the rule that joined it; with none of
    // those recorded, every rule counts. Each case runs with the rules in both orders.

    [TestCase(false)]
    [TestCase(true)]
    public void DecideLastScopeExit_ContributorsDisagree_RemainJoinedWinsNamingThatRule(bool reverse)
    {
        var (cso, disconnect, remain) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.RemainJoined);
        Contribute(cso, disconnect);
        Contribute(cso, remain);

        var decision = _engine.DecideLastScopeExit(cso, Ordered(reverse, disconnect, remain));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
            Assert.That(decision.DecidingRule, Is.SameAs(remain));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DecideLastScopeExit_RemainJoinedRuleHeldNothing_TheContributorDecides(bool reverse)
    {
        var (cso, disconnect, remain) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.RemainJoined);
        Contribute(cso, disconnect);

        var decision = _engine.DecideLastScopeExit(cso, Ordered(reverse, disconnect, remain));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
            Assert.That(decision.DecidingRule, Is.SameAs(disconnect));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DecideLastScopeExit_TheJoiningRuleCountsWithoutContributing(bool reverse)
    {
        var (cso, disconnect, remain) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.RemainJoined);
        Contribute(cso, disconnect);
        cso.JoinSyncRuleId = remain.Id;

        var decision = _engine.DecideLastScopeExit(cso, Ordered(reverse, disconnect, remain));

        Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DecideLastScopeExit_NothingRecorded_EveryRuleCounts(bool reverse)
    {
        var (cso, disconnect, remain) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.RemainJoined);

        var decision = _engine.DecideLastScopeExit(cso, Ordered(reverse, disconnect, remain));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
            Assert.That(decision.DecidingRule, Is.SameAs(remain));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DecideLastScopeExit_AnotherSystemsValueStampedWithTheRule_DoesNotCount(bool reverse)
    {
        var (cso, disconnect, remain) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.RemainJoined);
        Contribute(cso, disconnect);
        Contribute(cso, remain, systemId: cso.ConnectedSystemId + 1);

        var decision = _engine.DecideLastScopeExit(cso, Ordered(reverse, disconnect, remain));

        Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DecideLastScopeExit_BothDisconnect_NamesTheFirstByName(bool reverse)
    {
        var (cso, first, second) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.Disconnect);
        Contribute(cso, first);
        Contribute(cso, second);

        var decision = _engine.DecideLastScopeExit(cso, Ordered(reverse, first, second));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
            Assert.That(decision.DecidingRule, Is.SameAs(first), "rule A sorts before rule B");
        }
    }

    [Test]
    public void DecideLastScopeExit_NoRules_DisconnectsNamingNoRule()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 7 };

        var decision = _engine.DecideLastScopeExit(cso, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
            Assert.That(decision.DecidingRule, Is.Null);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DetermineOutOfScopeAction_TwoImportRulesNothingRecorded_RemainJoinedWins(bool reverse)
    {
        var (cso, disconnect, remain) = Arrange(InboundOutOfScopeAction.Disconnect, InboundOutOfScopeAction.RemainJoined);

        var action = _engine.DetermineOutOfScopeAction(cso, Ordered(reverse, disconnect, remain));

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
    }

    private static (ConnectedSystemObject Cso, SyncRule RuleA, SyncRule RuleB) Arrange(InboundOutOfScopeAction actionA, InboundOutOfScopeAction actionB)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            TypeId = 1,
            ConnectedSystemId = 7,
            MetaverseObject = new MetaverseObject { Id = Guid.NewGuid() }
        };
        SyncRule Rule(int id, string name, InboundOutOfScopeAction action) => new()
        {
            Id = id,
            Name = name,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = 7,
            ConnectedSystemObjectTypeId = 1,
            InboundOutOfScopeAction = action
        };
        return (cso, Rule(101, "Rule A", actionA), Rule(102, "Rule B", actionB));
    }

    private static void Contribute(ConnectedSystemObject cso, SyncRule rule, int? systemId = null) =>
        cso.MetaverseObject!.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            StringValue = rule.Name,
            ContributedBySyncRuleId = rule.Id,
            ContributedBySystemId = systemId ?? cso.ConnectedSystemId
        });

    private static List<SyncRule> Ordered(bool reverse, SyncRule a, SyncRule b) => reverse ? [b, a] : [a, b];
}
