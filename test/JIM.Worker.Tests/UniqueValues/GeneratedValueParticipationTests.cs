// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="GeneratedValueParticipation"/> (Unique Value Generation, #242, Phase 2 work package J): the
/// participating-target computation and adoptable-value lookup shared by the worker's real synchronisation
/// and Sync Preview's read-only evaluation. These pin the exact semantics the worker's private copies used
/// to have (single-source export mappings only, exclusions removed, first non-empty value in ascending
/// Connected System id order, numbers rendered invariant), so an extraction that drifts the behaviour fails
/// here rather than only being noticed as a Sync Preview discrepancy.
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

    #region FindAdoptableValueAsync

    [Test]
    public async Task FindAdoptableValueAsync_NoTargets_ReturnsNullWithoutQueryingAsync()
    {
        var repository = new Mock<ISyncRepository>(MockBehavior.Strict);

        var result = await GeneratedValueParticipation.FindAdoptableValueAsync(repository.Object, Guid.NewGuid(), []);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindAdoptableValueAsync_NoJoinedTargetSystem_ReturnsNullAsync()
    {
        var mvoId = Guid.NewGuid();
        var repository = new Mock<ISyncRepository>();
        repository.Setup(r => r.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync([]);

        var result = await GeneratedValueParticipation.FindAdoptableValueAsync(
            repository.Object, mvoId, [(ConnectedSystemId: 3, AttributeId: 500)]);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindAdoptableValueAsync_LowestConnectedSystemIdWithAValue_WinsOverAHigherIdAsync()
    {
        var mvoId = Guid.NewGuid();
        var csoHigh = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 5 };
        var csoLow = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 2 };

        var repository = new Mock<ISyncRepository>();
        repository.Setup(r => r.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<(Guid, int), ConnectedSystemObject>
            {
                [(mvoId, 5)] = csoHigh,
                [(mvoId, 2)] = csoLow
            });
        repository.Setup(r => r.GetCsoAttributeValuesByCsoIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
            [
                new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = csoHigh, AttributeId = 700, StringValue = "high-value" },
                new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = csoLow, AttributeId = 500, StringValue = "low-value" }
            ]);

        var result = await GeneratedValueParticipation.FindAdoptableValueAsync(
            repository.Object, mvoId, [(ConnectedSystemId: 5, AttributeId: 700), (ConnectedSystemId: 2, AttributeId: 500)]);

        Assert.That(result, Is.EqualTo("low-value"), "ascending Connected System id order must win, not declaration order");
    }

    [Test]
    public async Task FindAdoptableValueAsync_NumericTarget_RendersIntValueInvariantAsync()
    {
        var mvoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 3 };

        var repository = new Mock<ISyncRepository>();
        repository.Setup(r => r.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<(Guid, int), ConnectedSystemObject> { [(mvoId, 3)] = cso });
        repository.Setup(r => r.GetCsoAttributeValuesByCsoIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync([new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = 500, IntValue = 4242 }]);

        var result = await GeneratedValueParticipation.FindAdoptableValueAsync(
            repository.Object, mvoId, [(ConnectedSystemId: 3, AttributeId: 500)]);

        Assert.That(result, Is.EqualTo("4242"));
    }

    [Test]
    public async Task FindAdoptableValueAsync_EmptyValueAtFirstTarget_FallsThroughToTheNextAsync()
    {
        var mvoId = Guid.NewGuid();
        var csoEmpty = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 1 };
        var csoWithValue = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = 2 };

        var repository = new Mock<ISyncRepository>();
        repository.Setup(r => r.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
                It.IsAny<IEnumerable<Guid>>(), It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync(new Dictionary<(Guid, int), ConnectedSystemObject>
            {
                [(mvoId, 1)] = csoEmpty,
                [(mvoId, 2)] = csoWithValue
            });
        repository.Setup(r => r.GetCsoAttributeValuesByCsoIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync([new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = csoWithValue, AttributeId = 500, StringValue = "adopted" }]);

        var result = await GeneratedValueParticipation.FindAdoptableValueAsync(
            repository.Object, mvoId, [(ConnectedSystemId: 1, AttributeId: 400), (ConnectedSystemId: 2, AttributeId: 500)]);

        Assert.That(result, Is.EqualTo("adopted"));
    }

    #endregion
}
