// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="GeneratedValueParticipation"/> (Unique Value Generation, #242, Phase 2 work package J; adoption
/// source changed by product-owner decision): the participating-target computation (still connector-space,
/// feeding the generation-time collision gate) and the Metaverse Object's own-value adoption lookup, shared
/// by the worker's real synchronisation and Sync Preview's read-only evaluation. These pin the exact
/// semantics the worker's private copies used to have, so an extraction that drifts the behaviour fails here
/// rather than only being noticed as a Sync Preview discrepancy.
/// </summary>
[TestFixture]
public class GeneratedValueParticipationTests
{
    private static MetaverseAttribute GeneratedAttribute() => new() { Id = 1, Name = "AccountName", Type = AttributeDataType.Text };

    private static SyncRuleMapping GeneratedMapping(MetaverseAttribute attribute, params int[] excludedSystemIds)
    {
        var generation = new SyncRuleMappingGeneration { Id = 1 };
        foreach (var systemId in excludedSystemIds)
            generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = systemId });

        return new SyncRuleMapping
        {
            Id = 100,
            TargetMetaverseAttribute = attribute,
            TargetMetaverseAttributeId = attribute.Id,
            Generation = generation
        };
    }

    private static SyncRule ExportRule(int connectedSystemId, bool enabled, params SyncRuleMapping[] mappings)
    {
        var rule = new SyncRule { Id = connectedSystemId * 10, ConnectedSystemId = connectedSystemId, Enabled = enabled, Direction = SyncRuleDirection.Export };
        foreach (var mapping in mappings)
        {
            mapping.SyncRule = rule;
            mapping.SyncRuleId = rule.Id;
            rule.AttributeFlowRules.Add(mapping);
        }
        return rule;
    }

    private static SyncRuleMapping SingleSourceExportMapping(MetaverseAttribute source, int targetAttributeId, bool enabled = true) => new()
    {
        Id = targetAttributeId + 1000,
        Enabled = enabled,
        TargetConnectedSystemAttributeId = targetAttributeId,
        Sources = { new SyncRuleMappingSource { Order = 1, MetaverseAttributeId = source.Id } }
    };

    #region ComputeParticipatingTargets

    [Test]
    public void ComputeParticipatingTargets_NoTargetMetaverseAttribute_ReturnsEmpty()
    {
        var mapping = new SyncRuleMapping { Generation = new SyncRuleMappingGeneration() };

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, []);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeParticipatingTargets_NoGeneration_ReturnsEmpty()
    {
        var attribute = GeneratedAttribute();
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = attribute, TargetMetaverseAttributeId = attribute.Id };

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, []);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeParticipatingTargets_SingleSourceExportMappingOfTheGeneratedAttribute_IsIncluded()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var exportMapping = SingleSourceExportMapping(attribute, targetAttributeId: 500);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, exportMapping);

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, [rule]);

        Assert.That(targets, Is.EqualTo(new[] { (ConnectedSystemId: 3, AttributeId: 500) }));
    }

    [Test]
    public void ComputeParticipatingTargets_ExcludedConnectedSystem_IsNotAParticipatingTarget()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute, excludedSystemIds: 3);
        var exportMapping = SingleSourceExportMapping(attribute, targetAttributeId: 500);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, exportMapping);

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty, "an excluded Connected System must never be a participating target");
    }

    [Test]
    public void ComputeParticipatingTargets_MultiSourceExportMapping_IsNotAParticipatingTarget()
    {
        var attribute = GeneratedAttribute();
        var otherAttribute = new MetaverseAttribute { Id = 2, Name = "Other", Type = AttributeDataType.Text };
        var mapping = GeneratedMapping(attribute);
        var multiSourceMapping = new SyncRuleMapping
        {
            Id = 501,
            Enabled = true,
            TargetConnectedSystemAttributeId = 500,
            Sources =
            {
                new SyncRuleMappingSource { Order = 1, MetaverseAttributeId = attribute.Id },
                new SyncRuleMappingSource { Order = 2, MetaverseAttributeId = otherAttribute.Id }
            }
        };
        var rule = ExportRule(connectedSystemId: 3, enabled: true, multiSourceMapping);

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty, "an export mapping with more than one source is not a participating target");
    }

    [Test]
    public void ComputeParticipatingTargets_ExportMappingOfADifferentAttribute_IsNotAParticipatingTarget()
    {
        var attribute = GeneratedAttribute();
        var otherAttribute = new MetaverseAttribute { Id = 2, Name = "Other", Type = AttributeDataType.Text };
        var mapping = GeneratedMapping(attribute);
        var exportMapping = SingleSourceExportMapping(otherAttribute, targetAttributeId: 500);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, exportMapping);

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeParticipatingTargets_DisabledExportRule_IsExcluded()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var exportMapping = SingleSourceExportMapping(attribute, targetAttributeId: 500);
        var rule = ExportRule(connectedSystemId: 3, enabled: false, exportMapping);

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeParticipatingTargets_DisabledExportMapping_IsExcluded()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var exportMapping = SingleSourceExportMapping(attribute, targetAttributeId: 500, enabled: false);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, exportMapping);

        var targets = GeneratedValueParticipation.ComputeParticipatingTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    #endregion

    #region FindMetaverseOwnValue

    [Test]
    public void FindMetaverseOwnValue_NoValueForAttribute_ReturnsNull()
    {
        var mvo = new MetaverseObject();

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindMetaverseOwnValue_PersistedTextValueNotPendingRemoval_IsReturned()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, StringValue = "jsmith" } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.EqualTo("jsmith"), "a value left behind by a withdrawn higher-priority contributor must be adoptable");
    }

    [Test]
    public void FindMetaverseOwnValue_ValuePendingRemovalThisPass_ReturnsNull()
    {
        var value = new MetaverseObjectAttributeValue { AttributeId = 500, StringValue = "jsmith" };
        var mvo = new MetaverseObject { AttributeValues = { value } };
        mvo.PendingAttributeValueRemovals.Add(value);

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.Null, "a value being removed this same pass by a real removal must never be adopted");
    }

    [Test]
    public void FindMetaverseOwnValue_IntValue_RendersInvariant()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, IntValue = 4242 } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.EqualTo("4242"));
    }

    [Test]
    public void FindMetaverseOwnValue_LongValue_RendersInvariant()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, LongValue = 42424242424242L } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.EqualTo("42424242424242"));
    }

    [Test]
    public void FindMetaverseOwnValue_AssertedNullMarkerRow_ReturnsNull()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, NullValue = true } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.Null, "an asserted-null marker carries no value to adopt");
    }

    [Test]
    public void FindMetaverseOwnValue_ValueContributedByTheGeneratingRuleItself_ReturnsNull()
    {
        // The commit-loser case: a value THIS generated mapping produced in an earlier pass, whose assignment
        // then lost the cross-run collision race, is left on the object with no assignment of its own. Self-
        // healing means the object must draw a fresh candidate next time, never "adopt" its own abandoned
        // attempt, so a value stamped by the generating rule's own id must never be treated as adoptable.
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, StringValue = "joe.bloggs", ContributedBySyncRuleId = 1 } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindMetaverseOwnValue_ValueContributedByADifferentRule_IsReturned()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, StringValue = "jsmith", ContributedBySyncRuleId = 2 } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.EqualTo("jsmith"));
    }

    [Test]
    public void FindMetaverseOwnValue_NoGeneratingSyncRuleIdSupplied_DoesNotExcludeByProvenance()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 500, StringValue = "jsmith", ContributedBySyncRuleId = 1 } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: null);

        Assert.That(result, Is.EqualTo("jsmith"), "with no generating rule id to compare against, provenance cannot disqualify a value");
    }

    [Test]
    public void FindMetaverseOwnValue_ValueForADifferentAttribute_ReturnsNull()
    {
        var mvo = new MetaverseObject
        {
            AttributeValues = { new MetaverseObjectAttributeValue { AttributeId = 999, StringValue = "other" } }
        };

        var result = GeneratedValueParticipation.FindMetaverseOwnValue(mvo, attributeId: 500, generatingSyncRuleId: 1);

        Assert.That(result, Is.Null);
    }

    #endregion
}
