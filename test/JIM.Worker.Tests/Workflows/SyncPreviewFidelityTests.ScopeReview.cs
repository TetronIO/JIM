// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Whole-system preview fidelity for the export scope review (#892, #1925, #1530). An export rule change flags every
/// Metaverse Object of its type, and whichever system's synchronisation runs next drains the flagged set after its own
/// objects: provisioning what came into scope, deprovisioning what left it. The preview of a Full Synchronisation must
/// propose that review too, once per object, whether or not one of the synchronised system's own objects reaches it.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewFullSyncAsync_OfASystemThatDoesNotReachTheFlaggedObjects_ProposesTheReviewTheRunMakesAsync()
    {
        var ctx = await SetUpScopeReviewAsync();

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Target.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Target));

        var realCreates = CreatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realCreates, Is.EqualTo(1), "arrange: the run's review provisions the flagged object it does not reach");
            Assert.That(preview.Counts.ObjectsToCreate, Is.EqualTo(realCreates), "the preview proposes the provisioning the review stages");
            Assert.That(preview.Counts.ExportScopeReviewed, Is.EqualTo(1), "the preview says it reviewed the flagged object");
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_OfASystemThatReachesTheFlaggedObjects_ProposesTheReviewOnceAsync()
    {
        var ctx = await SetUpScopeReviewAsync();

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        var realCreates = CreatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realCreates, Is.EqualTo(1), "arrange: the run provisions the flagged object once");
            Assert.That(preview.Counts.ObjectsToCreate, Is.EqualTo(realCreates),
                "the walk reviews the object it reaches, and the review after the walk does not propose it again");
            Assert.That(preview.Counts.ExportScopeReviewed, Is.Zero, "the walk reviewed it, so the review after it has nothing left");
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_WithNothingFlagged_ProposesNoReviewAsync()
    {
        var ctx = await SetUpScopeReviewAsync();
        foreach (var mvo in SyncRepo.MetaverseObjects.Values)
            mvo.ScopeReviewPending = false;

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Target.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Target));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CreatesStagedFor(ctx.Target), Is.Zero, "arrange: with nothing flagged, the target's run provisions nothing");
            Assert.That(preview.Counts.ObjectsToCreate, Is.Zero, "and the preview proposes nothing");
            Assert.That(preview.Counts.ExportScopeReviewed, Is.Zero);
        }
    }

    private sealed record ScopeReviewContext(ConnectedSystem Source, ConnectedSystem Target);

    /// <summary>
    /// HR projects John Smith while the Active Directory export rule does not provision; provisioning is then switched
    /// on, flagging every Person for export scope review exactly as that configuration change does (#1925). Nothing
    /// has been provisioned yet.
    /// </summary>
    private async Task<ScopeReviewContext> SetUpScopeReviewAsync()
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var mvType = await CreateMvObjectTypeAsync("Person");
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        await DbContext.SaveChangesAsync();
        var mvDisplayName = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);

        await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export", enableProvisioning: false);
        FlowToAttribute(exportRule, targetType.Attributes.First(a => a.Name == "DisplayName"), mvDisplayName);
        await DbContext.SaveChangesAsync();
        await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");
        await RunFullSyncAsync(source);
        Assert.That(SyncRepo.ConnectedSystemObjects.Values.Count(c => c.ConnectedSystemId == target.Id), Is.Zero,
            "arrange: with provisioning off, nothing is provisioned");

        // Stamped as saving the change stamps it, so the next Full Synchronisation applies it to every object.
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.LastUpdated = DateTime.UtcNow;
        var flagged = await SyncRepo.FlagMetaverseObjectsOfTypeForScopeReviewAsync(mvType.Id);
        Assert.That(flagged, Is.EqualTo(1), "arrange: creating the export rule flags John Smith for review");
        SyncRepo.ClearAllPendingExports();

        return new ScopeReviewContext(source, target);
    }

    private int CreatesStagedFor(ConnectedSystem system) => SyncRepo.PendingExports.Values
        .Count(pe => pe.ConnectedSystemId == system.Id && pe.ChangeType == PendingExportChangeType.Create);
}
