// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The executor for a queued <see cref="DeleteConnectedSystemWorkerTask"/> whose mode is Synchronised
/// Deprovisioning (#809): processes every one of the system's Connected System Objects through the
/// obsoletion core extracted in Phase 1 (attribute recall, surviving-contributor re-election, Metaverse
/// Object deletion rules, Pending Export staging), runs a by-provenance residue pass per import
/// Synchronisation Rule, then deletes the system via the existing delete path as its final step. See
/// <see cref="ExecuteSynchronisedDeprovisioningAsync"/>.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Executes a queued Connected System Synchronised Deprovisioning run (#809), on the worker, in three
    /// passes:
    /// <list type="number">
    /// <item><b>Per-object pass</b>: batched over the system's Connected System Objects in ascending id
    /// order, each marked Obsolete and processed through
    /// <see cref="ConnectedSystemObjectObsoletionService"/> with the deleted-system recall scope, so
    /// attribute recall, surviving-contributor re-election, Metaverse Object deletion rules (grace-period
    /// identities marked, immediate ones deleted, with deletion-cascade and reference-recall Pending
    /// Exports staged) and downstream export staging all run exactly as a synchronisation disconnect would.
    /// One Run Profile Execution Item per object; Activity counters per batch.</item>
    /// <item><b>Residue pass</b>: per import Synchronisation Rule, remaining contributed values are
    /// recalled by provenance (values stranded with no backing Connected System Object, #1549's scenario),
    /// strictly BEFORE any rule is deleted: deletion's ON DELETE SET NULL severs the provenance the recall
    /// selects on.</item>
    /// <item><b>Final step</b>: the existing <see cref="ExecuteDeletionAsync"/> (tombstone snapshot,
    /// orphan marking, bulk delete), then the Activity message is set with summary statistics (the worker's
    /// dispatch boundary owns the completion call itself).</item>
    /// </list>
    /// <para>
    /// Resumability: a checkpoint (phase, last completed Connected System Object id, last completed
    /// Synchronisation Rule id) is persisted on the task row after each fully persisted batch. Per-object
    /// processing is designed for idempotence rather than exactly-once: an already-obsoleted or deleted
    /// object is a no-op, and export staging uses the delete-then-create pattern, so re-staging an export
    /// for an object processed just before a crash merges into (replaces) the previous staging rather than
    /// duplicating it.
    /// </para>
    /// <para>
    /// Failure mode is fast and hard: an exception propagates to the worker's dispatch boundary (which
    /// fails the Activity), the system survives fenced (Status stays Deleting so nothing synchronises a
    /// half-deprovisioned system), completed batches are consistent, and the run is retryable from the
    /// checkpoint.
    /// </para>
    /// </summary>
    /// <param name="task">The queued task, carrying the Connected System id, the mode flag, any resume
    /// checkpoint, and the Activity to report to.</param>
    public async Task<ConnectedSystemDeprovisioningResult> ExecuteSynchronisedDeprovisioningAsync(DeleteConnectedSystemWorkerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Activity == null)
            throw new InvalidDataException("ExecuteSynchronisedDeprovisioningAsync: the task must carry the Activity it was queued under.");
        if (!task.SynchronisedDeprovisioning)
            throw new InvalidDataException("ExecuteSynchronisedDeprovisioningAsync: the task is not a Synchronised Deprovisioning task.");

        const int batchSize = 500;
        var result = new ConnectedSystemDeprovisioningResult();
        var activity = task.Activity;

        // Verify the fence before touching anything: the system must exist and must have been marked
        // Deleting at queue time, or a concurrent synchronisation could interleave with the run.
        var connectedSystem = await Application.Repository.ConnectedSystems.GetConnectedSystemCoreAsync(task.ConnectedSystemId)
            ?? throw new InvalidDataException($"ExecuteSynchronisedDeprovisioningAsync: Connected System {task.ConnectedSystemId} no longer exists; nothing to deprovision.");
        if (connectedSystem.Status != ConnectedSystemStatus.Deleting)
            throw new InvalidDataException($"ExecuteSynchronisedDeprovisioningAsync: Connected System {task.ConnectedSystemId} is not fenced (Status={connectedSystem.Status}); refusing to run without the Deleting fence.");

        // The run's support set, mirroring the #1537 recall executor: the priority contributor cache is
        // built from every Synchronisation Rule, the export cache drives Pending Export staging, and the
        // scope tells the re-election core that the whole system is leaving: every one of its rules is
        // ineligible and none of its objects counts as a survivor.
        var allSyncRules = await Application.SyncRepo.GetAllSyncRulesAsync();
        var systemSyncRules = allSyncRules.Where(sr => sr.ConnectedSystemId == task.ConnectedSystemId).ToList();
        var priorityContext = new AttributePriorityContext(allSyncRules, honourNullAssertions: true);
        var syncEngine = new SyncEngine();
        var syncServer = new SyncServer(Application);
        var expressionEvaluator = new DynamicExpressoEvaluator();
        var exportEvaluationCache = await Application.ExportEvaluation.BuildExportEvaluationCacheAsync(allSyncRules);
        var recallScope = ContributorRecallScope.ForDeletedConnectedSystem(task.ConnectedSystemId);
        var survivorObjectTypes = new List<ConnectedSystemObjectType>();
        var systemNamesById = await Application.SyncRepo.GetConnectedSystemNamesAsync();
        var syncOutcomeTrackingLevel = await Application.ServiceSettings.GetSyncOutcomeTrackingLevelAsync();

        // Pass A: per-object obsoletion, batched in ascending id order (keyset pagination, which is also
        // what makes the checkpoint deterministic). Skipped entirely when a resumed run had already
        // finished this pass.
        if (task.CheckpointPhase is null or SynchronisedDeprovisioningPhase.ObjectPass)
        {
            // On resume, the count reflects only the remaining objects: completed batches deleted theirs.
            activity.ObjectsToProcess = await Application.SyncRepo.GetConnectedSystemObjectCountAsync(task.ConnectedSystemId);
            activity.ObjectsProcessed = 0;
            await Application.Repository.Activity.UpdateActivityAsync(activity);

            // Guid.Empty sorts first under both PostgreSQL's bytewise uuid ordering and .NET's Guid
            // comparison, so it is a safe "from the start" cursor that selects the keyset (id-ordered)
            // load path from the first page.
            var cursor = task.CheckpointPhase == SynchronisedDeprovisioningPhase.ObjectPass
                ? task.CheckpointConnectedSystemObjectId ?? Guid.Empty
                : Guid.Empty;

            while (true)
            {
                var page = await Application.SyncRepo.GetConnectedSystemObjectsAsync(
                    task.ConnectedSystemId, page: 1, pageSize: batchSize,
                    knownTotalCount: activity.ObjectsToProcess, lastSyncTimestamp: null, afterId: cursor);
                if (page.Results.Count == 0)
                    break;

                await ProcessDeprovisioningBatchAsync(task, activity, page.Results, systemSyncRules, recallScope,
                    priorityContext, syncEngine, syncServer, expressionEvaluator, exportEvaluationCache,
                    survivorObjectTypes, systemNamesById, syncOutcomeTrackingLevel, connectedSystem.Name, result);

                // Persist the checkpoint AFTER the batch is fully persisted: a crash between the two
                // re-processes the batch, which is safe (idempotent per object, delete-then-create export
                // staging), where the reverse ordering would skip unpersisted work.
                cursor = page.Results[^1].Id;
                task.CheckpointPhase = SynchronisedDeprovisioningPhase.ObjectPass;
                task.CheckpointConnectedSystemObjectId = cursor;
                await Application.Tasking.UpdateWorkerTaskAsync(task);
            }
        }

        // Pass B: residue recall by provenance, per import Synchronisation Rule in ascending id order,
        // reusing the #1537 per-rule recall. Catches values whose Metaverse Object no longer holds one of
        // this system's Connected System Objects (stranded by an earlier connector-space clear), which the
        // per-object pass structurally cannot reach. Runs strictly before the final deletion severs the
        // provenance.
        if (task.CheckpointPhase is not SynchronisedDeprovisioningPhase.FinalDeletion)
        {
            var importRules = systemSyncRules
                .Where(sr => sr.Direction == SyncRuleDirection.Import)
                .OrderBy(sr => sr.Id)
                .Where(sr => task.CheckpointPhase != SynchronisedDeprovisioningPhase.ResiduePass
                             || task.CheckpointSyncRuleId == null
                             || sr.Id > task.CheckpointSyncRuleId)
                .ToList();

            foreach (var importRule in importRules)
            {
                var residueResult = await RecallSyncRuleContributedValuesAsync(
                    importRule.Id,
                    recallScope,
                    priorityContext,
                    syncEngine,
                    expressionEvaluator,
                    exportEvaluationCache,
                    activity,
                    reElectedDetailMessage: $"Connected System '{connectedSystem.Name}' is being deprovisioned; a surviving contributor was re-elected for the recalled attribute value(s).",
                    clearedDetailMessage: $"Connected System '{connectedSystem.Name}' is being deprovisioned; the recalled attribute value(s) had no remaining contributor and were cleared.",
                    trackActivityProgress: false,
                    skipMetaverseObjectsPendingDeletion: true);

                result.ResidueMetaverseObjectsProcessed += residueResult.MetaverseObjectsProcessed;
                result.ResidueValuesRecalled += residueResult.ValuesRecalled;
                result.AttributesReElected += residueResult.AttributesReElected;
                result.AttributesCleared += residueResult.AttributesCleared;
                result.PendingExportsStaged += residueResult.PendingExportsStaged;

                task.CheckpointPhase = SynchronisedDeprovisioningPhase.ResiduePass;
                task.CheckpointSyncRuleId = importRule.Id;
                await Application.Tasking.UpdateWorkerTaskAsync(task);
            }
        }

        // Final step: the existing deletion (tombstone snapshot, orphan marking, bulk delete). The orphan
        // marking is retained for belt-and-braces: the per-object pass has already evaluated every
        // deletion rule, so it ordinarily finds nothing. Checkpointed first so a crash mid-deletion
        // resumes straight here; re-running the deletion is the ordinary retry of the immediate path.
        task.CheckpointPhase = SynchronisedDeprovisioningPhase.FinalDeletion;
        await Application.Tasking.UpdateWorkerTaskAsync(task);
        await ExecuteDeletionAsync(task.ConnectedSystemId, activity, changeReason: null,
            task.EvaluateMvoDeletionRules, task.DeleteChangeHistory);

        // Complete the task's Activity with summary statistics (the worker's dispatch boundary owns the
        // completion call itself, exactly as it does for the other queued task types).
        activity.Message =
            $"Deprovisioned {result.ConnectedSystemObjectsProcessed:N0} Connected System Object(s) through synchronisation: " +
            $"{result.AttributesReElected:N0} attribute value(s) re-elected to a surviving contributor, " +
            $"{result.AttributesCleared:N0} cleared (no remaining contributor); " +
            $"{result.MetaverseObjectsDeleted:N0} Metaverse Object(s) deleted and {result.MetaverseObjectsMarkedForDeletion:N0} marked for deletion by Deletion Rules; " +
            $"{result.ResidueValuesRecalled:N0} residual value(s) recalled by provenance across {result.ResidueMetaverseObjectsProcessed:N0} Metaverse Object(s); " +
            $"{result.PendingExportsStaged:N0} Pending Export(s) staged. The Connected System has been deleted.";

        Log.Information(
            "ExecuteSynchronisedDeprovisioningAsync: Connected System {ConnectedSystemId}: {CsoCount} Connected System Object(s) processed, " +
            "{ReElectedCount} attribute(s) re-elected, {ClearedCount} attribute(s) cleared, {MvoDeletedCount} Metaverse Object(s) deleted, " +
            "{MvoMarkedCount} Metaverse Object(s) marked for deletion, {ResidueValueCount} residual value(s) recalled across {ResidueObjectCount} object(s), " +
            "{PendingExportCount} Pending Export(s) staged; system deleted.",
            task.ConnectedSystemId, result.ConnectedSystemObjectsProcessed, result.AttributesReElected, result.AttributesCleared,
            result.MetaverseObjectsDeleted, result.MetaverseObjectsMarkedForDeletion, result.ResidueValuesRecalled,
            result.ResidueMetaverseObjectsProcessed, result.PendingExportsStaged);

        return result;
    }

    /// <summary>
    /// Processes one Pass A batch: marks each Connected System Object Obsolete, runs it through the
    /// obsoletion core, then persists the batch in dependency order (Metaverse Object updates and recalled
    /// value deletions, Connected System Object deletions with change records, immediate Metaverse Object
    /// deletions with their deletion-cascade and reference-recall Pending Exports, recall export staging,
    /// per-object results, Activity counters). Everything is persisted before the caller records the
    /// checkpoint.
    /// </summary>
    private async Task ProcessDeprovisioningBatchAsync(
        DeleteConnectedSystemWorkerTask task,
        Activity activity,
        List<ConnectedSystemObject> batch,
        List<SyncRule> systemSyncRules,
        ContributorRecallScope recallScope,
        AttributePriorityContext priorityContext,
        SyncEngine syncEngine,
        SyncServer syncServer,
        DynamicExpressoEvaluator expressionEvaluator,
        ExportEvaluationCache exportEvaluationCache,
        List<ConnectedSystemObjectType> survivorObjectTypes,
        Dictionary<int, string> systemNamesById,
        ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel syncOutcomeTrackingLevel,
        string connectedSystemName,
        ConnectedSystemDeprovisioningResult result)
    {
        // Refresh the export cache for the batch's joined Metaverse Objects BEFORE the joins are broken.
        var joinedMvoIds = batch
            .Where(cso => cso.MetaverseObjectId.HasValue)
            .Select(cso => cso.MetaverseObjectId!.Value)
            .Distinct()
            .ToList();
        if (joinedMvoIds.Count > 0)
            await Application.ExportEvaluation.RefreshExportEvaluationCacheForPageAsync(exportEvaluationCache, joinedMvoIds);

        var changedMvos = new List<MetaverseObject>();
        var graceMarkedMvos = new List<MetaverseObject>();
        var removedValueIds = new List<Guid>();
        var stagedPendingExports = new List<PendingExport>();
        var quietCsoDeletions = new List<ConnectedSystemObject>();
        var csoDeletions = new List<(ConnectedSystemObject Cso, ActivityRunProfileExecutionItem ExecutionItem)>();
        var executionItems = new List<ActivityRunProfileExecutionItem>();
        var pendingMvoDeletions = new List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> FinalAttributeValues)>();
        var preRecallAttributeSnapshots = new Dictionary<Guid, List<MetaverseObjectAttributeValue>>();

        // The Metaverse Object deletion rule delegate: evaluates via the engine and applies the decision at
        // application level, mirroring the worker processor's MarkMvoForDeletionAsync semantics: a
        // grace-period identity is marked for deferred deletion (housekeeping deletes it after the window),
        // an immediate one is queued for this batch's deletion flush.
        async Task<(MvoDeletionDecision Decision, string? PolicySnapshotJson)> ProcessMvoDeletionRuleAsync(
            MetaverseObject mvo, int disconnectingSystemId, IReadOnlyCollection<int> remainingConnectedSystemIds)
        {
            var disconnectingSystemName = systemNamesById.GetValueOrDefault(disconnectingSystemId, connectedSystemName);
            var decision = syncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingConnectedSystemIds, disconnectingSystemName);
            var policySnapshotJson = BuildDeprovisioningMvoDeletionPolicySnapshotJson(
                mvo, disconnectingSystemId, remainingConnectedSystemIds, decision, systemNamesById, disconnectingSystemName);

            switch (decision.Fate)
            {
                case MvoDeletionFate.DeletedImmediately:
                    mvo.DeletionTriggeredBySystemId = disconnectingSystemId;
                    mvo.DeletionTriggeredBySystemName = disconnectingSystemName;
                    if (!pendingMvoDeletions.Any(d => d.Mvo.Id == mvo.Id))
                    {
                        // Use the pre-recall snapshot for the deletion change record where one was captured
                        // (recall is skipped for immediate deletions, so the current values normally suffice).
                        var finalAttributeValues = preRecallAttributeSnapshots.TryGetValue(mvo.Id, out var snapshot)
                            ? snapshot
                            : mvo.AttributeValues.ToList();
                        preRecallAttributeSnapshots.Remove(mvo.Id);
                        pendingMvoDeletions.Add((mvo, finalAttributeValues));
                    }
                    break;

                case MvoDeletionFate.DeletionScheduled:
                    // Grace period configured: mark for deferred deletion by housekeeping, capturing the
                    // initiator triad and the decision-time policy snapshot (#119) exactly as the worker
                    // path does.
                    mvo.DeletionTriggeredBySystemId = disconnectingSystemId;
                    mvo.DeletionTriggeredBySystemName = disconnectingSystemName;
                    mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
                    mvo.DeletionInitiatedByType = activity.InitiatedByType;
                    mvo.DeletionInitiatedById = activity.InitiatedById;
                    mvo.DeletionInitiatedByName = activity.InitiatedByName;
                    mvo.DeletionPolicySnapshotJson = policySnapshotJson;
                    graceMarkedMvos.Add(mvo);
                    break;
            }

            return (decision, policySnapshotJson);
        }

        foreach (var connectedSystemObject in batch)
        {
            // Mark Obsolete and process through the shared obsoletion core. An object a crashed run already
            // obsoleted re-enters here identically; one it already deleted is simply absent from the page.
            connectedSystemObject.Status = ConnectedSystemObjectStatus.Obsolete;

            var obsoletionResult = await ConnectedSystemObjectObsoletionService.ProcessObsoleteConnectedSystemObjectAsync(
                connectedSystemObject,
                systemSyncRules,
                recallScope,
                priorityContext,
                syncEngine,
                Application.SyncRepo,
                (survivor, rule) => Application.ScopingEvaluation.IsCsoInScopeForImportRule(survivor, rule),
                survivorObjectTypes,
                expressionEvaluator,
                activity.PrepareRunProfileExecutionItem,
                syncOutcomeTrackingLevel,
                ProcessMvoDeletionRuleAsync,
                recordPreRecallAttributeSnapshot: mvo =>
                {
                    // First snapshot wins: a second object of the same Metaverse Object obsoleting in the
                    // same batch must not overwrite the pre-recall state the first capture recorded.
                    if (!preRecallAttributeSnapshots.ContainsKey(mvo.Id))
                        preRecallAttributeSnapshots[mvo.Id] = mvo.AttributeValues.ToList();
                });

            quietCsoDeletions.AddRange(obsoletionResult.QuietCsoDeletions);
            csoDeletions.AddRange(obsoletionResult.CsoDeletions);
            executionItems.AddRange(obsoletionResult.ExecutionItems);
            if (obsoletionResult.MvoToUpdate != null)
                changedMvos.Add(obsoletionResult.MvoToUpdate);
            if (obsoletionResult.MvoAttributeChange is { } mvoChange)
                removedValueIds.AddRange(mvoChange.Removals.Where(av => av.Id != Guid.Empty).Select(av => av.Id));

            if (obsoletionResult.ExportEvaluation is { } exportEvaluationInput)
            {
                // Stage the resulting export changes for mapped target systems. Recall semantics: the
                // deprovisioning updates existing target objects and never provisions new ones.
                var exportEvaluation = await Application.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
                    exportEvaluationInput.Mvo, exportEvaluationInput.ChangedAttributes, exportEvaluationCache,
                    deferSave: true, removedAttributes: exportEvaluationInput.RemovedAttributes,
                    existingPendingExports: stagedPendingExports, recallSemantics: true);
                var newlyStagedExports = exportEvaluation.PendingExports
                    .Where(pendingExport => !stagedPendingExports.Contains(pendingExport))
                    .ToList();
                stagedPendingExports.AddRange(newlyStagedExports);
            }

            result.ConnectedSystemObjectsProcessed++;
            result.AttributesReElected += obsoletionResult.MvoAttributeChange?.Additions.Count ?? 0;
            result.AttributesCleared += obsoletionResult.RecallClearedAttributeCount;
        }

        // Persist the batch in dependency order. Step 1: Metaverse Object attribute changes and grace-period
        // deletion markers (a grace-marked object with no attribute change still needs persisting), then the
        // recalled value rows.
        var mvosToPersist = changedMvos
            .Concat(graceMarkedMvos.Where(marked => changedMvos.All(changed => changed.Id != marked.Id)))
            .ToList();
        if (mvosToPersist.Count > 0)
            await Application.SyncRepo.UpdateMetaverseObjectsAsync(mvosToPersist);
        if (removedValueIds.Count > 0)
            await Application.SyncRepo.DeleteMetaverseObjectAttributeValuesByIdsAsync(removedValueIds);
        result.MetaverseObjectsMarkedForDeletion += graceMarkedMvos.Count;

        // Step 2: delete the batch's Connected System Objects (quiet deletions and per-object ones alike).
        // Deliberately WITHOUT per-object Connected System Object change records: the whole system's change
        // history is about to be deleted or FK-severed by the run's final deletion, so freshly minted Delete
        // change rows would be orphaned moments later; the per-object story lives on the execution items.
        var allCsoDeletions = quietCsoDeletions.Concat(csoDeletions.Select(d => d.Cso)).ToList();
        if (allCsoDeletions.Count > 0)
            await Application.SyncRepo.DeleteConnectedSystemObjectsAsync(allCsoDeletions);

        // Step 3: flush the immediate Metaverse Object deletions: capture which other objects reference the
        // candidates BEFORE deletion nulls the reference foreign keys, ensure delete Pending Exports for
        // the deleted objects' remaining Connected System Objects (the deletion cascade), delete with
        // change records, then stage reference-recall Pending Exports so referencing targets are corrected.
        if (pendingMvoDeletions.Count > 0)
        {
            var deletionCandidateIds = pendingMvoDeletions.Select(d => d.Mvo.Id).ToList();
            var referenceRecallContext = await syncServer.CaptureReferenceRecallContextAsync(deletionCandidateIds);

            var deleteExports = await syncServer.EvaluateMvoDeletionsAsync(
                pendingMvoDeletions.Select(d => d.Mvo).ToList(), exportEvaluationCache);
            result.PendingExportsStaged += deleteExports.Count;

            await syncServer.DeleteMetaverseObjectsAsync(
                pendingMvoDeletions, activity.InitiatedByType, activity.InitiatedById, activity.InitiatedByName);
            result.MetaverseObjectsDeleted += pendingMvoDeletions.Count;

            var referenceRecallResult = await syncServer.StageReferenceRecallExportsAsync(
                referenceRecallContext, deletionCandidateIds, exportEvaluationCache);
            result.PendingExportsStaged += referenceRecallResult.PendingExportsStaged;
        }

        // Step 4: stage the recall/re-election Pending Exports with the same delete-then-create pattern the
        // sync flush uses. This is what makes export staging idempotent on resume: re-evaluating an object
        // processed just before a crash replaces its target's previous staging rather than duplicating it.
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

        // Step 5: persist the per-object results. The deleted objects' rows are gone, so the items must
        // reference them by snapshot only (the core snapshotted the display fields eagerly, and the
        // CsoDeleted outcome carries the deleted id durably); a foreign key to a deleted row would fail.
        foreach (var executionItem in executionItems)
        {
            executionItem.ConnectedSystemObject = null;
            executionItem.ConnectedSystemObjectId = null;
        }
        await Application.Activities.AddRunProfileExecutionItemsAsync(activity, executionItems);

        activity.ObjectsProcessed += batch.Count;
        await Application.Repository.Activity.UpdateActivityAsync(activity);

        Log.Information(
            "ProcessDeprovisioningBatchAsync: Connected System {ConnectedSystemId}: batch of {BatchCount} object(s) processed: " +
            "{MvoUpdateCount} Metaverse Object(s) updated, {MvoDeleteCount} deleted, {GraceMarkedCount} marked for deletion, " +
            "{PendingExportCount} recall Pending Export(s) staged.",
            task.ConnectedSystemId, batch.Count, mvosToPersist.Count, pendingMvoDeletions.Count,
            graceMarkedMvos.Count, stagedPendingExports.Count);
    }

    /// <summary>
    /// Builds the serialised decision-time deletion policy snapshot (#119) for a deletion rule evaluation in
    /// the deprovisioning run, mirroring the worker processor's snapshot semantics: produced whenever the
    /// evaluation records an outcome (triggered, or evaluated against the source list without triggering);
    /// null for a plain non-event or an untyped Metaverse Object.
    /// </summary>
    private static string? BuildDeprovisioningMvoDeletionPolicySnapshotJson(
        MetaverseObject mvo,
        int disconnectingSystemId,
        IReadOnlyCollection<int> remainingConnectedSystemIds,
        MvoDeletionDecision decision,
        IReadOnlyDictionary<int, string> systemNamesById,
        string disconnectingSystemName)
    {
        var type = mvo.Type;
        if (type == null)
            return null;

        var triggerIds = type.DeletionTriggerConnectedSystemIds ?? [];
        var evaluatedAgainstSourceList = type.DeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
            && triggerIds.Contains(disconnectingSystemId);
        if (decision.Fate == MvoDeletionFate.NotDeleted && !evaluatedAgainstSourceList)
            return null;

        var snapshot = new MvoDeletionPolicySnapshot
        {
            DeletionRule = type.DeletionRule,
            TriggerMode = type.DeletionTriggerMode,
            GracePeriod = type.DeletionGracePeriod,
            TriggeringSystemId = disconnectingSystemId,
            TriggeringSystemName = disconnectingSystemName,
            ReasonCode = decision.ReasonCode,
            DeletionEligibleDate = decision.Fate == MvoDeletionFate.DeletionScheduled && decision.GracePeriod.HasValue
                ? DateTime.UtcNow.Add(decision.GracePeriod.Value)
                : null
        };

        foreach (var sourceSystemId in triggerIds)
        {
            snapshot.SelectedSourceSystemIds.Add(sourceSystemId);
            snapshot.SelectedSourceSystemNames.Add(systemNamesById.GetValueOrDefault(sourceSystemId, $"Connected System {sourceSystemId}"));
        }

        // The listed sources still holding a joined Connected System Object at decision time, distinct (a
        // source with two joined objects is one remaining source).
        foreach (var remainingSourceSystemId in remainingConnectedSystemIds.Where(triggerIds.Contains).Distinct())
        {
            snapshot.RemainingConnectedSourceSystemIds.Add(remainingSourceSystemId);
            snapshot.RemainingConnectedSourceSystemNames.Add(systemNamesById.GetValueOrDefault(remainingSourceSystemId, $"Connected System {remainingSourceSystemId}"));
        }

        return snapshot.ToJson();
    }
}
