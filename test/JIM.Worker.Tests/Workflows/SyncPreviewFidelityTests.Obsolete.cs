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
/// Whole-system preview fidelity for objects an import has marked obsolete (#1530). A Full Synchronisation tears each
/// one down before processing anything else: it disconnects the object from its Metaverse Object, puts that object to
/// its type's Deletion Rule, recalls what the departing object contributed, and deprovisions downstream when the
/// Metaverse Object is deleted. These are the most destructive things a Full Synchronisation routinely does, so the
/// preview of one must propose them as the run makes them, not skip the objects that cause them.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [Test]
    public async Task PreviewFullSyncAsync_OfASourceWhoseObjectIsObsolete_ProposesTheDeprovisioningTheRunMakesAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false);

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        var realDeletes = DeletesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realDeletes, Is.EqualTo(1), "arrange: the run deletes John Smith and deprovisions his Active Directory account");
            Assert.That(preview.Counts.ObjectsToDelete, Is.EqualTo(realDeletes), "the preview proposes the deprovisioning the teardown causes");
        }
    }

    [Test]
    public async Task StreamFullSyncPreviewAsync_OfASourceWhoseObjectIsObsolete_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false);

        var obsolete = await ObsoleteItemAsync(ctx);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(obsolete.Preview, Is.Not.Null, "the teardown is evaluated, not skipped");
            Assert.That(DescribeTree(obsolete.Preview!.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the preview's tree for the obsolete object is the tree the run records for it");
        }
    }

    [Test]
    public async Task StreamFullSyncPreviewAsync_OfAnObsoleteObjectWhoseMetaverseObjectIsScheduledForDeletion_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false,
            gracePeriod: TimeSpan.FromDays(30));

        var obsolete = await ObsoleteItemAsync(ctx);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletesStagedFor(ctx.Target), Is.Zero, "arrange: a scheduled deletion deprovisions nothing yet");
            Assert.That(DescribeTree(obsolete.Preview!.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
            Assert.That(obsolete.Preview!.Outbound.ObjectsToDelete, Is.Zero);
        }
    }

    [Test]
    public async Task PreviewFullSyncAsync_OfAnObsoleteObjectWhoseValuesAreRecalled_ProposesTheTargetUpdatesTheRunMakesAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, adImports: true);

        var preview = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        var realUpdates = UpdatesStagedFor(ctx.Target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(realUpdates, Is.EqualTo(1), "arrange: Active Directory keeps John Smith, and is sent the recall of his display name");
            Assert.That(DeletesStagedFor(ctx.Target), Is.Zero);
            Assert.That(preview.Counts.ObjectsToUpdate, Is.EqualTo(realUpdates), "the preview proposes the update the recall causes");
        }
    }

    [Test]
    public async Task StreamFullSyncPreviewAsync_OfAnObsoleteObjectWhoseValuesAreRecalled_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, adImports: true);

        var obsolete = await ObsoleteItemAsync(ctx);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        Assert.That(DescribeTree(obsolete.Preview!.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
            "the recall's exports are proposed without a node the run does not record");
    }

    [Test]
    public async Task PreviewFullSyncAsync_OfAnObsoleteObject_ChangesNothingAsync()
    {
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, adImports: false);
        var sourceObject = await ReloadEntityAsync(ctx.SourceObject);
        var mvoId = sourceObject.MetaverseObjectId!.Value;

        await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);

        sourceObject = await ReloadEntityAsync(ctx.SourceObject);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceObject.MetaverseObjectId, Is.EqualTo(mvoId), "the preview does not break the join");
            Assert.That(sourceObject.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId), Is.True, "the preview deletes nothing");
            Assert.That(SyncRepo.MetaverseObjects[mvoId].AttributeValues, Is.Not.Empty, "the preview recalls nothing");
            Assert.That(SyncRepo.PendingExports, Is.Empty, "the preview stages nothing");
        }
    }

    private sealed record ObsoleteContext(ConnectedSystem Source, ConnectedSystem Target, ConnectedSystemObject SourceObject);

    private async Task<FullSyncPreviewItem> ObsoleteItemAsync(ObsoleteContext ctx)
    {
        var items = new List<FullSyncPreviewItem>();
        await foreach (var item in Jim.SyncPreview.StreamFullSyncPreviewAsync(ctx.Source.Id))
            items.Add(item);

        return items.Single(i => i.Kind == FullSyncPreviewItemKind.Obsolete);
    }

    /// <summary>
    /// HR projects John Smith and provisions his Active Directory account, which has since been exported and confirmed.
    /// John has then been deleted in HR, and HR's import has marked his object obsolete. Active Directory imports too
    /// when <paramref name="adImports"/>, joining on employee ID, so it remains an import source after HR leaves.
    /// </summary>
    private async Task<ObsoleteContext> SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule deletionRule, bool adImports,
        TimeSpan? gracePeriod = null)
    {
        var source = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(source.Id, "User");
        var target = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(target.Id, "user");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync("Person", deletionRule, gracePeriod ?? TimeSpan.Zero,
            triggerConnectedSystemIds: [source.Id]);
        var mvDisplayName = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hrImport = await CreateImportSyncRuleWithDisplayNameFlowAsync(source, sourceType, mvType);
        var hrEmployeeId = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        hrImport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImport, TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeId, ConnectedSystemAttributeId = hrEmployeeId.Id } }
        });

        if (adImports)
        {
            var adImport = await CreateImportSyncRuleAsync(target.Id, targetType, mvType, "AD Import", enableProjection: false);
            var adEmployeeId = targetType.Attributes.First(a => a.Name == "EmployeeId");
            adImport.ObjectMatchingRules.Add(new ObjectMatchingRule
            {
                SyncRule = adImport, SyncRuleId = adImport.Id, Order = 0, CaseSensitive = true,
                TargetMetaverseAttribute = mvEmployeeId, TargetMetaverseAttributeId = mvEmployeeId.Id,
                Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = adEmployeeId, ConnectedSystemAttributeId = adEmployeeId.Id }]
            });
        }

        var adExport = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "AD Export",
            deprovisionAction: OutboundDeprovisionAction.Delete);
        var targetDisplayName = targetType.Attributes.First(a => a.Name == "DisplayName");
        var targetEmployeeId = targetType.Attributes.First(a => a.Name == "EmployeeId");
        FlowToAttribute(adExport, targetDisplayName, mvDisplayName);
        FlowToAttribute(adExport, targetEmployeeId, mvEmployeeId);
        await DbContext.SaveChangesAsync();

        var sourceObject = await CreateCsoAsync(source.Id, sourceType, "John Smith", "EMP001");
        await RunFullSyncAsync(source);

        // The provisioned account has been exported and confirmed: it exists in Active Directory, holding what was sent.
        sourceObject = await ReloadEntityAsync(sourceObject);
        var mvoId = sourceObject.MetaverseObjectId ?? throw new InvalidOperationException("arrange: the first sync projects John Smith");
        var targetObject = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.MetaverseObjectId == mvoId && c.Id != sourceObject.Id);
        targetObject.Status = ConnectedSystemObjectStatus.Normal;
        targetObject.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetObject.AttributeValues.RemoveAll(av => av.AttributeId == targetDisplayName.Id || av.AttributeId == targetEmployeeId.Id);
        targetObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = targetObject, Attribute = targetDisplayName, AttributeId = targetDisplayName.Id,
            StringValue = "John Smith"
        });
        targetObject.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = targetObject, Attribute = targetEmployeeId, AttributeId = targetEmployeeId.Id,
            StringValue = "EMP001"
        });
        SyncRepo.ClearAllPendingExports();

        sourceObject.Status = ConnectedSystemObjectStatus.Obsolete;
        sourceObject.LastUpdated = DateTime.UtcNow;

        return new ObsoleteContext(source, target, sourceObject);
    }

    private int DeletesStagedFor(ConnectedSystem system) => SyncRepo.PendingExports.Values
        .Count(pe => pe.ConnectedSystemId == system.Id && pe.ChangeType == PendingExportChangeType.Delete);
}
