// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for the priority-aware next-contributor recall fallback (#91, Phase 2c). When the winning
/// contributor's CSO is obsoleted and its value recalled, a still-joined lower-priority contributor must be
/// re-elected (its previously-suppressed value flows into the MVO) rather than the attribute being blanked.
/// The same fallback applies when the winning contributor's CSO instead falls out of its import Synchronisation
/// Rule's scope (<c>HandleCsoOutOfScopeAsync</c>, <c>InboundOutOfScopeAction.Disconnect</c>): scope exit is
/// another way a Connected System Object stops contributing, so it must hand over exactly like obsoletion does.
///
/// Topology: HR Source (priority 1, recall enabled) and Training Source (priority 2) both contribute Description
/// to the same Person MVO. HR wins while joined; the priority gate suppresses Training's value. When HR's CSO is
/// obsoleted (or falls out of scope), the surviving Training contribution must take over.
/// </summary>
[TestFixture]
public class AttributePriorityRecallWorkflowTests : WorkflowTestBase
{
    private const string HrDescription = "HR Description";
    private const string TrainingDescription = "Training Description";
    private const string SharedEmployeeId = "EMP001";

    [Test]
    public async Task Recall_HigherPriorityContributorObsoleted_ReElectsLowerPrioritySurvivorAsync()
    {
        var ctx = await SetUpTwoContributorsToDescriptionAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);

