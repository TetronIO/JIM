// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Search;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Preview fidelity for connection criteria (#2021). A join to an Object Type that an export rule's connection criterion
/// names moves that rule's scope with no attribute changing, and the run evaluates the identity's exports for it. The
/// preview reads connections from the database, which does not yet hold the join it is previewing, so it must add that
/// join itself and evaluate as the run does.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    [TestCase(false, TestName = "PreviewSyncForCsoAsync_JoinWithNoImportRuleBringsTheIdentityIntoAConnectionScopedRule_PredictsTheProvisioningTheRunMakesAsync")]
    [TestCase(true, TestName = "PreviewSyncForCsoAsync_JoinThroughAnImportRuleFlowingNothingBringsTheIdentityIntoAConnectionScopedRule_PredictsTheProvisioningTheRunMakesAsync")]
    public async Task PreviewSyncForCsoAsync_JoinBringsTheIdentityIntoAConnectionScopedRule_PredictsTheProvisioningTheRunMakesAsync(bool importRule)
    {
        // The directory account joins Jane with nothing flowing in; a badge system takes everyone with a directory account.
        ConnectedSystem badge = null!;
        var (target, targetCso, _) = await ArrangeDirectoryAccountMatchedByItsTypeAsync(simpleMode: true, importRule: importRule,
            configureBeforeSync: async (mvType, directoryType) =>
            {
                badge = await CreateConnectedSystemAsync("Badge");
                var badgeType = await CreateCsoTypeAsync(badge.Id, "Holder");
                var badgeExport = await CreateExportSyncRuleAsync(badge.Id, badgeType, mvType, "Badge Export");
                FlowToAttribute(badgeExport, badgeType.Attributes.First(a => a.Name == "EmployeeId"), mvType.Attributes.First(a => a.Name == "EmployeeId"));
                badgeExport.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
                {
                    Type = SearchGroupType.All,
                    Criteria = [new SyncRuleScopingCriteria { ConnectedObjectType = directoryType, ConnectedObjectTypeId = directoryType.Id, ComparisonType = SearchComparisonType.HasValue }]
                });
                await DbContext.SaveChangesAsync();
            });

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(target.Id, targetCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(target));

        var realTree = MapRealOutcomeTree(activity);
        Assert.That(DescribeTree(realTree), Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned)),
            "precondition: the join brings Jane into the badge rule's scope, so the run provisions her badge");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
                "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
            Assert.That(SyncRepo.PendingExports.Values.Count(pe => pe.ConnectedSystemId == badge.Id && pe.ChangeType == PendingExportChangeType.Create),
                Is.EqualTo(1), "precondition: the run staged the badge");
        }
    }

    [TestCase(true, TestName = "StreamFullSyncPreviewAsync_LeaverWithRecallLeavesAConnectionScopedRule_TreeMatchesTheRealSyncOutcomeTreeAsync")]
    [TestCase(false, TestName = "StreamFullSyncPreviewAsync_LeaverWithoutRecallLeavesAConnectionScopedRule_TreeMatchesTheRealSyncOutcomeTreeAsync")]
    public async Task StreamFullSyncPreviewAsync_LeaverLeavesAConnectionScopedRule_TreeMatchesTheRealSyncOutcomeTreeAsync(bool recall)
    {
        // The GAL leaver: John's HR account is obsolete and his Metaverse Object stays, so the only thing taking him out of
        // the directory rule's scope is the connection going. The run deletes his account, and the preview must say so.
        var ctx = await SetUpObsoleteSourceObjectAsync(MetaverseObjectDeletionRule.Manual, adImports: false,
            scopeExportToPeopleInHr: true, recallOnObsoletion: recall);

        var obsolete = await ObsoleteItemAsync(ctx);
        var whole = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletesStagedFor(ctx.Target), Is.EqualTo(1), "precondition: the run deletes John's directory account");
            Assert.That(whole.Counts.ObjectsToDelete, Is.EqualTo(1), "the preview proposes the deletion the run makes");
            Assert.That(obsolete.Preview, Is.Not.Null);
            Assert.That(DescribeTree(obsolete.Preview!.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the preview's tree for the leaver is the tree the run records for it");
        }
    }

    [TestCase(true, TestName = "PreviewSyncForCsoAsync_ScopeExitWithRecallBreaksTheConnectionAConnectionScopedRuleNeeds_PredictsTheDeletionTheRunMakesAsync")]
    [TestCase(false, TestName = "PreviewSyncForCsoAsync_ScopeExitWithoutRecallBreaksTheConnectionAConnectionScopedRuleNeeds_PredictsTheDeletionTheRunMakesAsync")]
    public async Task PreviewSyncForCsoAsync_ScopeExitBreaksTheConnectionAConnectionScopedRuleNeeds_PredictsTheDeletionTheRunMakesAsync(bool recall)
    {
        // John leaves the HR import rule's scope and is disconnected from HR; Active Directory keeps him alive, but its
        // export rule takes only people with an HR account, so his directory account is deleted.
        var ctx = await SetUpScopeExitWithRecallAsync(scopeExportToPeopleInHr: true, recall: recall);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Source.Id, ctx.SourceObject.Id);
        var whole = await Jim.SyncPreview.PreviewFullSyncAsync(ctx.Source.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Source));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletesStagedFor(ctx.Target), Is.EqualTo(1), "precondition: the run deletes John's directory account");
            Assert.That(whole.Counts.ObjectsToDelete, Is.EqualTo(1), "the preview proposes the deletion the run makes");
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))),
                "the preview's tree for the leaver is the tree the run records for it");
        }
    }
}
