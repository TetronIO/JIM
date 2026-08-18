// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The export matching rule-selection verdict, extracted from <c>AttemptExportMatchingAsync</c> into the pure
/// engine as the final slice of the #288 Phase 1 unbraiding (plan item 1e). The verdict answers "which Object
/// Matching Rules, in what order, should export matching try for this export Synchronisation Rule?": the
/// Connected System's matching mode chooses the rule source (Connected System mode reads the Connected System
/// Object Type's shared rules; Advanced mode reads the Synchronisation Rule's own), rules are tried in their
/// configured order, and an empty answer means matching is not attempted at all. The per-rule candidate query
/// stays with the orchestrator, where the data access is. These pin the extracted verdict to the braided
/// implementation's behaviour.
/// </summary>
[TestFixture]
public class SyncEngineExportMatchingTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void SelectExportMatchingRules_ConnectedSystemMode_ReturnsTheObjectTypesSharedRules()
    {
        var typeRule = MatchingRule(order: 1);
        var rule = ExportRule(ObjectMatchingRuleMode.ConnectedSystem, objectTypeRules: [typeRule], syncRuleRules: [MatchingRule(order: 2)]);

        var selected = _engine.SelectExportMatchingRules(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0], Is.SameAs(typeRule));
        }
    }

    [Test]
    public void SelectExportMatchingRules_AdvancedMode_ReturnsTheSynchronisationRulesOwnRules()
    {
        var ownRule = MatchingRule(order: 1);
        var rule = ExportRule(ObjectMatchingRuleMode.SyncRule, objectTypeRules: [MatchingRule(order: 2)], syncRuleRules: [ownRule]);

        var selected = _engine.SelectExportMatchingRules(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0], Is.SameAs(ownRule));
        }
    }

    [Test]
    public void SelectExportMatchingRules_Rules_AreOrderedByTheirConfiguredOrder()
    {
        var second = MatchingRule(order: 2);
        var first = MatchingRule(order: 1);
        var rule = ExportRule(ObjectMatchingRuleMode.ConnectedSystem, objectTypeRules: [second, first], syncRuleRules: []);

        var selected = _engine.SelectExportMatchingRules(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Has.Count.EqualTo(2));
            Assert.That(selected[0], Is.SameAs(first));
            Assert.That(selected[1], Is.SameAs(second));
        }
    }

    [Test]
    public void SelectExportMatchingRules_NoRulesConfigured_ReturnsEmptySoMatchingIsNotAttempted()
    {
        var rule = ExportRule(ObjectMatchingRuleMode.ConnectedSystem, objectTypeRules: [], syncRuleRules: []);

        Assert.That(_engine.SelectExportMatchingRules(rule), Is.Empty);
    }

    [Test]
    public void SelectExportMatchingRules_MissingConnectedSystemNavigation_ReturnsEmpty()
    {
        // The braided guard: without the Connected System loaded, the matching mode cannot be read, and
        // matching quietly does not run (provisioning proceeds as though no match existed). The navigation is
        // declared non-nullable (= null! default) but is null-checked throughout the sync path.
        var rule = ExportRule(ObjectMatchingRuleMode.ConnectedSystem, objectTypeRules: [MatchingRule(1)], syncRuleRules: []);
        rule.ConnectedSystem = null!;

        Assert.That(_engine.SelectExportMatchingRules(rule), Is.Empty);
    }

    [Test]
    public void SelectExportMatchingRules_MissingObjectTypeNavigation_ReturnsEmpty()
    {
        var rule = ExportRule(ObjectMatchingRuleMode.ConnectedSystem, objectTypeRules: [MatchingRule(1)], syncRuleRules: []);
        rule.ConnectedSystemObjectType = null!;

        Assert.That(_engine.SelectExportMatchingRules(rule), Is.Empty);
    }

    private static ObjectMatchingRule MatchingRule(int order) => new() { Id = order, Order = order };

    private static SyncRule ExportRule(
        ObjectMatchingRuleMode mode, List<ObjectMatchingRule> objectTypeRules, List<ObjectMatchingRule> syncRuleRules)
    {
        var rule = new SyncRule
        {
            Name = "Export rule",
            ConnectedSystemId = 1,
            ConnectedSystemObjectTypeId = 5,
            MetaverseObjectTypeId = 100,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystem = new ConnectedSystem { Id = 1, Name = "Target", ObjectMatchingRuleMode = mode },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 5, Name = "user", ObjectMatchingRules = objectTypeRules },
            ObjectMatchingRules = syncRuleRules
        };
        return rule;
    }
}
