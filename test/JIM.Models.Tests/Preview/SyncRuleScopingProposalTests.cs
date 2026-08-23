// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The proposed Scoping Criteria an administrator is about to save, as the G1 preview adapter receives them (#1436).
/// </summary>
[TestFixture]
public class SyncRuleScopingProposalTests
{
    [Test]
    public void FromCurrentScope_RuleWithNestedGroups_CapturesTheWholeTree()
    {
        var rule = BuildImportRuleWithNestedScope();

        var proposal = SyncRuleScopingProposal.FromCurrentScope(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.CriteriaGroups, Has.Count.EqualTo(1));
            Assert.That(proposal.CriteriaGroups[0].Type, Is.EqualTo(SearchGroupType.All));
            Assert.That(proposal.CriteriaGroups[0].Criteria, Has.Count.EqualTo(1));
            Assert.That(proposal.CriteriaGroups[0].Criteria[0].ConnectedSystemAttributeId, Is.EqualTo(7));
            Assert.That(proposal.CriteriaGroups[0].Criteria[0].StringValue, Is.EqualTo("Sales"));
            Assert.That(proposal.CriteriaGroups[0].ChildGroups, Has.Count.EqualTo(1));
            Assert.That(proposal.CriteriaGroups[0].ChildGroups[0].Type, Is.EqualTo(SearchGroupType.Any));
            Assert.That(proposal.CriteriaGroups[0].ChildGroups[0].Criteria, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void FromCurrentScope_RuleWithNoCriteria_IsUnscoped()
    {
        var rule = new SyncRule { Direction = SyncRuleDirection.Import };

        var proposal = SyncRuleScopingProposal.FromCurrentScope(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.CriteriaGroups, Is.Empty);
            Assert.That(proposal.IsUnscoped, Is.True,
                "a rule with no Scoping Criteria is in scope for every object of its type");
        }
    }

    [Test]
    public void IsUnscoped_GroupCarryingNoCriteriaAtAll_IsTrue()
    {
        // The evaluator treats an empty group as matching everything, so a proposal whose only group is empty
        // scopes nothing out. Reporting it as scoped would let the adapter offer a preview of a change that
        // cannot narrow or widen anything.
        var proposal = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, [], [])]);

        Assert.That(proposal.IsUnscoped, Is.True);
    }

    [Test]
    public void DescribesSameScopeAs_SameTreeRebuilt_IsTrue()
    {
        // The editor rebuilds its proposal on every render, so reference equality would report a change that
        // never happened and mark every preview stale the moment it finished.
        var first = SyncRuleScopingProposal.FromCurrentScope(BuildImportRuleWithNestedScope());
        var second = SyncRuleScopingProposal.FromCurrentScope(BuildImportRuleWithNestedScope());

        Assert.That(first.DescribesSameScopeAs(second), Is.True);
    }

    [Test]
    public void DescribesSameScopeAs_CriteriaReorderedWithinAGroup_IsTrue()
    {
        // Criteria in a group are combined with All or Any, neither of which depends on order, so dragging one
        // above another is not a configuration change and must not invalidate a preview.
        var department = Criterion(7, "Sales");
        var country = Criterion(8, "UK");
        var first = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, [department, country], [])]);
        var second = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, [country, department], [])]);

        Assert.That(first.DescribesSameScopeAs(second), Is.True);
    }

    [Test]
    public void DescribesSameScopeAs_ValueChanged_IsFalse()
    {
        var first = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, [Criterion(7, "Sales")], [])]);
        var second = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, [Criterion(7, "Marketing")], [])]);

        Assert.That(first.DescribesSameScopeAs(second), Is.False);
    }

    [Test]
    public void DescribesSameScopeAs_GroupTypeChanged_IsFalse()
    {
        // All to Any is the widest change the editor can make without touching a single criterion.
        var criteria = new[] { Criterion(7, "Sales"), Criterion(8, "UK") };
        var first = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, criteria, [])]);
        var second = new SyncRuleScopingProposal([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.Any, criteria, [])]);

        Assert.That(first.DescribesSameScopeAs(second), Is.False);
    }

    [Test]
    public void DescribesSameScopeAs_Null_IsFalse()
    {
        var proposal = SyncRuleScopingProposal.FromCurrentScope(BuildImportRuleWithNestedScope());

        Assert.That(proposal.DescribesSameScopeAs(null), Is.False);
    }

    [Test]
    public void SyncRuleScopingProposal_SerialisedAndBack_SurvivesTheQueue()
    {
        // A proposal is queued for JIM.Worker as JSON, so anything it cannot round trip is silently lost between
        // the administrator pressing the button and the adapter evaluating. The entity graph cannot be sent at all
        // (ParentGroup and ChildGroups form a cycle), which is why this DTO exists.
        var original = SyncRuleScopingProposal.FromCurrentScope(BuildImportRuleWithNestedScope());

        var restored = JsonSerializer.Deserialize<SyncRuleScopingProposal>(JsonSerializer.Serialize(original));

        Assert.That(restored, Is.Not.Null);
        Assert.That(original.DescribesSameScopeAs(restored), Is.True);
    }

    // ── The unsaved shape the portal editor builds ───────────────────────────────────────────────────────────

    [Test]
    public void FromCurrentScope_CriterionCarriesOnlyItsAttributeNavigation_StillReadsTheAttributeId()
    {
        // The shape the Scope editor builds when an administrator ADDS a criterion: the navigation is set and the
        // foreign key stays unassigned until the rule is saved. Reading the key alone made a criterion the editor
        // plainly shows read as naming no attribute, which the preview reports as a blocking finding.
        var department = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "department", Type = AttributeDataType.Text };
        var rule = new SyncRule { Id = 1, Name = "HR Import", Direction = SyncRuleDirection.Import };
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = department,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Sales"
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var proposal = SyncRuleScopingProposal.FromCurrentScope(rule);

        Assert.That(proposal.CriteriaGroups[0].Criteria[0].ConnectedSystemAttributeId, Is.EqualTo(department.Id));
    }

    [Test]
    public void FromCurrentScope_ExportCriterionCarriesOnlyItsAttributeNavigation_StillReadsTheAttributeId()
    {
        var department = new MetaverseAttribute { Id = 201, Name = "Department", Type = AttributeDataType.Text };
        var rule = new SyncRule { Id = 2, Name = "Directory Export", Direction = SyncRuleDirection.Export };
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = department,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Sales"
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var proposal = SyncRuleScopingProposal.FromCurrentScope(rule);

        Assert.That(proposal.CriteriaGroups[0].Criteria[0].MetaverseAttributeId, Is.EqualTo(department.Id));
    }

    #region helpers

    private static SyncRuleScopingCriterionProposal Criterion(int connectedSystemAttributeId, string value) =>
        new(null, connectedSystemAttributeId, SearchComparisonType.Equals, StringValue: value);

    /// <summary>
    /// An import rule scoped to department == Sales, with a nested Any group over two country values.
    /// </summary>
    private static SyncRule BuildImportRuleWithNestedScope()
    {
        var departmentAttribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "department", Type = AttributeDataType.Text };
        var countryAttribute = new ConnectedSystemObjectTypeAttribute { Id = 8, Name = "country", Type = AttributeDataType.Text };

        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = departmentAttribute,
            ConnectedSystemAttributeId = departmentAttribute.Id,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Sales"
        });
        group.ChildGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.Any,
            Criteria =
            {
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = countryAttribute,
                    ConnectedSystemAttributeId = countryAttribute.Id,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "UK"
                }
            }
        });

        var rule = new SyncRule { Direction = SyncRuleDirection.Import };
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    #endregion
}
