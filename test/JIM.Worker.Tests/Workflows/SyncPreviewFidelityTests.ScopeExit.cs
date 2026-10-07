// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Whole-system preview fidelity for an object leaving import scope while its Metaverse Object lives on (#1530). The
/// run recalls what the departing object contributed and evaluates exports over the recall, so a target holding a
/// recalled value is sent the change. The preview of a Full Synchronisation must propose those exports too, or a
/// scope exit reads as touching nothing outside the system being synchronised.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewFullSyncAsync_OfASourceObjectLeavingScopeWhoseValuesAreRecalled_ProposesTheTargetUpdatesTheRunMakesAsync()
    {
        var ctx = await SetUpScopeExitWithRecallAsync();

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        var realUpdates = UpdatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.EqualTo(1), "arrange: the run sends the recall of the display name to Active Directory");
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates), "the preview proposes the update the recall causes");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfASourceObjectLeavingScopeWhoseValuesAreRecalled_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var ctx = await SetUpScopeExitWithRecallAsync();

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Source.Id, ctx.SourceObject.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
            "the preview's tree records the recall's export where the run records it");
    }

    private sealed record ScopeExitContext(ConnectedSystem Source, ConnectedSystem Target, ConnectedSystemObject SourceObject);

    /// <summary>
    /// HR projects John Smith with his display name and employee ID, and Active Directory, which imports too, joins
    /// its account to him on employee ID and is sent his display name. John then leaves the scope of HR's import
    /// rule. Active Directory keeps the Metaverse Object alive and is still an import source, so the display name
    /// HR contributed is recalled rather than kept.
    /// </summary>
    private async Task<ScopeExitContext> SetUpScopeExitWithRecallAsync()
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person", MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: TimeSpan.Zero);
        var mvDisplayName = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hrImport = await CreateScopedImportSyncRuleAsync(source, sourceType, mvType);
        var hrEmployeeId = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        hrImport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImport, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId, ConnectedSystemAttributeId = hrEmployeeId.Id } }
        });

        var adImport = await CreateImportSyncRuleAsync(target.Id, targetType, mvType, "AD Import", enableProjection: false);
        var adEmployeeId = targetType.Attributes.First(a => a.Name == "EmployeeId");
        adImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = adImport, SyncRuleId = adImport.Id, Order = 0, CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = adEmployeeId, ConnectedSystemAttributeId = adEmployeeId.Id }]
        });
        var adExport = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export", enableProvisioning: false);
        FlowToAttribute(adExport, targetType.Attributes.First(a => a.Name == "DisplayName"), mvDisplayName);
        await DbContext.SaveChangesAsync();

        var sourceObject = await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");
        await CreateCsoAsync(target.Id, targetType, "John Smith", "EMP001");
        await RunFullSyncAsync(source);
        await RunFullSyncAsync(await ReloadEntityAsync(target));
        Assert.That(SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == target.Id).Select(c => c.MetaverseObjectId),
            Is.EqualTo(new[] { (await ReloadEntityAsync(sourceObject)).MetaverseObjectId }), "arrange: Active Directory joins John Smith");
        SyncRepo.ClearAllPendingExports();

        sourceObject = await ReloadEntityAsync(sourceObject);
        sourceObject.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "OUT_OF_SCOPE";
        sourceObject.LastUpdated = DateTime.UtcNow;

        return new ScopeExitContext(source, target, sourceObject);
    }
}
