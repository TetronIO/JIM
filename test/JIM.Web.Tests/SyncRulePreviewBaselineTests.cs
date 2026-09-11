// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Synchronisation Rule editor offers a preview only where the administrator has edited what that preview
/// answers. A preview evaluates the form against the stored rule, so where the two agree the only answer it can give
/// is that nothing changes, and a row of four buttons that all say so is noise beside the save button. The baseline
/// is what "edited" is measured from: the rule as the editor loaded it, or as it last saved it.
/// </summary>
[TestFixture]
public class SyncRulePreviewBaselineTests
{
    private static SyncRule ExportRule()
    {
        var rule = new SyncRule
        {
            Id = 7,
            Name = "Cross-Domain Export Users",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ProvisionToConnectedSystem = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
        };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    MetaverseAttributeId = 3,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Active"
                }
            ]
        });
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttributeId = 11 };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = 5 });
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    private static void AssertOffers(SyncRulePreviewBaseline baseline, SyncRule rule,
        bool destructiveToggles = false, bool scope = false, bool attributeFlow = false, bool behaviour = false)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(baseline.DestructiveTogglesEdited(rule), Is.EqualTo(destructiveToggles), "Deprovisioning Impact");
            Assert.That(baseline.ScopeEdited(rule), Is.EqualTo(scope), "Scope Impact");
            Assert.That(baseline.AttributeFlowEdited(rule), Is.EqualTo(attributeFlow), "Attribute Flow Impact");
            Assert.That(baseline.BehaviourEdited(rule), Is.EqualTo(behaviour), "Behaviour Impact");
        }
    }

    [Test]
    public void Capture_UneditedRule_OffersNoPreview()
    {
        var rule = ExportRule();

        var baseline = SyncRulePreviewBaseline.Capture(rule);

        AssertOffers(baseline, rule);
    }

    [Test]
    public void DestructiveTogglesEdited_DeprovisioningActionChanged_OffersThatPreviewAlone()
    {
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);

        rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;

        AssertOffers(baseline, rule, destructiveToggles: true);
    }

    [Test]
    public void DestructiveTogglesEdited_EditReverted_OffersNothingAgain()
    {
        // The buttons follow the form, not the history of the sitting: an edit put back is no edit.
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);
        rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;

        rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        AssertOffers(baseline, rule);
    }

    [Test]
    public void ScopeEdited_UnsavedCriterionAdded_OffersThatPreviewAlone()
    {
        // The Scope editor adds a criterion with its navigation set and the foreign key unassigned until the rule
        // is saved (#1450); an edit of that shape must still count as one.
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);

        rule.ObjectScopingCriteriaGroups[0].Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = new MetaverseAttribute { Id = 9, Name = "Department" },
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });

        AssertOffers(baseline, rule, scope: true);
    }

    [Test]
    public void AttributeFlowEdited_MappingDisabled_OffersThatPreviewAlone()
    {
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);

        rule.AttributeFlowRules[0].Enabled = false;

        AssertOffers(baseline, rule, attributeFlow: true);
    }

    [Test]
    public void AttributeFlowEdited_MappingRemoved_OffersThatPreviewAlone()
    {
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);

        rule.AttributeFlowRules.Clear();

        AssertOffers(baseline, rule, attributeFlow: true);
    }

    [Test]
    public void BehaviourEdited_RuleDisabled_OffersThatPreviewAlone()
    {
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);

        rule.Enabled = false;

        AssertOffers(baseline, rule, behaviour: true);
    }

    [Test]
    public void Capture_AfterTheEditIsSaved_MeasuresFromTheSavedSettings()
    {
        // A successful save keeps the same tracked rule on the page rather than reloading it, so the editor
        // captures again; the saved toggle is now the stored one and has nothing left to preview.
        var rule = ExportRule();
        rule.Enabled = false;

        var baseline = SyncRulePreviewBaseline.Capture(rule);

        AssertOffers(baseline, rule);
    }

    [Test]
    public void Capture_SnapshotsRatherThanReferencesTheRule()
    {
        // The editor mutates the loaded rule in place, tab by tab. A baseline holding references into it would
        // move with every edit and never report one.
        var rule = ExportRule();
        var baseline = SyncRulePreviewBaseline.Capture(rule);

        rule.ObjectScopingCriteriaGroups[0].Criteria[0].StringValue = "Leaver";
        rule.AttributeFlowRules[0].Sources[0].MetaverseAttributeId = 6;

        AssertOffers(baseline, rule, scope: true, attributeFlow: true);
    }
}
