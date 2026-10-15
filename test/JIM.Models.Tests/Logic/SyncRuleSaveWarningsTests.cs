// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// Every warning a whole-rule save raised about its Attribute Flows (#1750, #2055 Phase 3), as the portal and the REST
/// API both report them: each said once, in the order the mappings are held.
/// </summary>
[TestFixture]
public class SyncRuleSaveWarningsTests
{
    [Test]
    public void AttributeFlowSaveWarnings_SeveralMappings_GivesEachWarningOnceInMappingOrder()
    {
        var first = new SyncRuleMapping();
        first.SaveWarnings.AddRange(["first", "shared"]);
        var second = new SyncRuleMapping();
        second.SaveWarnings.AddRange(["shared", "second"]);
        var syncRule = new SyncRule { AttributeFlowRules = [first, new SyncRuleMapping(), second] };

        Assert.That(syncRule.AttributeFlowSaveWarnings, Is.EqualTo(new[] { "first", "shared", "second" }));
    }

    [Test]
    public void AttributeFlowSaveWarnings_NoWarnings_IsEmpty()
    {
        Assert.That(new SyncRule { AttributeFlowRules = [new SyncRuleMapping()] }.AttributeFlowSaveWarnings, Is.Empty);
    }

    [Test]
    public void SaveWarnings_FlowsAndNestedScopingCriteria_GivesEveryWarningOnceFlowsFirst()
    {
        // Every warning a save raised, Attribute Flows and Scoping Criteria (#2054) alike: the one list the portal and
        // the REST API report.
        var mapping = new SyncRuleMapping();
        mapping.SaveWarnings.Add("flow");
        var topCriterion = new SyncRuleScopingCriteria();
        topCriterion.SaveWarnings.Add("top");
        var nestedCriterion = new SyncRuleScopingCriteria();
        nestedCriterion.SaveWarnings.AddRange(["nested", "top"]);
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };
        syncRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Criteria = [topCriterion],
            ChildGroups = [new SyncRuleScopingCriteriaGroup { Criteria = [nestedCriterion] }]
        });

        Assert.That(syncRule.SaveWarnings, Is.EqualTo(new[] { "flow", "top", "nested" }));
    }
}