        // HR projects and wins Description; Training joins but its lower-priority Description is suppressed.
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(HrDescription), "HR (priority 1) should win Description while joined");

        // HR's CSO is obsoleted: its Description is recalled, and the surviving Training contribution must be re-elected.
        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);

        // The re-election must not corrupt the run: no unhandled errors (the recall path swallows exceptions into
        // UnhandledError RPEIs, so a silent throw would otherwise leave the recall half-applied).
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "re-election must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "Description must not be blanked: the surviving Training contributor (priority 2) should be re-elected");
        Assert.That(reElected!.StringValue, Is.EqualTo(TrainingDescription),
            "the recalled Description should be replaced by the surviving Training value, not cleared");
        Assert.That(reElected.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "the re-elected value must carry the surviving Training rule's provenance");
    }

    [Test]
    public async Task Recall_ClearedAttributesWithNoSurvivor_EmitNoContributorSyncOutcomeAsync()
    {
        var ctx = await SetUpTwoContributorsToDescriptionAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        // HR leaves: Description is re-elected to the surviving Training source (a change of value, not a clear),
        // while the single-source DisplayName and EmployeeId have no surviving contributor and are genuinely
        // cleared. Those clears must surface as a NoContributor outcome on the disconnection RPEI.
        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);

        var noContributorOutcomes = deltaActivity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)
            .ToList();
        Assert.That(noContributorOutcomes, Is.Not.Empty,
            "recalled attributes with no surviving contributor should emit a NoContributor sync outcome");
        Assert.That(noContributorOutcomes.Sum(o => o.DetailCount ?? 0), Is.EqualTo(2),
            "DisplayName and EmployeeId are cleared with no survivor; the re-elected Description must not be counted");
    }

    [Test]
    public async Task Recall_ReferenceAttributeWithSurvivor_ReElectsSurvivingReferenceAsync()
    {
        var ctx = await SetUpTwoContributorsToManagerReferenceAsync();

        // HR projects everyone and wins Manager (priority 1): John's Manager references Mary's MVO.
        await RunFullSyncAsync(ctx.Hr);
        var johnMvo = ctx.HrJohnCso.MetaverseObject;
        var maryMvoId = ctx.HrMaryCso.MetaverseObject?.Id;
        var bobMvoId = ctx.HrBobCso.MetaverseObject?.Id;
        Assert.That(johnMvo, Is.Not.Null, "HR full sync should project John's MVO");
        Assert.That(maryMvoId, Is.Not.Null, "HR full sync should project Mary's MVO");
        Assert.That(bobMvoId, Is.Not.Null, "HR full sync should project Bob's MVO");

        // Training joins on EmployeeId; its lower-priority Manager reference (to Bob's MVO) is suppressed.
        await RunFullSyncAsync(ctx.Training);
        var manager = johnMvo!.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvManagerAttributeId && !av.NullValue);
        Assert.That(GetReferencedMvoId(manager), Is.EqualTo(maryMvoId),
            "HR (priority 1) should win the Manager reference while joined");

        // HR's John CSO is obsoleted: the recalled Manager reference must be re-elected to the surviving
        // Training contribution in the recall pass, not left blank until a later sync of the Training system.
        await MarkCsoObsoleteAsync(ctx.HrJohnCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "reference re-election must complete without unhandled errors");

        johnMvo = SyncRepo.MetaverseObjects[johnMvo.Id];
        var reElected = johnMvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvManagerAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "the Manager reference must not be blanked: the surviving Training contributor should be re-elected in the recall pass");
        Assert.That(GetReferencedMvoId(reElected), Is.EqualTo(bobMvoId),
            "the recalled Manager reference should be replaced by the surviving Training reference (Bob's MVO)");
        Assert.That(reElected!.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "the re-elected reference must carry the surviving Training rule's provenance");
    }

    [Test]
    public async Task Recall_SurvivorContributesIdenticalValue_ValueRetainedWithSurvivorProvenanceAsync()
    {
        // Both contributors agree on the Description value. When the winning HR contributor leaves, the value
        // must survive the recall (handed to the identical Training contribution with its provenance), not be
        // cleared because the survivor's value looked "already present" while the leaver's row awaited removal.
        const string sharedDescription = "Shared Description";
        var ctx = await SetUpTwoContributorsToDescriptionAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2,
            hrDescriptionValue: sharedDescription, trainingDescriptionValue: sharedDescription);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "identical-value re-election must complete without unhandled errors");

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "Description must not be blanked when the surviving contributor holds the identical value");
        Assert.That(reElected!.StringValue, Is.EqualTo(sharedDescription),
            "the identical surviving value should be retained");
        Assert.That(reElected.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "provenance must hand over to the surviving Training rule");
    }

    [Test]
    public async Task Recall_WithSurvivingContributor_StagesSurvivorValueExportAsync()
    {
        // A downstream target holds the HR Description value. When HR's CSO is obsoleted and the Description is
        // handed to the surviving Training contributor, the target must receive an Update Pending Export carrying
        // the survivor's value in the same run. This guards the export side of the handover: the MVO re-election
        // alone is not enough if export evaluation stages the leaver's stale value (which no-net-change detection
        // then filters out, leaving the target stale forever).
        var ctx = await SetUpTwoContributorsToDescriptionAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);
        var target = await AddDescriptionExportTargetAsync(ctx.MvType, ctx.MvDisplayNameAttributeId, ctx.MvDescriptionAttributeId);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);
        SimulateTargetExportExecuted(target, displayName: "John Smith", description: HrDescription);

        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "obsoletion recall export evaluation must complete without unhandled errors");

        var targetExports = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObject?.ConnectedSystemId == target.Target.Id)
            .ToList();
        Assert.That(targetExports, Has.Count.EqualTo(1),
            "the obsoletion recall must stage one Update Pending Export for the target system");

        var descriptionChange = targetExports[0].AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == target.TargetDescriptionAttribute.Id);
        Assert.That(descriptionChange, Is.Not.Null,
            "the export must carry the handed-over Description");
        Assert.That(descriptionChange!.StringValue, Is.EqualTo(TrainingDescription),
            "the export must carry the surviving Training value (a change of value, not a clear or the leaver's stale value)");
    }

    [Test]
    public async Task Recall_HigherPriorityObsoletedUnderGracePeriod_ReElectsSurvivorButFreezesSingleSourceAttributesAsync()
    {
        // A deletion grace period is configured. Today the grace period freezes all recall, so a multi-source
        // attribute would be left stale at the departed source's value. With per-attribute re-election: the
        // multi-source Description is handed to the surviving Training source, while single-source HR attributes
        // (no survivor) are still frozen (preserved) for the grace window rather than cleared.
        var ctx = await SetUpTwoContributorsToDescriptionAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2, deletionGracePeriod: TimeSpan.FromDays(7));

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        await MarkCsoObsoleteAsync(ctx.HrCso);
        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "re-election under a grace period must complete without unhandled errors");

        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(TrainingDescription),
            "the multi-source Description must be re-elected to the surviving Training source even under a grace period");

        var displayName = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDisplayNameAttributeId && !av.NullValue);
        Assert.That(displayName, Is.Not.Null,
            "single-source HR DisplayName has no surviving contributor, so it is frozen (preserved) for the grace window, not cleared");
    }

    [Test]
    public async Task Withdrawal_WinnerWithdrawsValueInPlace_ReElectsSurvivorInSameRunAsync()
    {
        var ctx = await SetUpTwoContributorsToDescriptionAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);

        // HR projects and wins Description; Training joins but its lower-priority Description is suppressed.
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(HrDescription), "HR (priority 1) should win Description while joined");

        // In-place withdrawal: HR stays joined but stops supplying its Description value (no "Null is a value"
        // assertion). The suppressed Training survivor must be re-elected in this same run, not left blank until
        // the Training system next synchronises.
        var hrDescriptionValue = ctx.HrCso.AttributeValues.Single(av => av.Attribute?.Name == "HrDescription");
        ctx.HrCso.AttributeValues.Remove(hrDescriptionValue);
        await ModifyCsoAsync(ctx.HrCso);

        var deltaActivity = await RunDeltaSyncReturningActivityAsync(ctx.Hr);
        Assert.That(deltaActivity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "withdrawal re-election must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects.Values.Single();
        var reElected = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "Description must not be blanked: the surviving Training contributor (priority 2) should be re-elected in the same run");
        Assert.That(reElected!.StringValue, Is.EqualTo(TrainingDescription),
            "the withdrawn Description should be handed to the surviving Training value, not cleared");
        Assert.That(reElected.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "the re-elected value must carry the surviving Training rule's provenance");

        var noContributorDetailCount = deltaActivity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Where(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)
            .Sum(o => o.DetailCount ?? 0);
        Assert.That(noContributorDetailCount, Is.EqualTo(0),
            "a re-elected attribute must not be reported as cleared with no contributor");
    }

    [Test]
    public async Task ScopeExit_WithSurvivingContributor_ReElectsSurvivorAsync()
    {
        var ctx = await SetUpTwoContributorsToDescriptionWithScopingAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);

        // HR projects and wins Description while in scope; Training joins but its lower-priority Description is suppressed.
        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(HrDescription), "HR (priority 1) should win Description while in scope");

        // HR's CSO falls out of its import Synchronisation Rule's scope: its Description is recalled via
        // HandleCsoOutOfScopeAsync, and the surviving Training contribution must be re-elected in the same run,
        // exactly as ProcessObsoleteConnectedSystemObjectAsync already does for CSO obsoletion.
        await PushHrOutOfScopeAsync(ctx);
        var fullSync2Activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(fullSync2Activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "scope-exit re-election must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects[mvo.Id];
        var reElected = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(reElected, Is.Not.Null,
            "Description must not be blanked: the surviving Training contributor (priority 2) should be re-elected");
        Assert.That(reElected!.StringValue, Is.EqualTo(TrainingDescription),
            "the recalled Description should be replaced by the surviving Training value, not cleared");
        Assert.That(reElected.ContributedBySyncRuleId, Is.EqualTo(ctx.TrainingImportRuleId),
            "the re-elected value must carry the surviving Training rule's provenance");
    }

    [Test]
    public async Task ScopeExit_NoSurvivingContributor_ClearsAttributeAsync()
    {
        // Training does not contribute Description here, so there is no surviving contributor to re-elect;
        // this guards against the re-election fix over-correcting a genuine clear into a stale value.
        var ctx = await SetUpTwoContributorsToDescriptionWithScopingAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2, trainingContributesDescription: false);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        var mvo = SyncRepo.MetaverseObjects.Values.Single();
        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(HrDescription), "HR should be the sole Description contributor while in scope");

        await PushHrOutOfScopeAsync(ctx);
        var fullSync2Activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(fullSync2Activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "scope-exit with no surviving contributor must complete without unhandled errors");

        mvo = SyncRepo.MetaverseObjects[mvo.Id];
        var cleared = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(cleared, Is.Null,
            "Description has no surviving contributor, so it must be genuinely cleared, not left stale at the departed HR value");
    }

    [Test]
    public async Task ScopeExit_GracePeriodWithSurvivor_HandsOverAndFreezesOnlySoleSourceAsync()
    {
        // A deletion grace period is configured. The multi-source Description must still be handed to the
        // surviving Training source (a safe change-of-value, not a clear), while the single-source HR DisplayName
        // (no survivor) is frozen (preserved) for the grace window rather than cleared - mirroring the obsoletion
        // path's grace refinement.
        var ctx = await SetUpTwoContributorsToDescriptionWithScopingAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2, deletionGracePeriod: TimeSpan.FromDays(7));

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        await PushHrOutOfScopeAsync(ctx);
        var fullSync2Activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(fullSync2Activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "scope-exit re-election under a grace period must complete without unhandled errors");

        var mvo = SyncRepo.MetaverseObjects.Values.Single();

        var description = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDescriptionAttributeId && !av.NullValue);
        Assert.That(description?.StringValue, Is.EqualTo(TrainingDescription),
            "the multi-source Description must be re-elected to the surviving Training source even under a grace period");

        var displayName = mvo.AttributeValues.SingleOrDefault(av => av.AttributeId == ctx.MvDisplayNameAttributeId && !av.NullValue);
        Assert.That(displayName, Is.Not.Null,
            "single-source HR DisplayName has no surviving contributor, so it is frozen (preserved) for the grace window, not cleared");
    }

    [Test]
    public async Task ScopeExit_WithSurvivingContributor_QueuesExportEvaluationAsync()
    {
        // A downstream target holds the HR Description value. When HR falls out of scope and the Description is
        // handed to the surviving Training contributor, the target must receive an Update Pending Export carrying
        // the new value in the same run - mirroring what obsoletion recall already stages via export evaluation.
        var ctx = await SetUpTwoContributorsToDescriptionWithScopingAsync(hrDescriptionPriority: 1, trainingDescriptionPriority: 2);
        var target = await AddDescriptionExportTargetAsync(ctx.MvType, ctx.MvDisplayNameAttributeId, ctx.MvDescriptionAttributeId);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);
        SimulateTargetExportExecuted(target, displayName: "John Smith", description: HrDescription);

        await PushHrOutOfScopeAsync(ctx);
        var fullSync2Activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(fullSync2Activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "scope-exit export evaluation must complete without unhandled errors");

        var targetExports = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObject?.ConnectedSystemId == target.Target.Id)
            .ToList();
        Assert.That(targetExports, Has.Count.EqualTo(1),
            "the scope-exit recall must queue export evaluation, staging one Update Pending Export for the target system");
        Assert.That(targetExports[0].ChangeType, Is.EqualTo(JIM.Models.Transactional.PendingExportChangeType.Update),
            "the staged Pending Export should be an Update");

        var descriptionChange = targetExports[0].AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == target.TargetDescriptionAttribute.Id);
        Assert.That(descriptionChange, Is.Not.Null,
            "the export must carry the handed-over Description");
        Assert.That(descriptionChange!.StringValue, Is.EqualTo(TrainingDescription),
            "the export must carry the surviving Training value (a change of value, not a clear)");
    }

    [Test]
    public async Task ScopeExit_NoSurvivingContributor_QueuesExportEvaluationAsRemovalAsync()
    {
        // No surviving Description contributor: the clear must still reach export evaluation, so the target
        // receives an Update Pending Export null-clearing the stale value rather than keeping it forever.
        var ctx = await SetUpTwoContributorsToDescriptionWithScopingAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2, trainingContributesDescription: false);
        var target = await AddDescriptionExportTargetAsync(ctx.MvType, ctx.MvDisplayNameAttributeId, ctx.MvDescriptionAttributeId);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);
        SimulateTargetExportExecuted(target, displayName: "John Smith", description: HrDescription);

        await PushHrOutOfScopeAsync(ctx);
        var fullSync2Activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        Assert.That(fullSync2Activity.RunProfileExecutionItems.Any(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError),
            Is.False, "scope-exit export evaluation must complete without unhandled errors");

        var targetExports = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObject?.ConnectedSystemId == target.Target.Id)
            .ToList();
        Assert.That(targetExports, Has.Count.EqualTo(1),
            "the scope-exit clear must queue export evaluation, staging one Update Pending Export for the target system");

        var descriptionChange = targetExports[0].AttributeValueChanges
            .FirstOrDefault(c => c.AttributeId == target.TargetDescriptionAttribute.Id);
        Assert.That(descriptionChange, Is.Not.Null,
            "the export must include the cleared Description");
        Assert.That(descriptionChange!.StringValue, Is.Null,
            "the cleared Description must reach the target as a null-clearing change (a removal, not a stale value)");
    }

    [Test]
    public async Task ScopeExit_NoSurvivingContributor_EmitsNoContributorOutcomeAsync()
    {
        // Scope exit clears HR's DisplayName, EmployeeId and Description with no surviving contributor for any of
        // them. In Detailed tracking those clears must surface as a NoContributor child outcome on the
        // DisconnectedOutOfScope RPEI, exactly as the obsoletion path reports them on its Disconnected RPEI.
        var ctx = await SetUpTwoContributorsToDescriptionWithScopingAsync(
            hrDescriptionPriority: 1, trainingDescriptionPriority: 2, trainingContributesDescription: false);

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Training);

        await PushHrOutOfScopeAsync(ctx);
        var fullSync2Activity = await RunFullSyncReturningActivityAsync(ctx.Hr);

        var outOfScopeRpei = fullSync2Activity.RunProfileExecutionItems
            .SingleOrDefault(r => r.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope);
        Assert.That(outOfScopeRpei, Is.Not.Null, "the scope exit should produce a DisconnectedOutOfScope RPEI");

        var rootOutcome = outOfScopeRpei!.SyncOutcomes.SingleOrDefault(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome, Is.Not.Null, "the RPEI should have a root sync outcome");

        var noContributorOutcome = rootOutcome!.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor);
        Assert.That(noContributorOutcome, Is.Not.Null,
            "attributes cleared at scope exit with no surviving contributor should emit a NoContributor outcome");
        Assert.That(noContributorOutcome!.DetailCount, Is.EqualTo(3),
            "DisplayName, EmployeeId and Description are all cleared with no survivor");
    }

    private sealed record TwoContributorContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        ConnectedSystemObject HrCso,
        MetaverseObjectType MvType,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId,
        int TrainingImportRuleId);

    private sealed record TwoContributorScopedContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        ConnectedSystemObject HrCso,
        ConnectedSystemObjectTypeAttribute HrScopeFlagAttribute,
        MetaverseObjectType MvType,
        int MvDescriptionAttributeId,
        int MvDisplayNameAttributeId,
        int TrainingImportRuleId);

    private sealed record ExportTargetContext(
        ConnectedSystem Target,
        ConnectedSystemObjectTypeAttribute TargetDescriptionAttribute,
        ConnectedSystemObjectTypeAttribute TargetDisplayNameAttribute);

    private sealed record ReferenceContributorContext(
        ConnectedSystem Hr,
        ConnectedSystem Training,
        ConnectedSystemObject HrJohnCso,
        ConnectedSystemObject HrMaryCso,
        ConnectedSystemObject HrBobCso,
        int MvManagerAttributeId,
        int TrainingImportRuleId);

    /// <summary>
    /// Resolves the Metaverse Object a reference attribute value points at, via the scalar FK or navigation.
    /// </summary>
    private static Guid? GetReferencedMvoId(MetaverseObjectAttributeValue? av)
        => av?.ReferenceValueId ?? av?.ReferenceValue?.Id;

    /// <summary>
    /// Builds a reference-attribute topology: HR (projects, contributes DisplayName/EmployeeId and a Manager
    /// reference at priority 1, recall enabled) and Training (joins on EmployeeId, contributes Manager from its
    /// Mentor reference at priority 2). HR's John references Mary; Training's John record references Bob's
    /// Training record, whose person is also projected by HR so both references resolve to Metaverse Objects.
    /// </summary>
    private async Task<ReferenceContributorContext> SetUpTwoContributorsToManagerReferenceAsync()
    {
        // --- HR source: primary, recall enabled, with a Manager reference attribute ---
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrManagerAttr = new ConnectedSystemObjectTypeAttribute { Name = "Manager", Type = AttributeDataType.Reference, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrManagerAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        // --- Training source: supplemental, joins on EmployeeId, with a Mentor reference attribute ---
        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingMentorAttr = new ConnectedSystemObjectTypeAttribute { Name = "Mentor", Type = AttributeDataType.Reference, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingMentorAttr });
        trainingType.RemoveContributedAttributesOnObsoletion = true;

        // --- MV type with a single-valued Manager reference attribute ---
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvManagerAttr = new MetaverseAttribute
        {
            Name = "Manager",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvManagerAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvManagerAttr);

        // --- HR import rule: DisplayName, EmployeeId, Manager@1 ---
        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvManagerAttr, hrManagerAttr, priority: 1));
        await DbContext.SaveChangesAsync();

        // --- Training import rule: Manager <- Mentor@2, join on EmployeeId ---
        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvManagerAttr, trainingMentorAttr, priority: 2));
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new() { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id }
            }
        });
        await DbContext.SaveChangesAsync();

        // --- HR CSOs: Mary and Bob (reference targets), John (subject, Manager -> Mary) ---
        var hrMaryCso = await CreateCsoAsync(hrSystem.Id, hrType, "Mary Manager", "EMP002");
        var hrBobCso = await CreateCsoAsync(hrSystem.Id, hrType, "Bob Mentor", "EMP003");
        var hrJohnCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrJohnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrManagerAttr.Id, Attribute = hrManagerAttr,
            ReferenceValueId = hrMaryCso.Id, ReferenceValue = hrMaryCso, ConnectedSystemObject = hrJohnCso
        });

        // --- Training CSOs: Bob's record (reference target, joins Bob's MVO), John's record (Mentor -> Bob) ---
        var trainingBobCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", "EMP003");
        var trainingJohnCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingJohnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingMentorAttr.Id, Attribute = trainingMentorAttr,
            ReferenceValueId = trainingBobCso.Id, ReferenceValue = trainingBobCso, ConnectedSystemObject = trainingJohnCso
        });

        return new ReferenceContributorContext(hrSystem, trainingSystem, hrJohnCso, hrMaryCso, hrBobCso, mvManagerAttr.Id, trainingImportRule.Id);
    }

    /// <summary>
    /// Builds the topology: HR (projects, contributes DisplayName/EmployeeId/Description at
    /// <paramref name="hrDescriptionPriority"/>, recall enabled) and Training (joins on EmployeeId, contributes
    /// Description at <paramref name="trainingDescriptionPriority"/>). Both source CSOs share an EmployeeId.
    /// </summary>
    private async Task<TwoContributorContext> SetUpTwoContributorsToDescriptionAsync(
        int hrDescriptionPriority, int trainingDescriptionPriority, TimeSpan? deletionGracePeriod = null,
        string hrDescriptionValue = HrDescription, string trainingDescriptionValue = TrainingDescription)
    {
        // --- HR source: primary, recall enabled ---
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        // --- Training source: supplemental, joins on EmployeeId ---
        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingDescriptionAttr });
        trainingType.RemoveContributedAttributesOnObsoletion = true;

        // --- MV type with Description (and an optional deletion grace period) ---
        var mvType = await CreateMvObjectTypeAsync("Person");
        if (deletionGracePeriod.HasValue)
            mvType.DeletionGracePeriod = deletionGracePeriod.Value;
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        // --- HR import rule: DisplayName, EmployeeId, Description@hrDescriptionPriority ---
        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, hrDescriptionPriority));
        await DbContext.SaveChangesAsync();

        // --- Training import rule: Description@trainingDescriptionPriority, join on EmployeeId ---
        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvDescriptionAttr, trainingDescriptionAttr, trainingDescriptionPriority));
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new() { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id }
            }
        });
        await DbContext.SaveChangesAsync();

        // --- Source CSOs (sharing an EmployeeId so Training joins HR's projected MVO) ---
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = hrDescriptionValue, ConnectedSystemObject = hrCso
        });

        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = trainingDescriptionAttr.Id, Attribute = trainingDescriptionAttr, StringValue = trainingDescriptionValue, ConnectedSystemObject = trainingCso
        });

        return new TwoContributorContext(hrSystem, trainingSystem, hrCso, mvType, mvDescriptionAttr.Id, mvDisplayNameAttr.Id, trainingImportRule.Id);
    }

    /// <summary>
    /// Builds the same two-contributor Description topology as <see cref="SetUpTwoContributorsToDescriptionAsync"/>,
    /// but adds a dedicated <c>ScopeFlag</c> Connected System Object attribute on HR and an
    /// <see cref="SyncRuleScopingCriteriaGroup"/> on HR's import rule requiring it to equal "InScope". This lets a
    /// test drive HR out of scope (via <see cref="PushHrOutOfScopeAsync"/>) without disturbing the EmployeeId join
    /// key both systems share, exercising <c>HandleCsoOutOfScopeAsync</c> rather than CSO obsoletion.
    /// </summary>
    private async Task<TwoContributorScopedContext> SetUpTwoContributorsToDescriptionWithScopingAsync(
        int hrDescriptionPriority, int trainingDescriptionPriority, TimeSpan? deletionGracePeriod = null,
        bool trainingContributesDescription = true)
    {
        // --- HR source: primary, recall enabled, scoped via ScopeFlag ---
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var hrExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var hrDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var hrEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var hrDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "HrDescription", Type = AttributeDataType.Text, Selected = true };
        var hrScopeFlagAttr = new ConnectedSystemObjectTypeAttribute { Name = "ScopeFlag", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            new List<ConnectedSystemObjectTypeAttribute> { hrExternalIdAttr, hrDisplayNameAttr, hrEmployeeIdAttr, hrDescriptionAttr, hrScopeFlagAttr });
        hrType.RemoveContributedAttributesOnObsoletion = true;

        // --- Training source: supplemental, joins on EmployeeId ---
        var trainingSystem = await CreateConnectedSystemAsync("Training Source");
        trainingSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var trainingExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var trainingEmployeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var trainingDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "TrainingDescription", Type = AttributeDataType.Text, Selected = true };
        var trainingType = await CreateCsoTypeAsync(trainingSystem.Id, "TrainingRecord",
            new List<ConnectedSystemObjectTypeAttribute> { trainingExternalIdAttr, trainingEmployeeIdAttr, trainingDescriptionAttr });
        trainingType.RemoveContributedAttributesOnObsoletion = true;

        // --- MV type with Description (and an optional deletion grace period) ---
        var mvType = await CreateMvObjectTypeAsync("Person");
        if (deletionGracePeriod.HasValue)
            mvType.DeletionGracePeriod = deletionGracePeriod.Value;
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDescriptionAttr = new MetaverseAttribute
        {
            Name = "Description",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(mvDescriptionAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvDescriptionAttr);

        // --- HR import rule: DisplayName, EmployeeId, Description@hrDescriptionPriority, scoped on ScopeFlag == "InScope" ---
        var hrImportRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDisplayNameAttr, hrDisplayNameAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvEmployeeIdAttr, hrEmployeeIdAttr));
        hrImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(hrImportRule, mvDescriptionAttr, hrDescriptionAttr, hrDescriptionPriority));
        hrImportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = hrScopeFlagAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "InScope",
                    CaseSensitive = true
                }
            }
        });
        await DbContext.SaveChangesAsync();

        // --- Training import rule: Description@trainingDescriptionPriority (optional), join on EmployeeId ---
        var trainingImportRule = await CreateImportSyncRuleAsync(trainingSystem.Id, trainingType, mvType, "Training Import", enableProjection: false);
        if (trainingContributesDescription)
            trainingImportRule.AttributeFlowRules.Add(BuildDirectImportMapping(trainingImportRule, mvDescriptionAttr, trainingDescriptionAttr, trainingDescriptionPriority));
        trainingImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = trainingImportRule,
            SyncRuleId = trainingImportRule.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new() { Order = 0, ConnectedSystemAttribute = trainingEmployeeIdAttr, ConnectedSystemAttributeId = trainingEmployeeIdAttr.Id }
            }
        });
        await DbContext.SaveChangesAsync();

        // --- Source CSOs (sharing an EmployeeId so Training joins HR's projected MVO); HR starts in scope ---
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", SharedEmployeeId);
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrDescriptionAttr.Id, Attribute = hrDescriptionAttr, StringValue = HrDescription, ConnectedSystemObject = hrCso
        });
        hrCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = hrScopeFlagAttr.Id, Attribute = hrScopeFlagAttr, StringValue = "InScope", ConnectedSystemObject = hrCso
        });

        var trainingCso = await CreateCsoAsync(trainingSystem.Id, trainingType, "unused", SharedEmployeeId);
        if (trainingContributesDescription)
        {
            trainingCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = trainingDescriptionAttr.Id, Attribute = trainingDescriptionAttr, StringValue = TrainingDescription, ConnectedSystemObject = trainingCso
            });
        }

        return new TwoContributorScopedContext(hrSystem, trainingSystem, hrCso, hrScopeFlagAttr, mvType, mvDescriptionAttr.Id, mvDisplayNameAttr.Id, trainingImportRule.Id);
    }

    /// <summary>
    /// Adds a downstream target Connected System to a two-contributor topology: an export Synchronisation Rule
    /// (with provisioning) mapping the Metaverse DisplayName and Description to matching target attributes, so
    /// tests can assert that a recall (obsoletion or scope exit) reaches export evaluation and stages Pending
    /// Exports for the target. Call before the first Full Sync so the target is provisioned when HR projects the
    /// Metaverse Object.
    /// </summary>
    private async Task<ExportTargetContext> AddDescriptionExportTargetAsync(
        MetaverseObjectType mvType, int mvDisplayNameAttributeId, int mvDescriptionAttributeId)
    {
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetExternalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var targetDisplayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var targetDescriptionAttr = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "TargetUser",
            new List<ConnectedSystemObjectTypeAttribute> { targetExternalIdAttr, targetDisplayNameAttr, targetDescriptionAttr });

        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Id == mvDisplayNameAttributeId);
        var mvDescriptionAttr = mvType.Attributes.First(a => a.Id == mvDescriptionAttributeId);

        var exportRule = new SyncRule
        {
            ConnectedSystemId = targetSystem.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetType.Id,
            ConnectedSystemObjectType = targetType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayNameAttr, MetaverseAttributeId = mvDisplayNameAttr.Id } }
        });
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDescriptionAttr,
            TargetConnectedSystemAttributeId = targetDescriptionAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDescriptionAttr, MetaverseAttributeId = mvDescriptionAttr.Id } }
        });

        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        return new ExportTargetContext(targetSystem, targetDescriptionAttr, targetDisplayNameAttr);
    }

    /// <summary>
    /// Simulates the provisioning export having been executed against the target Connected System: marks the
    /// provisioned target CSO Normal, writes the given executed attribute values onto it, and clears all Pending
    /// Exports so subsequent assertions only see exports staged by the scope-exit recall under test.
    /// </summary>
    private ConnectedSystemObject SimulateTargetExportExecuted(ExportTargetContext target, string displayName, string description)
    {
        var targetCso = SyncRepo.ConnectedSystemObjects.Values.First(c => c.ConnectedSystemId == target.Target.Id);
        targetCso.Status = ConnectedSystemObjectStatus.Normal;
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = target.TargetDisplayNameAttribute.Id,
            Attribute = target.TargetDisplayNameAttribute,
            StringValue = displayName,
            ConnectedSystemObject = targetCso
        });
        targetCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = target.TargetDescriptionAttribute.Id,
            Attribute = target.TargetDescriptionAttribute,
            StringValue = description,
            ConnectedSystemObject = targetCso
        });
        SyncRepo.ClearAllPendingExports();
        return targetCso;
    }

    /// <summary>
    /// Flips HR's <c>ScopeFlag</c> attribute value so it no longer satisfies its import rule's scoping criteria,
    /// then touches <c>LastUpdated</c> so the next Full Sync re-evaluates the CSO instead of skipping it as
    /// unchanged. Mirrors the mutate-then-resync pattern <c>NugatoryWorkOptimisationTests</c> uses to drive a scope exit.
    /// </summary>
    private static Task PushHrOutOfScopeAsync(TwoContributorScopedContext ctx)
    {
        var scopeFlagValue = ctx.HrCso.AttributeValues.Single(av => av.AttributeId == ctx.HrScopeFlagAttribute.Id);
        scopeFlagValue.StringValue = "OutOfScope";
        ctx.HrCso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a Full Sync for a Connected System and returns the resulting Activity, so the caller can inspect its
    /// Run Profile Execution Items (e.g. for the DisconnectedOutOfScope RPEI produced by a scope exit).
    /// </summary>
    private async Task<JIM.Models.Activities.Activity> RunFullSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private static SyncRuleMapping BuildDirectImportMapping(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
    {
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            Priority = priority,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    private async Task RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    private async Task<JIM.Models.Activities.Activity> RunDeltaSyncReturningActivityAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    private static Task MarkCsoObsoleteAsync(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
