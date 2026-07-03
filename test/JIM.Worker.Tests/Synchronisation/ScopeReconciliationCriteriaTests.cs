// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Application.Servers;
using JIM.Models.Logic;
using JIM.Models.Search;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for ScopeReconciliationServer.EnumerateRelativeDateCriteria (issue #892): the pure traversal that
/// selects the relative-date criteria a rule contributes to the Temporal Scope Reconciler, resolving the right
/// attribute ID for the rule's direction and skipping absolute or incomplete criteria.
/// </summary>
[TestFixture]
public class ScopeReconciliationCriteriaTests
{
    private static SyncRuleScopingCriteria RelativeCriterion(int? csAttrId, int? mvAttrId, int count = 0, RelativeDateUnit unit = RelativeDateUnit.Hours, RelativeDateDirection direction = RelativeDateDirection.FromNow)
    {
        return new SyncRuleScopingCriteria
        {
            ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = count,
            RelativeUnit = unit,
            RelativeDirection = direction,
            ConnectedSystemAttributeId = csAttrId,
            MetaverseAttributeId = mvAttrId
        };
    }

    private static SyncRule RuleWith(SyncRuleDirection direction, SyncRuleScopingCriteriaGroup group)
    {
        var rule = new SyncRule { Direction = direction };
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    [Test]
    public void EnumerateRelativeDateCriteria_ImportRule_ResolvesConnectedSystemAttributeId()
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(RelativeCriterion(csAttrId: 42, mvAttrId: null, count: 7, unit: RelativeDateUnit.Days, direction: RelativeDateDirection.Ago));
        var rule = RuleWith(SyncRuleDirection.Import, group);

        var result = ScopeReconciliationServer.EnumerateRelativeDateCriteria(rule).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo((42, 7, RelativeDateUnit.Days, RelativeDateDirection.Ago)));
    }

    [Test]
    public void EnumerateRelativeDateCriteria_ExportRule_ResolvesMetaverseAttributeId()
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(RelativeCriterion(csAttrId: null, mvAttrId: 99, count: 24, unit: RelativeDateUnit.Hours, direction: RelativeDateDirection.FromNow));
        var rule = RuleWith(SyncRuleDirection.Export, group);

        var result = ScopeReconciliationServer.EnumerateRelativeDateCriteria(rule).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo((99, 24, RelativeDateUnit.Hours, RelativeDateDirection.FromNow)));
    }

    [Test]
    public void EnumerateRelativeDateCriteria_SkipsAbsoluteAndIncompleteCriteria()
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        // Absolute-mode date criterion: does not drift with the clock.
        group.Criteria.Add(new SyncRuleScopingCriteria { ValueMode = DateCriteriaValueMode.Absolute, ConnectedSystemAttributeId = 1, DateTimeValue = new System.DateTime(2030, 1, 1, 0, 0, 0, System.DateTimeKind.Utc) });
        // Relative but incomplete (no unit): cannot resolve a window.
        group.Criteria.Add(new SyncRuleScopingCriteria { ValueMode = DateCriteriaValueMode.Relative, RelativeCount = 5, RelativeUnit = null, RelativeDirection = RelativeDateDirection.Ago, ConnectedSystemAttributeId = 2 });
        // Import rule but the relative criterion is missing the Connected System attribute id.
        group.Criteria.Add(RelativeCriterion(csAttrId: null, mvAttrId: 3));
        var rule = RuleWith(SyncRuleDirection.Import, group);

        var result = ScopeReconciliationServer.EnumerateRelativeDateCriteria(rule).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void EnumerateRelativeDateCriteria_TraversesNestedChildGroups()
    {
        var child = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.Any };
        child.Criteria.Add(RelativeCriterion(csAttrId: 7, mvAttrId: null, count: 0, unit: RelativeDateUnit.Hours, direction: RelativeDateDirection.FromNow));

        var root = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        root.Criteria.Add(RelativeCriterion(csAttrId: 5, mvAttrId: null, count: 30, unit: RelativeDateUnit.Days, direction: RelativeDateDirection.Ago));
        root.ChildGroups.Add(child);
        var rule = RuleWith(SyncRuleDirection.Import, root);

        var result = ScopeReconciliationServer.EnumerateRelativeDateCriteria(rule).Select(r => r.AttributeId).ToList();

        Assert.That(result, Is.EquivalentTo(new[] { 5, 7 }));
    }
}
