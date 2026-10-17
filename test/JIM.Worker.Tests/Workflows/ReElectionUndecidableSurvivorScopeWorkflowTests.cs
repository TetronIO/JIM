// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2336: when HR's contribution to a Metaverse Object is withdrawn, the surviving Training contributor is re-elected
/// only if it is in its import rule's scope. Here that scope cannot be decided: a schema refresh changed the type of the
/// attribute Training's rule scopes on and no import has read it since (#2329), which behaves exactly as a pattern that
/// runs past its time limit (#2250). The contract is the #2250 one: that one object errors as
/// <see cref="ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed"/>, and nothing acts on a guess, so the
/// Metaverse Object is left exactly as it was (no Deletion Rule, no recall, no re-election) and the withdrawal is retried
/// once a Full Import has read the value.
///
/// Topology (shared with Synchronised Deprovisioning): HR projects and contributes Description at priority 1; Training
/// joins on EmployeeId and contributes Description at priority 2; a target exports Description.
/// </summary>
[TestFixture]
public class ReElectionUndecidableSurvivorScopeWorkflowTests : SynchronisedDeprovisioningTestBase
{
    // -----------------------------------------------------------------------------------------------------------------
    // A joined object deleted at source (the obsoletion path)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeltaSync_ObsoleteObjectWhoseSurvivorsScopeIsUndecidable_RecordsScopingCriterionFailedAndLeavesTheMetaverseObjectAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkObsoleteAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        var item = ItemFor(activity, hrCso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError), Is.Empty,
                "an undecidable scope is an expected, typed error, not an unhandled one");
            Assert.That(item?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(item?.ErrorMessage, Does.Contain("Training Import").And.Contain("Number").And.Contain("Full Import"));
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
                "HR's Description is neither recalled nor replaced");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId)?.StringValue, Is.EqualTo("John Smith"),
                "nor is HR's sole contribution recalled");
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(hrCso.Id), Is.True,
                "the leaving object is kept, so its deletion is processed once the scope can be decided");
            Assert.That(SyncRepo.ConnectedSystemObjects[hrCso.Id].MetaverseObjectId, Is.EqualTo(mvoId), "and keeps its join");
            Assert.That(SyncRepo.ConnectedSystemObjects[hrCso.Id].Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(PendingExportsFor(ctx.Target), Is.Empty, "nothing is exported for a change that was not made");
        }
    }

    [Test]
    public async Task DeltaSync_ObsoleteObjectWhoseSurvivorsScopeIsUndecidable_DoesNotScheduleTheDeletionAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync(deletionGracePeriod: TimeSpan.FromDays(7));
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkObsoleteAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemFor(activity, hrCso.Id)?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(mvo.DeletionEligibleDate, Is.Null, "the Deletion Rule is not applied while the outcome is undecided");
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null);
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription));
        }
    }

    [Test]
    public async Task DeltaSync_ObsoleteObjectWhoseSurvivorsScopeIsUndecidable_DoesNotDeleteTheMetaverseObjectImmediatelyAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync(deletionGracePeriod: TimeSpan.Zero);
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkObsoleteAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemFor(activity, hrCso.Id)?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(SyncRepo.MetaverseObjects.ContainsKey(mvoId), Is.True,
                "nothing acts on the object until every surviving contributor's scope is decided, the Deletion Rule included");
        }
    }

    [Test]
    public async Task FullSync_AfterAFullImportReadsTheSurvivorsValue_ProcessesTheHeldDeletionAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;
        await MarkObsoleteAsync(hrCso);
        await RunDeltaSyncAsync(ctx.Hr);

        // A Full Import of Training reads the attribute as its new type.
        var held = TrainingCso(ctx).AttributeValues.Single(av => av.Attribute.Name == "TrainingDescription");
        held.IntValue = null;
        held.StringValue = TrainingDescription;
        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(hrCso.Id), Is.False, "the held deletion is now processed");
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(TrainingDescription),
                "and Training, now decidably in scope, takes Description over");
        }
    }

    [Test]
    public async Task DeltaSync_ObsoleteObjectWhoseSurvivorIsDecidablyInScope_ReElectsTheSurvivorAsBeforeAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        ScopeTraining(ctx, SearchComparisonType.Equals, TrainingDescription);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkObsoleteAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(TrainingDescription));
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(hrCso.Id), Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_ObsoleteObjectWhoseSurvivorIsDecidablyOutOfScope_ClearsTheAttributeAsBeforeAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        ScopeTraining(ctx, SearchComparisonType.Equals, "Some other description");
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkObsoleteAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId), Is.Null,
                "an out-of-scope survivor is not re-elected, so the recalled Description is cleared");
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(hrCso.Id), Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_ObsoleteObjectWithNoSurvivor_IsProcessedAsBeforeAsync()
    {
        var ctx = await SetUpSoleContributorWithExportTargetAsync();
        await RunFullSyncAsync(ctx.Hr);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        await MarkObsoleteAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(SyncRepo.ConnectedSystemObjects.ContainsKey(hrCso.Id), Is.False);
            Assert.That(SyncRepo.MetaverseObjects[mvoId].ConnectedSystemObjects.Any(c => c.Id == hrCso.Id), Is.False, "the join is broken");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // A joined object leaving its import rule's scope, and a contributor withdrawing a value in place
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task FullSync_ObjectLeavingScopeWhoseSurvivorsScopeIsUndecidable_RecordsTheErrorAndLeavesTheMetaverseObjectAsync()
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        ConfigureDeletionRule(ctx, TimeSpan.FromDays(7));
        var hrDescription = ctx.HrImportRule.ConnectedSystemObjectType!.Attributes.First(a => a.Name == "HrDescription");
        AddCriterion(ctx.HrImportRule, hrDescription, SearchComparisonType.Equals, HrDescription);
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        hrCso.AttributeValues.Single(av => av.AttributeId == hrDescription.Id).StringValue = "No longer in scope";
        await ModifyCsoAsync(hrCso);
        var activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemFor(activity, hrCso.Id)?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(ItemFor(activity, hrCso.Id)?.ErrorMessage, Does.Contain("Training Import"));
            Assert.That(SyncRepo.ConnectedSystemObjects[hrCso.Id].MetaverseObjectId, Is.EqualTo(mvoId), "the join is kept");
            Assert.That(mvo.DeletionEligibleDate, Is.Null, "the Deletion Rule is not applied while the outcome is undecided");
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null);
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription));
        }
    }

    [Test]
    public async Task DeltaSync_ValueWithdrawnInPlaceWhileSurvivorsScopeIsUndecidable_RecordsTheErrorAndLeavesTheMetaverseObjectAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;

        hrCso.AttributeValues.Remove(hrCso.AttributeValues.Single(av => av.Attribute.Name == "HrDescription"));
        await ModifyCsoAsync(hrCso);
        var activity = await RunDeltaSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemFor(activity, hrCso.Id)?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed));
            Assert.That(GetAttributeValue(SyncRepo.MetaverseObjects[mvoId], ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Synchronisation Rule deletion recall
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_SurvivorsScopeIsUndecidable_LeavesTheObjectAndKeepsTheRuleAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var mvoId = HrCso(ctx).MetaverseObjectId!.Value;

        var (task, activity) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        Assert.ThrowsAsync<SyncRuleDeletionIncompleteException>(() => Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task));

        var mvo = SyncRepo.MetaverseObjects[mvoId];
        var errors = DbContext.ActivityRunProfileExecutionItems
            .Where(rpei => rpei.ActivityId == activity.Id && rpei.ErrorType == ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed)
            .ToList();
        var rule = await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(mvo, ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
                "the object is left as it was: HR's Description neither recalled nor replaced");
            Assert.That(GetAttributeValue(mvo, ctx.MvDisplayNameAttributeId)?.StringValue, Is.EqualTo("John Smith"));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors.Single().ErrorMessage, Does.Contain("Training Import").And.Contain("Number"));
            Assert.That(rule, Is.Not.Null, "the rule is not deleted while its values could not all be recalled");
            Assert.That(rule!.Enabled, Is.False);
        }
    }

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_SurvivorDecidablyInScope_RecallsAndDeletesTheRuleAsBeforeAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        ScopeTraining(ctx, SearchComparisonType.Equals, TrainingDescription);
        var mvoId = HrCso(ctx).MetaverseObjectId!.Value;

        var (task, _) = await DisableRuleAndBuildTaskAsync(ctx.HrImportRule);
        await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(SyncRepo.MetaverseObjects[mvoId], ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(TrainingDescription));
            Assert.That(await DbContext.SyncRules.FindAsync(ctx.HrImportRule.Id), Is.Null);
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Recall by provenance: Synchronised Deprovisioning's residue pass and the stranded-value sweep
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_ResidueObjectWhoseSurvivorsScopeIsUndecidable_LeavesItAndKeepsTheSystemAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var mvoId = StrandHrContribution(ctx);

        var (task, activity) = await FenceSystemAndBuildTaskAsync(ctx.Hr);
        Assert.ThrowsAsync<SynchronisedDeprovisioningIncompleteException>(() => Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task));

        var errors = DbContext.ActivityRunProfileExecutionItems
            .Where(rpei => rpei.ActivityId == activity.Id && rpei.ErrorType == ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(SyncRepo.MetaverseObjects[mvoId], ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription),
                "the residue object keeps HR's Description, provenance and all");
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors.Single().ErrorMessage, Does.Contain("Training Import"));
            Assert.That(await DbContext.ConnectedSystems.FindAsync(ctx.Hr.Id), Is.Not.Null,
                "the Connected System is not deleted while its provenance is still needed");
        }
    }

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_StrandedObjectWhoseSurvivorsScopeIsUndecidable_LeavesItAndStaysArmedAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var mvoId = StrandHrContribution(ctx);
        var hr = await ReloadEntityAsync(ctx.Hr);
        hr.StrandedValueSweepArmedAt = DateTime.UtcNow.AddMinutes(-10);
        hr.LastSuccessfulFullImportCompletedAt = DateTime.UtcNow.AddMinutes(-5);
        var profile = await CreateRunProfileAsync(hr.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(hr.Id, profile, ConnectedSystemRunType.FullSynchronisation);

        await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(hr, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetAttributeValue(SyncRepo.MetaverseObjects[mvoId], ctx.MvDescriptionAttributeId)?.StringValue, Is.EqualTo(HrDescription));
            Assert.That(activity.RunProfileExecutionItems.Select(i => i.ErrorType),
                Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed }));
            Assert.That(hr.StrandedValueSweepArmedAt, Is.Not.Null,
                "the sweep stays armed, since nothing else would ever recall the value it left stranded");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Previews
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task StreamFullSyncPreviewAsync_ObsoleteObjectWhoseSurvivorsScopeIsUndecidable_ReportsTheErrorTheRunRecordsAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var hrCso = HrCso(ctx);
        await MarkObsoleteAsync(hrCso);

        var items = new List<JIM.Models.Transactional.FullSyncPreviewItem>();
        await foreach (var streamed in Jim.SyncPreview.StreamFullSyncPreviewAsync(ctx.Hr.Id))
            items.Add(streamed);
        var preview = items.Single(i => i.ConnectedSystemObjectId == hrCso.Id).Preview!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors.Select(e => e.Code), Is.EqualTo(new[] { JIM.Models.Transactional.SyncPreviewMessageCode.ScopingCriterionFailed }));
            Assert.That(preview.Errors.Single().Detail, Does.Contain("Training Import"));
            Assert.That(preview.OutcomeTree, Is.Empty, "the run records no outcome for an object it leaves as it was");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_SurvivorsScopeIsUndecidable_ReportsTheObjectAsOneThatWouldFailAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var mvoId = HrCso(ctx).MetaverseObjectId!.Value;

        var deltas = new List<JIM.Models.Preview.PreviewDelta>();
        await foreach (var delta in Jim.ConnectedSystems.PreviewSynchronisedDeprovisioningAsync(ctx.Hr.Id))
            deltas.Add(delta);

        var failures = deltas.Where(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures.Single().MetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(failures.Single().NewValue, Does.Contain("Training Import"));
            Assert.That(deltas.Where(d => d.TransitionType is ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor
                    or ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue), Is.Empty,
                "nothing is proposed for an object the run leaves as it was");
        }
    }

    [Test]
    public async Task PreviewSynchronisedDeprovisioningAsync_ResidueObjectWhoseSurvivorsScopeIsUndecidable_ReportsItAsOneThatWouldFailAsync()
    {
        var ctx = await SetUpSyncedTwoContributorsAsync();
        MakeTrainingScopeUndecidable(ctx);
        var mvoId = StrandHrContribution(ctx);

        var deltas = new List<JIM.Models.Preview.PreviewDelta>();
        await foreach (var delta in Jim.ConnectedSystems.PreviewSynchronisedDeprovisioningAsync(ctx.Hr.Id))
            deltas.Add(delta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Where(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail).Select(d => d.MetaverseObjectId),
                Is.EqualTo(new Guid?[] { mvoId }));
            Assert.That(deltas.Where(d => d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor), Is.Empty);
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// HR's object is gone without having been obsoleted (an earlier Connector Space clear), so its Metaverse Object holds
    /// HR's values by provenance alone, with Training still joined: only recall by provenance can reach it.
    /// </summary>
    private Guid StrandHrContribution(DeprovisioningContext ctx)
    {
        var hrCso = HrCso(ctx);
        var mvoId = hrCso.MetaverseObjectId!.Value;
        SyncRepo.RemoveConnectedSystemObject(hrCso);
        SyncRepo.MetaverseObjects[mvoId].ConnectedSystemObjects.Remove(hrCso);
        return mvoId;
    }

    private async Task<DeprovisioningContext> SetUpSyncedTwoContributorsAsync(TimeSpan? deletionGracePeriod = null)
    {
        var ctx = await SetUpTwoContributorsWithExportTargetAsync();
        if (deletionGracePeriod.HasValue)
            ConfigureDeletionRule(ctx, deletionGracePeriod.Value);
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training!);
        SimulateTargetExportExecuted(ctx, "John Smith", HrDescription);
        Assert.That(GetAttributeValue(SyncRepo.MetaverseObjects.Values.Single(), ctx.MvDescriptionAttributeId)?.StringValue,
            Is.EqualTo(HrDescription), "arrange: HR (priority 1) wins Description");
        return ctx;
    }

    /// <summary>HR's disconnection deletes the Metaverse Object, after the grace period given (zero: immediately).</summary>
    private void ConfigureDeletionRule(DeprovisioningContext ctx, TimeSpan gracePeriod)
    {
        var mvType = ctx.HrImportRule.MetaverseObjectType!;
        mvType.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        mvType.DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect;
        mvType.DeletionTriggerConnectedSystemIds = [ctx.Hr.Id];
        mvType.DeletionGracePeriod = gracePeriod == TimeSpan.Zero ? null : gracePeriod;
        DbContext.SaveChanges();
    }

    private SyncRule TrainingRule(DeprovisioningContext ctx) => SyncRepo.SyncRules.Values.Single(r => r.Id == ctx.TrainingImportRuleId);

    private ConnectedSystemObject HrCso(DeprovisioningContext ctx) =>
        SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Hr.Id);

    private ConnectedSystemObject TrainingCso(DeprovisioningContext ctx) =>
        SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Training!.Id);

    /// <summary>Training's rule is scoped on a value a schema refresh moved to Text, which Training still holds as a Number.</summary>
    private void MakeTrainingScopeUndecidable(DeprovisioningContext ctx)
    {
        ScopeTraining(ctx, SearchComparisonType.HasValue, null);
        var held = TrainingCso(ctx).AttributeValues.Single(av => av.Attribute.Name == "TrainingDescription");
        held.StringValue = null;
        held.IntValue = 42;
    }

    private void ScopeTraining(DeprovisioningContext ctx, SearchComparisonType op, string? value)
    {
        var rule = TrainingRule(ctx);
        AddCriterion(rule, rule.ConnectedSystemObjectType!.Attributes.First(a => a.Name == "TrainingDescription"), op, value);
    }

    private static void AddCriterion(SyncRule rule, ConnectedSystemObjectTypeAttribute attribute, SearchComparisonType op, string? value) =>
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = attribute,
                    ConnectedSystemAttributeId = attribute.Id,
                    ComparisonType = op,
                    StringValue = value,
                    CaseSensitive = true
                }
            ]
        });

    private static Task MarkObsoleteAsync(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    private List<JIM.Models.Transactional.PendingExport> PendingExportsFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == system.Id).ToList();

    private List<ActivityRunProfileExecutionItem> ItemsOf(Activity activity) =>
        SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .ToList();

    private ActivityRunProfileExecutionItem? ItemFor(Activity activity, Guid csoId) =>
        ItemsOf(activity).SingleOrDefault(i => i.ConnectedSystemObjectId == csoId);

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    private async Task<Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    /// <summary>Disables the rule with the deletion-in-progress reason, as the queue step does, and builds its task.</summary>
    private async Task<(DeleteSyncRuleWorkerTask Task, Activity Activity)> DisableRuleAndBuildTaskAsync(SyncRule rule)
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        rule.Enabled = false;
        rule.DisabledReason = "Deletion in progress: contributed attribute values are being recalled.";
        DbContext.Entry(rule).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        await DbContext.SaveChangesAsync();

        var activity = new Activity
        {
            TargetName = rule.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.RecallAttributeValues,
            Status = ActivityStatus.InProgress,
            ConnectedSystemId = rule.ConnectedSystemId,
            Executed = DateTime.UtcNow
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        var task = new DeleteSyncRuleWorkerTask(rule.Id, recallContributedValues: true)
        {
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test Administrator",
            Activity = activity
        };
        return (task, activity);
    }
}
