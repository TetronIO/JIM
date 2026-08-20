// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Expressions;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The proposed Attribute Flow mappings an administrator is about to save, as the G2 preview adapter receives
/// them (#1437).
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowProposalTests
{
    [Test]
    public void FromCurrentMappings_ImportRule_CapturesEachMappingAndItsSources()
    {
        var rule = BuildImportRuleWithMappings();

        var proposal = SyncRuleAttributeFlowProposal.FromCurrentMappings(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mappings, Has.Count.EqualTo(2));
            var displayName = proposal.Mappings.Single(m => m.TargetMetaverseAttributeId == DisplayNameAttributeId);
            Assert.That(displayName.Sources, Has.Count.EqualTo(1));
            Assert.That(displayName.Sources[0].ConnectedSystemAttributeId, Is.EqualTo(SourceAttributeId));
            Assert.That(displayName.Priority, Is.EqualTo(1));
            Assert.That(displayName.NullIsValue, Is.True);
        }
    }

    [Test]
    public void FromCurrentMappings_ExpressionSource_CarriesTheExpressionAndItsMissingInputBehaviour()
    {
        // The Expression and its Missing Input Behaviour decide what a malformed value becomes, so a proposal
        // that lost either would preview a different flow than the one being saved.
        var rule = BuildImportRuleWithMappings();

        var proposal = SyncRuleAttributeFlowProposal.FromCurrentMappings(rule);

        var expressionMapping = proposal.Mappings.Single(m => m.TargetMetaverseAttributeId == EmailAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expressionMapping.Sources[0].Expression, Is.EqualTo("[givenName] + \".\" + [sn] + \"@corp.local\""));
            Assert.That(expressionMapping.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.ContributeNoValue));
        }
    }

    [Test]
    public void DescribesSameMappingsAs_SameSetRebuilt_IsTrue()
    {
        // The editor rebuilds its proposal on every render, so reference equality would mark every preview stale
        // the moment it finished.
        var first = SyncRuleAttributeFlowProposal.FromCurrentMappings(BuildImportRuleWithMappings());
        var second = SyncRuleAttributeFlowProposal.FromCurrentMappings(BuildImportRuleWithMappings());

        Assert.That(first.DescribesSameMappingsAs(second), Is.True);
    }

    [Test]
    public void DescribesSameMappingsAs_MappingsListedInAnotherOrder_IsTrue()
    {
        // One mapping per target attribute, and the set is evaluated per attribute, so the order the editor lists
        // them in is presentation rather than configuration.
        var mappings = SyncRuleAttributeFlowProposal.FromCurrentMappings(BuildImportRuleWithMappings()).Mappings;
        var reversed = new SyncRuleAttributeFlowProposal([.. mappings.Reverse()]);

        Assert.That(new SyncRuleAttributeFlowProposal(mappings).DescribesSameMappingsAs(reversed), Is.True);
    }

    [Test]
    public void DescribesSameMappingsAs_SourcesReordered_IsFalse()
    {
        // Source order is NOT presentation: chained sources feed each other, so swapping two changes the value.
        var first = new SyncRuleAttributeFlowProposal([Mapping(DisplayNameAttributeId, Source(0, "a"), Source(1, "b"))]);
        var second = new SyncRuleAttributeFlowProposal([Mapping(DisplayNameAttributeId, Source(0, "b"), Source(1, "a"))]);

        Assert.That(first.DescribesSameMappingsAs(second), Is.False);
    }

    [Test]
    public void DescribesSameMappingsAs_ExpressionEdited_IsFalse()
    {
        var first = new SyncRuleAttributeFlowProposal([Mapping(EmailAttributeId, Source(0, "[mail]"))]);
        var second = new SyncRuleAttributeFlowProposal([Mapping(EmailAttributeId, Source(0, "[userPrincipalName]"))]);

        Assert.That(first.DescribesSameMappingsAs(second), Is.False);
    }

    [Test]
    public void DescribesSameMappingsAs_PriorityChanged_IsFalse()
    {
        // Priority decides whether the mapping wins the attribute at all, so it is part of what the flow does.
        var first = new SyncRuleAttributeFlowProposal([Mapping(DisplayNameAttributeId, Source(0, "[cn]")) with { Priority = 1 }]);
        var second = new SyncRuleAttributeFlowProposal([Mapping(DisplayNameAttributeId, Source(0, "[cn]")) with { Priority = 2 }]);

        Assert.That(first.DescribesSameMappingsAs(second), Is.False);
    }

    [Test]
    public void DescribesSameMappingsAs_MappingRemoved_IsFalse()
    {
        var full = SyncRuleAttributeFlowProposal.FromCurrentMappings(BuildImportRuleWithMappings());
        var reduced = new SyncRuleAttributeFlowProposal([full.Mappings[0]]);

        Assert.That(full.DescribesSameMappingsAs(reduced), Is.False);
    }

    [Test]
    public void DescribesSameMappingsAs_Null_IsFalse()
    {
        var proposal = SyncRuleAttributeFlowProposal.FromCurrentMappings(BuildImportRuleWithMappings());

        Assert.That(proposal.DescribesSameMappingsAs(null), Is.False);
    }

    [Test]
    public void SyncRuleAttributeFlowProposal_SerialisedAndBack_SurvivesTheQueue()
    {
        // A proposal is queued for JIM.Worker as JSON. The rule's own mapping graph cannot be sent (each source
        // carries whole attribute entities and a backlink to the rule), which is why this DTO exists.
        var original = SyncRuleAttributeFlowProposal.FromCurrentMappings(BuildImportRuleWithMappings());

        var restored = JsonSerializer.Deserialize<SyncRuleAttributeFlowProposal>(JsonSerializer.Serialize(original));

        Assert.That(restored, Is.Not.Null);
        Assert.That(original.DescribesSameMappingsAs(restored), Is.True);
    }

    // ── The unsaved shape the portal editors build ───────────────────────────────────────────────────────────

    [Test]
    public void FromCurrentMappings_MappingCarriesOnlyItsTargetNavigation_StillReadsTheAttributeId()
    {
        // The shape the Attribute Flow editor builds when an administrator ADDS a mapping: the navigation is set,
        // and the foreign key stays unassigned because nothing has been saved. Reading the key alone made a mapping
        // the editor plainly shows invisible to the proposal, so the preview refused it as "names no target
        // attribute" and, in the same breath, reported the attribute as no longer written at all.
        var email = new MetaverseAttribute { Id = 201, Name = "Email", Type = AttributeDataType.Text };
        var source = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "mail", Type = AttributeDataType.Text };
        var rule = new SyncRule { Id = 1, Name = "HR Import", Direction = SyncRuleDirection.Import };

        var unsaved = new SyncRuleMapping { TargetMetaverseAttribute = email };
        unsaved.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source });
        rule.AttributeFlowRules.Add(unsaved);

        var proposal = SyncRuleAttributeFlowProposal.FromCurrentMappings(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mappings[0].TargetMetaverseAttributeId, Is.EqualTo(email.Id));
            Assert.That(proposal.Mappings[0].Sources[0].ConnectedSystemAttributeId, Is.EqualTo(source.Id));
        }
    }

    [Test]
    public void FromCurrentMappings_ExportMappingCarriesOnlyItsNavigations_StillReadsBothAttributeIds()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 103, Name = "mail", Type = AttributeDataType.Text };
        var source = new MetaverseAttribute { Id = 201, Name = "Email", Type = AttributeDataType.Text };
        var rule = new SyncRule { Id = 2, Name = "Directory Export", Direction = SyncRuleDirection.Export };

        var unsaved = new SyncRuleMapping { TargetConnectedSystemAttribute = target };
        unsaved.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source });
        rule.AttributeFlowRules.Add(unsaved);

        var proposal = SyncRuleAttributeFlowProposal.FromCurrentMappings(rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.Mappings[0].TargetConnectedSystemAttributeId, Is.EqualTo(target.Id));
            Assert.That(proposal.Mappings[0].Sources[0].MetaverseAttributeId, Is.EqualTo(source.Id));
        }
    }

    [Test]
    public void FromCurrentMappings_SavedAndUnsavedFormsOfTheSameMapping_CompareAsTheSameProposal()
    {
        // The staleness check runs the two forms against each other every render, so a saved mapping and the
        // editor's unsaved copy of it must not read as different proposals.
        var email = new MetaverseAttribute { Id = 201, Name = "Email", Type = AttributeDataType.Text };
        var source = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "mail", Type = AttributeDataType.Text };

        var savedRule = new SyncRule { Id = 1, Direction = SyncRuleDirection.Import };
        var saved = new SyncRuleMapping { TargetMetaverseAttribute = email, TargetMetaverseAttributeId = email.Id };
        saved.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id });
        savedRule.AttributeFlowRules.Add(saved);

        var unsavedRule = new SyncRule { Id = 1, Direction = SyncRuleDirection.Import };
        var unsaved = new SyncRuleMapping { TargetMetaverseAttribute = email };
        unsaved.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source });
        unsavedRule.AttributeFlowRules.Add(unsaved);

        Assert.That(SyncRuleAttributeFlowProposal.FromCurrentMappings(savedRule)
            .DescribesSameMappingsAs(SyncRuleAttributeFlowProposal.FromCurrentMappings(unsavedRule)), Is.True);
    }

    #region helpers

    private const int DisplayNameAttributeId = 11;
    private const int EmailAttributeId = 12;
    private const int SourceAttributeId = 101;

    private static SyncRuleMappingProposal Mapping(int targetMetaverseAttributeId, params SyncRuleMappingSourceProposal[] sources) =>
        new(targetMetaverseAttributeId, null, sources);

    private static SyncRuleMappingSourceProposal Source(int order, string expression) =>
        new(order, null, null, expression);

    private static SyncRule BuildImportRuleWithMappings()
    {
        var sourceAttribute = new ConnectedSystemObjectTypeAttribute { Id = SourceAttributeId, Name = "cn", Type = AttributeDataType.Text };
        var displayName = new MetaverseAttribute { Id = DisplayNameAttributeId, Name = "Display Name", Type = AttributeDataType.Text };
        var email = new MetaverseAttribute { Id = EmailAttributeId, Name = "Email", Type = AttributeDataType.Text };

        var rule = new SyncRule { Id = 3, Direction = SyncRuleDirection.Import };

        var direct = new SyncRuleMapping
        {
            TargetMetaverseAttribute = displayName,
            TargetMetaverseAttributeId = displayName.Id,
            Priority = 1,
            NullIsValue = true
        };
        direct.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            ConnectedSystemAttribute = sourceAttribute,
            ConnectedSystemAttributeId = sourceAttribute.Id
        });
        rule.AttributeFlowRules.Add(direct);

        var expression = new SyncRuleMapping
        {
            TargetMetaverseAttribute = email,
            TargetMetaverseAttributeId = email.Id
        };
        expression.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            Expression = "[givenName] + \".\" + [sn] + \"@corp.local\"",
            MissingInputBehaviour = MissingInputBehaviour.ContributeNoValue
        });
        rule.AttributeFlowRules.Add(expression);

        return rule;
    }

    #endregion
}
