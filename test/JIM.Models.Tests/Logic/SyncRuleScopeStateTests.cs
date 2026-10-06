// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// Which saves of an export Synchronisation Rule can move objects into or out of its scope (#1925). Each one that can
/// flags every Metaverse Object of the rule's type for review at the next synchronisation, so a save that cannot must
/// not, and a save that can must never be missed.
/// </summary>
[TestFixture]
public class SyncRuleScopeStateTests
{
    private const int TitleAttributeId = 7;

    [Test]
    public void ExportScopeMovedBy_NothingChanged_ReturnsFalse()
    {
        var stored = StateOf(Rule(scoped: true));

        Assert.That(stored.ExportScopeMovedBy(Rule(scoped: true)), Is.False);
    }

    [Test]
    public void ExportScopeMovedBy_RuleReEnabled_ReturnsTrue()
    {
        var stored = StateOf(Rule(enabled: false));

        Assert.That(stored.ExportScopeMovedBy(Rule()), Is.True);
    }

    [Test]
    public void ExportScopeMovedBy_RuleDisabled_ReturnsFalse()
    {
        var stored = StateOf(Rule());

        Assert.That(stored.ExportScopeMovedBy(Rule(enabled: false)), Is.False, "disabling destroys nothing, so reviews nothing");
    }

    [Test]
    public void ExportScopeMovedBy_ProvisioningSwitchedOn_ReturnsTrue()
    {
        var stored = StateOf(Rule());

        Assert.That(stored.ExportScopeMovedBy(Rule(provisioning: true)), Is.True);
    }

    [Test]
    public void ExportScopeMovedBy_ProvisioningSwitchedOff_ReturnsFalse()
    {
        var stored = StateOf(Rule(provisioning: true));

        Assert.That(stored.ExportScopeMovedBy(Rule()), Is.False);
    }

    [Test]
    public void ExportScopeMovedBy_CriteriaAdded_ReturnsTrue()
    {
        var stored = StateOf(Rule());

        Assert.That(stored.ExportScopeMovedBy(Rule(scoped: true)), Is.True);
    }

    [Test]
    public void ExportScopeMovedBy_CriteriaRemoved_ReturnsTrue()
    {
        var stored = StateOf(Rule(scoped: true));

        Assert.That(stored.ExportScopeMovedBy(Rule()), Is.True);
    }

    [Test]
    public void ExportScopeMovedBy_EmptyGroupAddedToAnUnscopedRule_ReturnsFalse()
    {
        // The scripted way to scope a rule creates the group first and its criteria after, one save each. A group
        // with no criteria matches every object, exactly as no group does, so that first save moves nobody.
        var stored = StateOf(Rule());
        var saved = Rule();
        saved.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All });

        Assert.That(stored.ExportScopeMovedBy(saved), Is.False);
    }

    [TestCase(OutboundDeprovisionAction.Delete, OutboundDeprovisionAction.Disconnect)]
    [TestCase(OutboundDeprovisionAction.Disconnect, OutboundDeprovisionAction.Delete)]
    public void DeprovisioningActionChangedBy_ActionChanged_ReturnsTrue(OutboundDeprovisionAction stored, OutboundDeprovisionAction saved)
    {
        // Objects already out of scope are only deprovisioned when something reviews them, so the new action reaches
        // a Delete queued under the old one only through a review (#1970).
        var state = StateOf(Rule(deprovisionAction: stored));

        Assert.That(state.DeprovisioningActionChangedBy(Rule(deprovisionAction: saved)), Is.True);
    }

    [Test]
    public void DeprovisioningActionChangedBy_ActionUnchanged_ReturnsFalse()
    {
        var state = StateOf(Rule(deprovisionAction: OutboundDeprovisionAction.Delete));

        Assert.That(state.DeprovisioningActionChangedBy(Rule(deprovisionAction: OutboundDeprovisionAction.Delete)), Is.False);
    }

    [Test]
    public void DeprovisioningActionChangedBy_RuleDisabledByTheSameSave_ReturnsFalse()
    {
        // A disabled rule deprovisions nothing, so there is nothing for the new action to reach until it is re-enabled,
        // which is a review of its own.
        var state = StateOf(Rule(deprovisionAction: OutboundDeprovisionAction.Delete));

        Assert.That(state.DeprovisioningActionChangedBy(Rule(enabled: false, deprovisionAction: OutboundDeprovisionAction.Disconnect)), Is.False);
    }

    private static SyncRuleScopeState StateOf(SyncRule rule) =>
        new(rule.Enabled, rule.ProvisionToConnectedSystem == true, SyncRuleScopingProposal.FromCurrentScope(rule), rule.OutboundDeprovisionAction);

    private static SyncRule Rule(bool enabled = true, bool provisioning = false, bool scoped = false,
        OutboundDeprovisionAction deprovisionAction = OutboundDeprovisionAction.Disconnect)
    {
        var rule = new SyncRule
        {
            Direction = SyncRuleDirection.Export, Enabled = enabled, ProvisionToConnectedSystem = provisioning, OutboundDeprovisionAction = deprovisionAction
        };
        if (scoped)
        {
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = [new SyncRuleScopingCriteria { MetaverseAttributeId = TitleAttributeId, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee" }]
            });
        }

        return rule;
    }
}
