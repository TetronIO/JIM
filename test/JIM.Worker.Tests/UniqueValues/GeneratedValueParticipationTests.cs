// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="GeneratedValueParticipation"/> (Unique Value Generation, #242, Phase 2 work package J): the
/// participating-target computation feeding the generation-time collision gate, shared by the worker's real synchronisation and Sync Preview's read-only evaluation. These pin the exact
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

    #region ComputeProbeTargets

    private static ConnectedSystemObjectTypeAttribute TargetAttribute(int id, string name = "sAMAccountName", AttributeDataType type = AttributeDataType.Text,
        bool isExternalId = false, bool isSecondaryExternalId = false) => new()
    {
        Id = id,
        Name = name,
        Type = type,
        IsExternalId = isExternalId,
        IsSecondaryExternalId = isSecondaryExternalId
    };

    private static SyncRuleMapping ProbeableExportMapping(MetaverseAttribute source, ConnectedSystemObjectTypeAttribute target, bool enabled = true)
    {
        var mapping = SingleSourceExportMapping(source, target.Id, enabled);
        mapping.TargetConnectedSystemAttribute = target;
        return mapping;
    }

    [Test]
    public void ComputeProbeTargets_DirectSingleSourceTextFlow_IsAProbeTarget()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, ProbeableExportMapping(attribute, TargetAttribute(500)));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.EqualTo(new[] { new UniquenessProbeTarget(3, 500) }));
    }

    [Test]
    public void ComputeProbeTargets_SameTargetFromTwoRules_IsProbedOnce()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var target = TargetAttribute(500);
        var first = ExportRule(connectedSystemId: 3, enabled: true, ProbeableExportMapping(attribute, target));
        var second = ExportRule(connectedSystemId: 3, enabled: true, ProbeableExportMapping(attribute, target));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [first, second]);

        Assert.That(targets, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Plan Phase 7 item 3: a value reaching the target through an export expression is not probed (Collision
    /// Remediation covers it); the participation filter already drops anything but a direct single-source flow.
    /// </summary>
    [Test]
    public void ComputeProbeTargets_ExpressionExportMapping_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var expressionMapping = new SyncRuleMapping
        {
            Id = 501,
            Enabled = true,
            TargetConnectedSystemAttributeId = 500,
            TargetConnectedSystemAttribute = TargetAttribute(500, "userPrincipalName"),
            Sources = { new SyncRuleMappingSource { Order = 1, Expression = "mv[\"AccountName\"] + \"@corp.local\"" } }
        };
        var rule = ExportRule(connectedSystemId: 3, enabled: true, expressionMapping);

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeProbeTargets_DistinguishedNameTarget_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var rule = ExportRule(connectedSystemId: 3, enabled: true,
            ProbeableExportMapping(attribute, TargetAttribute(500, "distinguishedName", isSecondaryExternalId: true)));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty, "the DN is unique per container, so a partition-wide search would report false collisions");
    }

    [Test]
    public void ComputeProbeTargets_ExternalIdTarget_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var rule = ExportRule(connectedSystemId: 3, enabled: true,
            ProbeableExportMapping(attribute, TargetAttribute(500, "objectGUID", isExternalId: true)));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeProbeTargets_NonTextTarget_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var rule = ExportRule(connectedSystemId: 3, enabled: true,
            ProbeableExportMapping(attribute, TargetAttribute(500, "employeeNumber", AttributeDataType.Number)));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeProbeTargets_TargetAttributeNotLoaded_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, SingleSourceExportMapping(attribute, targetAttributeId: 500));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty, "without the attribute JIM cannot tell what it is, so it does not guess");
    }

    [Test]
    public void ComputeProbeTargets_ExcludedConnectedSystem_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute, excludedSystemIds: 3);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, ProbeableExportMapping(attribute, TargetAttribute(500)));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void ComputeProbeTargets_DisabledExportMapping_IsNotProbed()
    {
        var attribute = GeneratedAttribute();
        var mapping = GeneratedMapping(attribute);
        var rule = ExportRule(connectedSystemId: 3, enabled: true, ProbeableExportMapping(attribute, TargetAttribute(500), enabled: false));

        var targets = GeneratedValueParticipation.ComputeProbeTargets(mapping, [rule]);

        Assert.That(targets, Is.Empty);
    }

    [Test]
    public void IsDirectProbeTarget_ReturnsWhetherAnExportModeTargetIsProbed()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueParticipation.IsDirectProbeTarget(TargetAttribute(1)), Is.True);
            Assert.That(GeneratedValueParticipation.IsDirectProbeTarget(TargetAttribute(2, "distinguishedName", isSecondaryExternalId: true)), Is.False);
            Assert.That(GeneratedValueParticipation.IsDirectProbeTarget(TargetAttribute(3, "objectGUID", isExternalId: true)), Is.False);
            Assert.That(GeneratedValueParticipation.IsDirectProbeTarget(TargetAttribute(4, "uidNumber", AttributeDataType.Number)), Is.False);
            Assert.That(GeneratedValueParticipation.IsDirectProbeTarget(null), Is.False);
        }
    }

    #endregion
}
