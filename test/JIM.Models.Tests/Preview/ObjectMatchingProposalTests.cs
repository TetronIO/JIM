// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The proposal an Object Matching preview is asked about (#1457): the matching rules an administrator has in the
/// editor, not the ones the database holds.
///
/// Two properties carry the weight. The proposal must read an unsaved rule the editor has just built, where the
/// foreign key is still unassigned and only the navigation property names the attribute (#1450); reading the key
/// alone reports a rule the administrator can plainly see as naming nothing. And comparison must be
/// order-SENSITIVE, unlike Scoping Criteria: matching rules are evaluated in ascending order until one matches, so
/// dragging the second rule above the first changes which Metaverse Object an account joins to.
/// </summary>
[TestFixture]
public class ObjectMatchingProposalTests
{
    private static ConnectedSystemObjectTypeAttribute EmployeeIdAttribute => new()
        { Id = 101, Name = "employeeID", Type = AttributeDataType.Text };

    private static MetaverseAttribute EmployeeIdMetaverseAttribute => new()
        { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };

    private static ObjectMatchingRule SavedRule(int order = 0, bool caseSensitive = false) => new()
    {
        Id = 1,
        Order = order,
        ConnectedSystemObjectTypeId = 9,
        MetaverseObjectTypeId = 3,
        TargetMetaverseAttributeId = 201,
        CaseSensitive = caseSensitive,
        Sources = [new ObjectMatchingRuleSource { Id = 1, Order = 0, ConnectedSystemAttributeId = 101 }]
    };

    [Test]
    public void FromCurrentConfiguration_SimpleModeRules_AreReadFromTheObjectType()
    {
        var objectType = new ConnectedSystemObjectType { Id = 9, Name = "User", ObjectMatchingRules = [SavedRule()] };
        var connectedSystem = new ConnectedSystem { Id = 5, Name = "HR", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };

        var proposal = ObjectMatchingProposal.FromCurrentConfiguration(connectedSystem, [objectType], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
            Assert.That(proposal.Rules, Has.Count.EqualTo(1));
            Assert.That(proposal.Rules[0].ConnectedSystemObjectTypeId, Is.EqualTo(9));
            Assert.That(proposal.Rules[0].TargetMetaverseAttributeId, Is.EqualTo(201));
            Assert.That(proposal.Rules[0].Sources[0].ConnectedSystemAttributeId, Is.EqualTo(101));
        }
    }

    [Test]
    public void FromCurrentConfiguration_AdvancedModeRules_AreReadFromTheSynchronisationRules()
    {
        var rule = SavedRule();
        rule.ConnectedSystemObjectTypeId = null;
        rule.SyncRuleId = 42;
        var syncRule = new SyncRule { Id = 42, Name = "HR Import", ObjectMatchingRules = [rule] };
        var connectedSystem = new ConnectedSystem { Id = 5, Name = "HR", ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };

        var proposal = ObjectMatchingProposal.FromCurrentConfiguration(connectedSystem, [], [syncRule]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
            Assert.That(proposal.Rules, Has.Count.EqualTo(1));
            Assert.That(proposal.Rules[0].SyncRuleId, Is.EqualTo(42));
            Assert.That(proposal.Rules[0].ConnectedSystemObjectTypeId, Is.Null);
        }
    }

