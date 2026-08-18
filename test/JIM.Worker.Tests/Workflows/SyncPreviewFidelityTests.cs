// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The release-blocking fidelity pairing for the Sync Preview Engine (#288, PRD requirement 9): a preview's
/// speculative outcome tree must have the same shape as the tree the real synchronisation then records for
/// the same object over the same data. Each test previews first (which must not disturb the data: the
/// pairing itself proves it, because the real sync then runs over whatever state the preview left) and
/// diffs the preview tree against the real tree mapped through the one shared mapping,
/// <see cref="SyncOutcomeNode.FromSyncOutcome"/> (PRD decision D4).
///
/// The shape compared is (OutcomeType, DetailCount, child count) per node in sibling order. Entity ids and
/// detail messages are deliberately excluded: a preview persists nothing, so it has no Pending Export or
/// provisioning CSO ids to carry.
/// </summary>
[TestFixture]
public class SyncPreviewFidelityTests : WorkflowTestBase
{
    /// <summary>
    /// The flagship chain: an unjoined CSO that would project, flow a display name, provision a target
    /// object and stage its Pending Export. Preview tree and real tree must match shape node for node:
    /// Projected -> Attribute Flow -> Provisioned -> Pending Export.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ProjectionWithProvisioningExport_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - source system with a projecting import rule flowing DisplayName
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");

        // Flow into a correctly-named built-in Display Name attribute so the Identity resolves a name,
        // matching the real naming path (see ProjectedOutcomeDescriptionTests for the rationale).
        var mvDisplayNameAttr = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvDisplayNameAttr);
        mvType.Attributes.Add(mvDisplayNameAttr);
        await DbContext.SaveChangesAsync();

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        // Target system with a provisioning export rule flowing the same attribute back out
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "user");
        var exportRule = await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export");
        var targetDisplayNameAttr = targetType.Attributes.First(a => a.Name == "DisplayName");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvDisplayNameAttr,
                MetaverseAttributeId = mvDisplayNameAttr.Id
            }}
        });

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Act 1 - preview, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert - the real tree, mapped through the one shared mapping, matches the preview tree's shape
        var realTree = MapRealOutcomeTree(fullSyncActivity);
        Assert.That(realTree, Is.Not.Empty, "The real synchronisation must have recorded an outcome tree to pair against");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");

        // The preview's inbound summary and outbound counters must agree with what really happened
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Inbound!.WouldProject, Is.True);
            Assert.That(preview.Outbound.ObjectsToCreate, Is.EqualTo(1),
                "The preview said one target object would be created, and the real sync provisioned one");
            Assert.That(preview.OutcomeTree[0].TargetEntityDescription, Is.EqualTo(realTree[0].TargetEntityDescription),
                "Both trees must name the Identity identically at the root");
        }
    }

    /// <summary>
    /// The steady-state chain: a joined CSO whose source value changed, with no export rules. Preview tree
    /// and real tree must both be a single Attribute Flow root.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_JoinedCsoWithChangedValue_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - projecting import rule with a DisplayName flow; first sync establishes the join
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var firstProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 1", ConnectedSystemRunType.FullSynchronisation);
        var firstActivity = await CreateActivityAsync(sourceSystem.Id, firstProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, firstProfile, firstActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "The first sync must have joined the CSO");

        // The source value changes
        cso.AttributeValues.Single(av => av.AttributeId == csoDisplayNameAttr.Id).StringValue = "John Smith-Jones";

        // Act 1 - preview the changed object against the live joined state
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real second synchronisation
        var secondProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        var secondActivity = await CreateActivityAsync(sourceSystem.Id, secondProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, secondProfile, secondActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert
        var realTree = MapRealOutcomeTree(secondActivity);
        Assert.That(realTree, Is.Not.Empty, "The second sync must have recorded an Attribute Flow outcome");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
                "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
            Assert.That(preview.Inbound!.AlreadyJoinedMetaverseObjectId, Is.EqualTo(cso.MetaverseObjectId));
            Assert.That(preview.Inbound!.AttributeFlowChanges.Any(c => c.IsAddition && c.Value == "John Smith-Jones"), Is.True);
        }
    }

    #region helpers

    /// <summary>
    /// Maps the real synchronisation's recorded outcome roots for the run through the shared
    /// <see cref="SyncOutcomeNode.FromSyncOutcome"/> mapping, in root ordinal order.
    /// </summary>
    private static List<SyncOutcomeNode> MapRealOutcomeTree(Activity activity)
    {
        return activity.RunProfileExecutionItems
            .SelectMany(rpei => rpei.SyncOutcomes)
            .Where(o => o.ParentSyncOutcome == null && !o.ParentSyncOutcomeId.HasValue)
            .OrderBy(o => o.Ordinal)
            .Select(SyncOutcomeNode.FromSyncOutcome)
            .ToList();
    }

    /// <summary>
    /// Flattens a tree into a comparable shape description: one line per node in sibling order, carrying
    /// depth, outcome type, detail count and child count.
    /// </summary>
    private static string DescribeTree(IEnumerable<SyncOutcomeNode> nodes, int depth = 0)
    {
        var lines = new List<string>();
        foreach (var node in nodes)
        {
            lines.Add($"{new string(' ', depth * 2)}{node.OutcomeType} (count: {node.DetailCount?.ToString() ?? "-"}, children: {node.Children.Count})");
            if (node.Children.Count > 0)
                lines.Add(DescribeTree(node.Children.OrderBy(c => c.Ordinal), depth + 1));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Creates an export Synchronisation Rule (local twin of the DeletionRuleWorkflowTests helper; the
    /// shared base does not carry one).
    /// </summary>
    private async Task<SyncRule> CreateExportSyncRuleAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string name)
    {
        var syncRule = new SyncRule
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            ConnectedSystemObjectType = csoType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };

        DbContext.SyncRules.Add(syncRule);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedSyncRule(syncRule);

        return syncRule;
    }

    #endregion
}
