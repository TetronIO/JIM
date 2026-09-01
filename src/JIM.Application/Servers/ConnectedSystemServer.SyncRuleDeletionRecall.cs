// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The executor for a queued <see cref="DeleteSyncRuleWorkerTask"/> (#1537): withdraws the Synchronisation
/// Rule's contributed Metaverse attribute values by provenance, re-electing surviving contributors and
/// staging Pending Exports through the normal model, then deletes the rule via the ordinary delete path as
/// its final step. See <see cref="ExecuteSyncRuleDeletionRecallAsync"/>.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Executes a queued Synchronisation Rule deletion recall (#1537), on the worker: selects every Metaverse
    /// Object holding values the rule contributed (by provenance, which is why this runs BEFORE the deletion
    /// severs it), and per object stages the values for removal, re-elects any surviving contributor (the
    /// attribute is handed over rather than blanked), stages resulting Pending Exports for mapped target
    /// systems, and records one Run Profile Execution Item. Work is batched with progress reported on the
    /// task's Activity; the FINAL step deletes the rule via the existing delete path (configuration snapshot,
    /// priority reconciliation) as a child Activity.
    /// <para>
    /// Failure mode is fast and hard: an exception propagates to the worker's dispatch boundary (which fails
    /// the Activity), the rule survives, still disabled with its deletion-in-progress reason, completed
    /// objects are consistent, and the deletion can be retried.
    /// </para>
    /// </summary>
    /// <param name="task">The queued task, carrying the Synchronisation Rule id and the Activity to report to.</param>
    public async Task<SyncRuleDeletionRecallResult> ExecuteSyncRuleDeletionRecallAsync(DeleteSyncRuleWorkerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Activity == null)
            throw new InvalidDataException("ExecuteSyncRuleDeletionRecallAsync: the task must carry the Activity it was queued under.");

        var result = new SyncRuleDeletionRecallResult();
        var activity = task.Activity;

        // The rule must still exist: it was disabled, not deleted, when the task queued. Its full graph is
        // needed for the final delete's configuration snapshot.
        var syncRule = await GetSyncRuleAsync(task.SyncRuleId)
            ?? throw new InvalidDataException($"ExecuteSyncRuleDeletionRecallAsync: Synchronisation Rule {task.SyncRuleId} no longer exists; nothing to recall or delete.");

        if (task.RecallContributedValues)
        {
            // The recall support set: the priority contributor cache is built from every Synchronisation Rule
            // (the deleted rule is disabled, so it is dormant in the cache and cannot be re-elected), the
            // export cache drives Pending Export staging, and the scope tells the re-election core that only
            // the deleted rule's own contribution is ineligible (other rules of the same system are
            // legitimate survivors here, unlike the obsoletion path).
            var allSyncRules = await Application.SyncRepo.GetAllSyncRulesAsync();
            var priorityContext = new AttributePriorityContext(allSyncRules, honourNullAssertions: true);
            var syncEngine = new SyncEngine();
            var expressionEvaluator = new DynamicExpressoEvaluator();
            var exportEvaluationCache = await Application.ExportEvaluation.BuildExportEvaluationCacheAsync(allSyncRules);
            var recallScope = ContributorRecallScope.ForDeletedSyncRule(task.SyncRuleId);

            var affectedMvoIds = await Application.SyncRepo.GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(task.SyncRuleId);
            activity.ObjectsToProcess = affectedMvoIds.Count;
            activity.ObjectsProcessed = 0;
            await Application.Repository.Activity.UpdateActivityAsync(activity);

            result = await RecallSyncRuleContributedValuesAsync(
                task.SyncRuleId,
                recallScope,
                priorityContext,
                syncEngine,
                expressionEvaluator,
                exportEvaluationCache,
                activity,
                reElectedDetailMessage: $"Synchronisation Rule '{syncRule.Name}' is being deleted; a surviving contributor was re-elected for the recalled attribute value(s).",
                clearedDetailMessage: $"Synchronisation Rule '{syncRule.Name}' is being deleted; the recalled attribute value(s) had no remaining contributor and were cleared.",
                trackActivityProgress: true);
        }

        // FINAL step: delete the rule via the existing delete path (configuration snapshot, priority
        // reconciliation), as a child Activity of the recall so the history reads as one action. Any change
        // reason was recorded on the task's Activity at queue time. The initiator triad survives principal
        // deletion; a task queued without one (internal callers) is attributed to the system.
        var deleteActivity = new Activity
        {
            TargetName = syncRule.Name,
            TargetContext = syncRule.ConnectedSystem?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Delete,
            // The Connected System id is what keeps a rule deletion attributable once the rule itself is gone.
            ConnectedSystemId = syncRule.ConnectedSystemId,
            ParentActivityId = activity.Id
        };
        var initiatorType = task.InitiatedByType == ActivityInitiatorType.NotSet
            ? ActivityInitiatorType.System
            : task.InitiatedByType;
        await Application.Activities.CreateActivityWithTriadAsync(deleteActivity, initiatorType, task.InitiatedById, task.InitiatedByName);
        await DeleteSyncRuleCoreAsync(syncRule, deleteActivity, changeReason: null);

        // Complete the task's Activity with summary statistics (the worker's dispatch boundary owns the
        // completion call itself, exactly as it does for the other queued task types).
        activity.Message = $"Recalled {result.ValuesRecalled:N0} attribute value(s) across {result.MetaverseObjectsProcessed:N0} " +
            $"Metaverse Object(s): {result.AttributesReElected:N0} re-elected to a surviving contributor, " +
            $"{result.AttributesCleared:N0} cleared (no remaining contributor); {result.PendingExportsStaged:N0} " +
            "Pending Export(s) staged. The Synchronisation Rule has been deleted.";

        Log.Information(
            "ExecuteSyncRuleDeletionRecallAsync: Synchronisation Rule {SyncRuleId}: {ObjectCount} Metaverse Object(s) processed, " +
            "{ValueCount} value(s) recalled, {ReElectedCount} attribute(s) re-elected, {ClearedCount} attribute(s) cleared, " +
            "{PendingExportCount} Pending Export(s) staged; rule deleted.",
            task.SyncRuleId, result.MetaverseObjectsProcessed, result.ValuesRecalled, result.AttributesReElected,
            result.AttributesCleared, result.PendingExportsStaged);

        return result;
    }

    /// <summary>
    /// Recalls every Metaverse attribute value the given Synchronisation Rule contributed, selected by
    /// intact provenance, re-electing surviving contributors under the caller's
    /// <see cref="ContributorRecallScope"/> and staging Pending Exports for mapped target systems through
    /// the normal model. Batched, with one Run Profile Execution Item per affected Metaverse Object. Shared
    /// by the rule-deletion recall (#1537) and the Connected System Synchronised Deprovisioning residue pass
    /// (#809), whose scopes and audit wording differ but whose mechanics are one implementation.
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule whose contributed values are recalled (by provenance).</param>
    /// <param name="recallScope">Whose contribution is ineligible for re-election and who may take over.</param>
    /// <param name="priorityContext">The attribute priority contributor cache (#91), built from all Synchronisation Rules.</param>
    /// <param name="syncEngine">The synchronisation decision engine, for the re-election re-flow and change application.</param>
    /// <param name="expressionEvaluator">The evaluator for expression-based mappings in the re-election re-flow.</param>
    /// <param name="exportEvaluationCache">The pre-built export evaluation cache driving Pending Export staging.</param>
    /// <param name="activity">The Activity the per-object results are recorded on.</param>
    /// <param name="reElectedDetailMessage">The outcome wording for values a surviving contributor took over.</param>
    /// <param name="clearedDetailMessage">The outcome wording for values cleared with no remaining contributor.</param>
    /// <param name="trackActivityProgress">Whether to advance the Activity's ObjectsProcessed counter per batch
    /// (true for the #1537 task, whose counters track Metaverse Objects; false for the deprovisioning residue
    /// pass, whose counters track Connected System Objects).</param>
    /// <param name="skipMetaverseObjectsPendingDeletion">Whether to leave Metaverse Objects already marked for
    /// deferred deletion untouched (true for the deprovisioning residue pass: their single-source values were
    /// deliberately frozen for the grace window by the per-object pass, and housekeeping owns their removal).</param>
    /// <param name="affectedMetaverseObjectIds">The candidate Metaverse Object ids, when the caller has
    /// already selected them (the stranded-value sweep's join-absence selector, #1549); null re-selects via
    /// GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync as before.</param>
    /// <param name="remainingImportSourceEvaluator">When supplied AND the scope is not a deliberate
    /// withdrawal, gates each object's recall on the #1570 last-known-state preservation check: an object
    /// with no remaining enabled import source for its type keeps its values instead of being recalled. Null
    /// (the #1537/#809 callers) preserves today's behaviour: the gate is never consulted.</param>
    /// <param name="preservedDetailMessage">The outcome wording for values preserved by the gate above.</param>
    internal async Task<SyncRuleDeletionRecallResult> RecallSyncRuleContributedValuesAsync(
        int syncRuleId,
        ContributorRecallScope recallScope,
        AttributePriorityContext priorityContext,
        SyncEngine syncEngine,
        JIM.Models.Interfaces.IExpressionEvaluator expressionEvaluator,
        ExportEvaluationCache exportEvaluationCache,
        Activity activity,
        string reElectedDetailMessage,
        string clearedDetailMessage,
        bool trackActivityProgress,
        bool skipMetaverseObjectsPendingDeletion = false,
        IReadOnlyList<Guid>? affectedMetaverseObjectIds = null,
        RemainingImportSourceEvaluator? remainingImportSourceEvaluator = null,
        string? preservedDetailMessage = null)
    {
        const int batchSize = 500;
        var result = new SyncRuleDeletionRecallResult();
        var survivorObjectTypes = new List<Models.Staging.ConnectedSystemObjectType>();
        var affectedMvoIds = affectedMetaverseObjectIds
            ?? await Application.SyncRepo.GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(syncRuleId);

        foreach (var batch in affectedMvoIds.Chunk(batchSize))
        {
            // Tracked load: the re-election path hydrates survivors as tracked entities in this same
            // context, and persisting a no-tracking graph beside them throws an identity conflict on
            // shared principals (each object's MetaverseObjectType). Found at runtime; the in-memory
            // test provider always tracks and cannot catch it.
            var metaverseObjects = await Application.SyncRepo.GetMetaverseObjectsByIdsForUpdateAsync(batch);
            await Application.ExportEvaluation.RefreshExportEvaluationCacheForPageAsync(exportEvaluationCache, batch);

            var changedMvos = new List<MetaverseObject>();
            var removedValueIds = new List<Guid>();
            var stagedPendingExports = new List<PendingExport>();
            var executionItems = new List<ActivityRunProfileExecutionItem>();

            // A Metaverse Object marked for deferred deletion keeps its values for the grace window;
            // recalling them here would undo the per-object pass's deliberate freeze.
            foreach (var mvo in metaverseObjects
                         .Where(mvo => !skipMetaverseObjectsPendingDeletion || mvo.LastConnectorDisconnectedDate == null))
            {
                // Select this object's recalled values by intact provenance. An empty set means the
                // provenance moved since the id query ran (a concurrent re-election); nothing to do.
                var recalledValues = mvo.AttributeValues
                    .Where(av => av.ContributedBySyncRuleId == syncRuleId)
                    .ToList();
                if (recalledValues.Count == 0)
                    continue;

                // The #1570 last-known-state preservation gate: only consulted for a disappearance scope
                // (never a deliberate withdrawal, where the administrator's consequences were already
                // surfaced), and only when the caller supplied an evaluator. An object with no remaining
                // joined system carrying an enabled import Synchronisation Rule for its type keeps its
                // values as-is; recalling them would blank a live target account or feed an expression-based
                // mapping with nulls.
                if (remainingImportSourceEvaluator != null && !recallScope.IsDeliberateWithdrawal && mvo.Type != null)
                {
                    var remainingConnectedSystemIds = await Application.SyncRepo.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(mvo.Id);
                    if (!await remainingImportSourceEvaluator.AnyImportSourceRemainsAsync(remainingConnectedSystemIds, mvo.Type.Id))
                    {
                        executionItems.Add(BuildPreservedExecutionItem(mvo, preservedDetailMessage, recalledValues.Count));
                        result.MetaverseObjectsPreserved++;
                        result.ValuesPreserved += recalledValues.Count;
                        continue;
                    }
                }

                mvo.PendingAttributeValueRemovals.AddRange(recalledValues);

                // Re-elect any surviving contributor for the recalled attributes: with the recalled rule's
                // values marked for removal, re-flowing the survivors through the normal attribute-flow
                // gate elects the highest-priority survivor; attributes with no other contributor gain
                // nothing and are genuinely cleared.
                await ContributorReElectionService.ReElectSurvivingContributorsAsync(
                    mvo, recalledValues, recallScope, priorityContext, syncEngine, Application.SyncRepo,
                    (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
                    survivorObjectTypes, expressionEvaluator);

                // Capture pending changes BEFORE applying (which clears the pending lists), additions
                // first: export evaluation stages a single-valued attribute's FIRST matching changed
                // value, so a re-elected survivor's addition must precede the recalled removal or the
                // target would be staged with the stale value.
                var additions = mvo.PendingAttributeValueAdditions.ToList();
                var removals = mvo.PendingAttributeValueRemovals.ToList();
                var changedAttributes = additions.Concat(removals).ToList();
                var removedAttributes = removals.ToHashSet();
                var clearedAttributeCount = ContributorReElectionService.GetClearedAttributeIds(mvo, additions, removals).Count;

                syncEngine.ApplyPendingAttributeChanges(mvo);
                changedMvos.Add(mvo);
                removedValueIds.AddRange(removals.Where(av => av.Id != Guid.Empty).Select(av => av.Id));

                // Stage the resulting export changes for mapped target systems. Recall semantics: a
                // recall updates existing target objects and never provisions new ones; ordinary
                // synchronisation remains the provisioning path.
                var exportEvaluation = await Application.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
                    mvo, changedAttributes, exportEvaluationCache, deferSave: true,
                    removedAttributes: removedAttributes, existingPendingExports: stagedPendingExports,
                    recallSemantics: true);
                // The evaluation can return an export it merged into rather than a new one; only genuinely
                // new instances join the batch's staging list.
                var newlyStagedExports = exportEvaluation.PendingExports
                    .Where(pendingExport => !stagedPendingExports.Contains(pendingExport))
                    .ToList();
                stagedPendingExports.AddRange(newlyStagedExports);

                executionItems.Add(BuildRecallExecutionItem(mvo, reElectedDetailMessage, clearedDetailMessage, additions.Count, clearedAttributeCount));

                result.MetaverseObjectsProcessed++;
                result.ValuesRecalled += recalledValues.Count;
                result.AttributesReElected += additions.Count;
                result.AttributesCleared += clearedAttributeCount;
            }

            // Persist the batch: apply the attribute changes, explicitly delete the recalled value rows
            // (the objects were loaded untracked, so nothing else would), then stage the Pending Exports
            // with the same delete-then-create pattern the sync flush uses (prevents unique-index
            // collisions on ConnectedSystemObjectId; pre-existing exports were merged into these instances).
            await Application.SyncRepo.UpdateMetaverseObjectsAsync(changedMvos);
            if (removedValueIds.Count > 0)
                await Application.SyncRepo.DeleteMetaverseObjectAttributeValuesByIdsAsync(removedValueIds);

            if (stagedPendingExports.Count > 0)
            {
                var targetCsoIds = stagedPendingExports
                    .Where(pe => pe.ConnectedSystemObjectId.HasValue)
                    .Select(pe => pe.ConnectedSystemObjectId!.Value)
                    .Distinct()
                    .ToList();
                if (targetCsoIds.Count > 0)
                    await Application.SyncRepo.DeletePendingExportsByConnectedSystemObjectIdsAsync(targetCsoIds);
                await Application.SyncRepo.CreatePendingExportsAsync(stagedPendingExports);
                result.PendingExportsStaged += stagedPendingExports.Count;
            }

            await Application.Activities.AddRunProfileExecutionItemsAsync(activity, executionItems);

            if (trackActivityProgress)
            {
                activity.ObjectsProcessed += batch.Length;
                await Application.Repository.Activity.UpdateActivityAsync(activity);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the per-object Run Profile Execution Item for a recall: an Attribute Flow change whose outcomes
    /// record what was re-elected and what was cleared, so the decision's history names every object it touched.
    /// </summary>
    private static ActivityRunProfileExecutionItem BuildRecallExecutionItem(
        MetaverseObject mvo, string reElectedDetailMessage, string clearedDetailMessage, int reElectedCount, int clearedCount)
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.AttributeFlow,
            DisplayNameSnapshot = mvo.NameOrId,
            ObjectTypeSnapshot = mvo.Type?.Name
        };

        var ordinal = 0;
        if (reElectedCount > 0)
        {
            item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                TargetEntityId = mvo.Id,
                TargetEntityDescription = mvo.NameOrId,
                DetailMessage = reElectedDetailMessage,
                DetailCount = reElectedCount,
                Ordinal = ordinal++
            });
        }
        if (clearedCount > 0)
        {
            item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
                TargetEntityId = mvo.Id,
                TargetEntityDescription = mvo.NameOrId,
                DetailMessage = clearedDetailMessage,
                DetailCount = clearedCount,
                Ordinal = ordinal
            });
        }

        item.OutcomeSummary = string.Join(",", item.SyncOutcomes
            .GroupBy(o => o.OutcomeType)
            .Select(g => $"{g.Key}:{g.Count()}"));
        return item;
    }

    /// <summary>
    /// Builds the per-object Run Profile Execution Item for the #1570 preservation gate: a single
    /// ValuesPreserved outcome, so an object whose recall was skipped still gets a durable, auditable record
    /// naming why its values were left alone.
    /// </summary>
    private static ActivityRunProfileExecutionItem BuildPreservedExecutionItem(
        MetaverseObject mvo, string? preservedDetailMessage, int preservedCount)
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.AttributeFlow,
            DisplayNameSnapshot = mvo.NameOrId,
            ObjectTypeSnapshot = mvo.Type?.Name
        };

        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved,
            TargetEntityId = mvo.Id,
            TargetEntityDescription = mvo.NameOrId,
            DetailMessage = preservedDetailMessage,
            DetailCount = preservedCount,
            Ordinal = 0
        });

        item.OutcomeSummary = $"{ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved}:1";
        return item;
    }
}