    [Test]
    public void FromRule_UnsavedRule_ReadsIdsFromNavigationPropertiesWhenForeignKeysAreUnassigned()
    {
        // What the editor hands over the moment an administrator adds a rule: the entities are attached, the keys
        // are still zero because nothing has been saved (#1450).
        var unsaved = new ObjectMatchingRule
        {
            Order = 0,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 9, Name = "User" },
            MetaverseObjectType = new MetaverseObjectType { Id = 3, Name = "Person" },
            TargetMetaverseAttribute = EmployeeIdMetaverseAttribute,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = EmployeeIdAttribute }]
        };

        var proposal = ObjectMatchingRuleProposal.FromRule(unsaved);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.ConnectedSystemObjectTypeId, Is.EqualTo(9));
            Assert.That(proposal.MetaverseObjectTypeId, Is.EqualTo(3));
            Assert.That(proposal.TargetMetaverseAttributeId, Is.EqualTo(201));
            Assert.That(proposal.Sources[0].ConnectedSystemAttributeId, Is.EqualTo(101));
        }
    }

    [Test]
    public void DescribesSameMatchingAs_IdenticalRulesRebuilt_ReportsNoChange()
    {
        var objectType = new ConnectedSystemObjectType { Id = 9, Name = "User", ObjectMatchingRules = [SavedRule()] };
        var connectedSystem = new ConnectedSystem { Id = 5, Name = "HR", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem };

        var stored = ObjectMatchingProposal.FromCurrentConfiguration(connectedSystem, [objectType], []);
        var rebuilt = ObjectMatchingProposal.FromCurrentConfiguration(connectedSystem, [objectType], []);

        Assert.That(stored.DescribesSameMatchingAs(rebuilt), Is.True);
    }

    [Test]
    public void DescribesSameMatchingAs_RulesReordered_ReportsAChange()
    {
        // Order decides which rule wins, so reordering is a real change; this is where matching parts company with
        // Scoping Criteria, whose groups combine order-insensitively.
        var first = SavedRule(order: 0);
        var second = SavedRule(order: 1);
        second.TargetMetaverseAttributeId = 202;

        var stored = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem,
            [ObjectMatchingRuleProposal.FromRule(first), ObjectMatchingRuleProposal.FromRule(second)]);

        first.Order = 1;
        second.Order = 0;
        var reordered = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem,
            [ObjectMatchingRuleProposal.FromRule(first), ObjectMatchingRuleProposal.FromRule(second)]);

        Assert.That(stored.DescribesSameMatchingAs(reordered), Is.False);
    }

    [Test]
    public void DescribesSameMatchingAs_CaseSensitivityFlipped_ReportsAChange()
    {
        var stored = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem,
            [ObjectMatchingRuleProposal.FromRule(SavedRule(caseSensitive: false))]);
        var proposed = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem,
            [ObjectMatchingRuleProposal.FromRule(SavedRule(caseSensitive: true))]);

        Assert.That(stored.DescribesSameMatchingAs(proposed), Is.False);
    }

    [Test]
    public void DescribesSameMatchingAs_ModeSwitchedWithTheSameRules_ReportsAChange()
    {
        // The Simple to Advanced switch changes which rules apply even when the rule bodies are untouched.
        var rules = new List<ObjectMatchingRuleProposal> { ObjectMatchingRuleProposal.FromRule(SavedRule()) };

        var stored = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem, rules);
        var proposed = new ObjectMatchingProposal(ObjectMatchingRuleMode.SyncRule, rules);

        Assert.That(stored.DescribesSameMatchingAs(proposed), Is.False);
    }

    [Test]
    public void DescribesSameMatchingAs_Null_ReportsAChange()
    {
        var stored = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem,
            [ObjectMatchingRuleProposal.FromRule(SavedRule())]);

        Assert.That(stored.DescribesSameMatchingAs(null), Is.False);
    }

    [Test]
    public void RulesFor_SimpleMode_SelectsRulesByObjectTypeAndOrdersThem()
    {
        var second = ObjectMatchingRuleProposal.FromRule(SavedRule(order: 1));
        var first = ObjectMatchingRuleProposal.FromRule(SavedRule(order: 0));
        var otherType = ObjectMatchingRuleProposal.FromRule(SavedRule(order: 0)) with { ConnectedSystemObjectTypeId = 99 };

        var proposal = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem, [second, first, otherType]);

        var selected = proposal.RulesFor(connectedSystemObjectTypeId: 9, syncRuleId: null).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Has.Count.EqualTo(2));
            Assert.That(selected[0].Order, Is.EqualTo(0));
            Assert.That(selected[1].Order, Is.EqualTo(1));
        }
    }

    [Test]
    public void RulesFor_AdvancedMode_SelectsRulesBySynchronisationRule()
    {
        var mine = ObjectMatchingRuleProposal.FromRule(SavedRule()) with { ConnectedSystemObjectTypeId = null, SyncRuleId = 42 };
        var theirs = ObjectMatchingRuleProposal.FromRule(SavedRule()) with { ConnectedSystemObjectTypeId = null, SyncRuleId = 43 };

        var proposal = new ObjectMatchingProposal(ObjectMatchingRuleMode.SyncRule, [mine, theirs]);

        var selected = proposal.RulesFor(connectedSystemObjectTypeId: 9, syncRuleId: 42).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0].SyncRuleId, Is.EqualTo(42));
        }
    }
}
