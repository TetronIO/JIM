// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
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

    [Test]
    public async Task StreamFullSyncPreviewAsync_OfASystemThatDoesNotReachTheFlaggedObjects_YieldsTheReviewAfterTheWalkAsync()
    {
        var ctx = await SetUpScopeReviewAsync();
        var john = SyncRepo.MetaverseObjects.Values.Single(m => m.ScopeReviewPending);

        var items = new List<FullSyncPreviewItem>();
        await foreach (var item in Jim.SyncPreview.StreamFullSyncPreviewAsync(ctx.Target.Id))
            items.Add(item);

        var review = items.Single(i => i.Kind == FullSyncPreviewItemKind.ExportScopeReview);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(items[^1], Is.SameAs(review), "the run drains the review after its own objects");
            Assert.That(review.MetaverseObjectId, Is.EqualTo(john.Id));
            Assert.That(review.DisplayName, Is.EqualTo("John Smith"));
            Assert.That(review.ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(review.Preview!.Outbound.ObjectsToCreate, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_FlaggedObjectWithAValueChangedOutsideSynchronisation_ProposesTheUpdateTheRunStagesAsync()
    {
        // #2615: John's account exists and is current; his name is then changed in the portal (outside any
        // synchronisation), which flags him and records the change. The review a run of an unrelated system makes
        // delivers it, so the preview of that run must propose it.
        var ctx = await SetUpScopeReviewAsync();
        // Let go of what the arranging synchronisation modified on instances shared with the DbContext, so it can save a new system.
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        var unrelated = await CreateConnectedSystemAsync("Unrelated");
        await CreateCsoTypeAsync(unrelated.Id, "user");
        // The in-memory store knows only the Metaverse Attributes it is told of; the review skips a recorded change to one it does not know.
        foreach (var attribute in DbContext.MetaverseAttributes.ToList())
            SyncRepo.SeedMetaverseAttributeName(attribute.Id, attribute.Name);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Target));
        ConfirmProvisionedAccounts(ctx.Target);
        await RenameJohnOutsideSynchronisationAsync("John Smyth");

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(unrelated.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(unrelated));

        var realUpdates = SyncRepo.PendingExports.Values.Count(pe => pe.ConnectedSystemId == ctx.Target.Id && pe.ChangeType == PendingExportChangeType.Update);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.EqualTo(1), "arrange: the run's review stages the new name for John's account");
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates), "the preview proposes the update the review stages");
            Assert.That(preview.Counts.ExportScopeReviewed, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The provisioning Create has been exported and confirmed: each provisioned account is a normal object holding the
    /// values it was sent, with nothing left queued.
    /// </summary>
    private void ConfirmProvisionedAccounts(ConnectedSystem target)
    {
        foreach (var account in SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == target.Id))
        {
            foreach (var change in SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == account.Id).SelectMany(pe => pe.AttributeValueChanges))
            {
                account.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(), ConnectedSystemObject = account, AttributeId = change.AttributeId, Attribute = change.Attribute,
                    StringValue = change.StringValue, GuidValue = change.GuidValue
                });
            }

            account.Status = ConnectedSystemObjectStatus.Normal;
        }

        SyncRepo.ClearAllPendingExports();
    }

    /// <summary>
    /// Changes John's display name through the application layer, as the portal does. The harness holds synchronised
    /// Metaverse Objects in the sync store by reference, so the edit's own save is routed to a no-op.
    /// </summary>
    private async Task RenameJohnOutsideSynchronisationAsync(string newName)
    {
        Jim = new JIM.Application.JimApplication(MetaverseSaveBypassingRepositoryProxy.Create(Repository), syncRepository: SyncRepo);
        var john = SyncRepo.MetaverseObjects.Values.Single();
        var name = john.AttributeValues.Single(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName);
        var removal = name.CopyFor(john);
        name.StringValue = newName;
        await Jim.Metaverse.UpdateMetaverseObjectAsync(john, additions: [name], removals: [removal]);
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
