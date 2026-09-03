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
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The executor for a gated stranded-value sweep (#1549, gated on a genuine re-import and extended with
/// Deletion Rule evaluation by #1605): a Connector Space clear hard-deletes Connected System Objects without
/// obsoletion, so Metaverse attribute values the cleared system contributed survive with live provenance and
/// no joined Connected System Object of that system, indefinitely stranded until something recalls them, and
/// the objects themselves receive no Deletion Rule decision at all. The next Full Synchronisation of the
/// cleared system reads <see cref="ConnectedSystem.StrandedValueSweepArmedAt"/> and, once a Full Import of
/// the system has completed successfully later than that arming (see <see cref="IsSweepGateOpen"/>), runs
/// this sweep after its ordinary passes:
/// <list type="number">
/// <item>Re-join shortfall check (#1605 Functional Requirement 9): refuses, doing nothing else, when too
/// great a share of the objects recorded at the clear have not rejoined.</item>
/// <item>Per import Synchronisation Rule (enabled and disabled alike), selects the stranded candidates by
/// provenance-plus-join-absence and recalls them through the shipped #1537/#809 recall engine under the
/// <see cref="ContributorRecallScope.ForStrandedContribution"/> scope (#1549, unchanged).</item>
/// <item>Deletion Rule evaluation (#1605 Functional Requirement 7) for every recorded object that still lacks
/// a re-join, using the same evaluation and marking code the obsoletion path uses
/// (<see cref="MetaverseObjectDeletionRuleApplier"/>).</item>
/// <item>The state-convergent zero-join pass (#1605 Functional Requirement 10), metaverse-wide.</item>
/// <item>Deletes the join records and clears the arming.</item>
/// </list>
/// A Full Synchronisation run while the gate is closed leaves the arming in place instead; see
/// <see cref="ExecuteStrandedValueSweepIfArmedAsync"/>.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Executes the stranded-value sweep for one Connected System, once its Full Synchronisation run has
    /// completed its ordinary passes. Refuses (fast, hard) unless the caller has already confirmed the
    /// system is armed (<see cref="ConnectedSystem.StrandedValueSweepArmedAt"/> not null): the arming read is
    /// the caller's job (Full Synchronisation pays one nullable-timestamp read on every other run, and must
    /// not construct the sweep's support set unless there is work to do), so a call here without it is a
    /// caller defect, not a normal "nothing to do" outcome.
    /// <para>
    /// The re-join shortfall check runs first and can refuse the whole sweep (#1605 Functional Requirement
    /// 9): when too great a share of the objects recorded at the clear have not rejoined, neither the value
    /// recall, the Deletion Rule evaluation nor the zero-join pass runs. The arming and the join record both
    /// stay in place so a later run can retry once the administrator has re-imported, or raised the
    /// threshold setting.
    /// </para>
    /// <para>
    /// Otherwise sweeps EVERY import Synchronisation Rule of the system, enabled and disabled alike, for
    /// stranded values: the sweep substitutes for the obsoletion that never ran when the Connector Space was
    /// cleared, and obsoletion recalls by system regardless of rule enablement. The #1537 disable-retention
    /// doctrine protects a paused flow whose source object remains; here the source object is gone, which is
    /// a different question entirely. A rule is skipped only when its Connected System Object Type has
    /// RemoveContributedAttributesOnObsoletion disabled: those retained values are policy, not strands, and
    /// the sweep must not override an administrator's deliberate choice.
    /// </para>
    /// <para>
    /// After the value recall, every recorded Metaverse Object that still lacks a re-join is evaluated
    /// against its type's Deletion Rule, event-shaped with the cleared system as the disconnecting system
    /// (#1605 Functional Requirement 7): a Manual rule does nothing, a grace-period fate is marked for
    /// deferred deletion, and a no-grace fate is flushed immediately through the #809 batch machinery. Then
    /// the state-convergent zero-join pass (#1605 Functional Requirement 10) finds every Projected Metaverse
    /// Object metaverse-wide with no joined Connected System Object at all and a state-convergent Deletion
    /// Rule, and marks it (never an immediate delete) with a null triggering system.
    /// </para>
    /// <para>
    /// Metaverse Objects already pending deletion are left untouched by the value recall (their single-source
    /// values were already frozen for the grace window by whatever triggered the pending deletion, and
    /// housekeeping owns their removal) and are skipped by the Deletion Rule evaluation pass, so an earlier
    /// decision's markers are never overwritten.
    /// </para>
    /// <para>
    /// Does not complete the Activity or set its Message: the caller (the Full Synchronisation task
    /// processor) owns run-level messaging and completion. Execution items are recorded per Metaverse
    /// Object as each pass processes them, the zero-findings case included (nothing is recorded when a pass
    /// finds no candidates at all).
    /// </para>
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose Connector Space was cleared; must be armed.</param>
    /// <param name="activity">The Full Synchronisation run's Activity to record execution items against.</param>
    public async Task<StrandedValueSweepResult> ExecuteStrandedValueSweepAsync(ConnectedSystem connectedSystem, Activity activity)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(activity);
        if (connectedSystem.StrandedValueSweepArmedAt is not { } armedAt)
            throw new InvalidDataException(
                $"ExecuteStrandedValueSweepAsync: Connected System {connectedSystem.Id} is not armed " +
                "(StrandedValueSweepArmedAt is null); refusing to sweep. The caller must check the arming, " +
                "and the #1605 gate, before invoking the sweep.");

        var result = new StrandedValueSweepResult();

        // #1605 Functional Requirement 9: the re-join shortfall check. Runs before anything else touches the
        // Connector Space or the Metaverse: a broken re-import (a filter or base DN change) must never be
        // mistaken for a genuine mass departure, and this is the only point that can tell the two apart.
        // The joined-systems lookup reuses the same per-object query the obsoletion path already uses for
        // "remaining connected systems" (ConnectedSystemObjectObsoletionService, #1537's deletion recall),
        // rather than a bespoke batched query: the sweep's recorded set is bounded by what one clear joined,
        // and consistency with the established lookup matters more here than shaving round trips.
        var recordedMvoIds = await Application.Repository.ConnectedSystems.GetConnectorSpaceClearJoinRecordedMetaverseObjectIdsAsync(connectedSystem.Id);
        var joinedSystemIdsByMvoId = new Dictionary<Guid, List<int>>();
        foreach (var recordedId in recordedMvoIds)
            joinedSystemIdsByMvoId[recordedId] = await Application.SyncRepo.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(recordedId);

        var missingMvoIds = recordedMvoIds
            .Where(id => !joinedSystemIdsByMvoId[id].Contains(connectedSystem.Id))
            .ToList();

        var maxMissingPercent = await Application.ServiceSettings.GetPostClearReconciliationMaxMissingPercentAsync();
        if (IsReconciliationRefused(recordedMvoIds.Count, missingMvoIds.Count, maxMissingPercent))
        {
            result.Refused = true;
            result.RefuseReason = BuildSweepRefusedMessage(missingMvoIds.Count, recordedMvoIds.Count, armedAt, maxMissingPercent);

            Log.Warning(
                "ExecuteStrandedValueSweepAsync: Connected System {ConnectedSystemId}: refused - {Missing} of {Recorded} recorded object(s) have not rejoined, above the {Threshold}% threshold. Arming and join records left in place.",
                connectedSystem.Id, missingMvoIds.Count, recordedMvoIds.Count, maxMissingPercent);

            return result;
        }

        // The sweep's support set, mirroring the #1537/#809 recall executors: the priority contributor
        // cache is built from every Synchronisation Rule, the export cache drives Pending Export staging,
        // and the scope excludes the swept system's own rules from re-election.
        var allSyncRules = await Application.SyncRepo.GetAllSyncRulesAsync();
        var priorityContext = new AttributePriorityContext(allSyncRules, honourNullAssertions: true);
        var syncEngine = new SyncEngine();
        var expressionEvaluator = new DynamicExpressoEvaluator();
        var exportEvaluationCache = await Application.ExportEvaluation.BuildExportEvaluationCacheAsync(allSyncRules);
        var recallScope = ContributorRecallScope.ForStrandedContribution(connectedSystem.Id);
        var remainingImportSourceEvaluator = new RemainingImportSourceEvaluator(Application.SyncRepo);

        // Fetched once: the rules from GetAllSyncRulesAsync carry their Connected System Object Type
        // navigation already, so this is the defensive fallback, not the ordinary path.
        var objectTypesById = (await Application.Repository.ConnectedSystems.GetObjectTypesAsync(connectedSystem.Id))
            .ToDictionary(t => t.Id);

        var importRules = allSyncRules
            .Where(rule => rule.ConnectedSystemId == connectedSystem.Id && rule.Direction == SyncRuleDirection.Import)
            .OrderBy(rule => rule.Id)
            .ToList();

        foreach (var importRule in importRules)
        {
            var objectType = importRule.ConnectedSystemObjectType
                ?? (objectTypesById.TryGetValue(importRule.ConnectedSystemObjectTypeId, out var resolvedType) ? resolvedType : null);
            if (objectType != null && !objectType.RemoveContributedAttributesOnObsoletion)
            {
                // Retained values here are policy, not strands; the sweep must not override that.
                continue;
            }

            var strandedMvoIds = await Application.SyncRepo.GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(
                importRule.Id, connectedSystem.Id);
            if (strandedMvoIds.Count == 0)
                continue;

            result.SyncRulesSwept++;

            var ruleResult = await RecallSyncRuleContributedValuesAsync(
                importRule.Id,
                recallScope,
                priorityContext,
                syncEngine,
                expressionEvaluator,
                exportEvaluationCache,
                activity,
                reElectedDetailMessage: $"Values stranded by an earlier Connector Space clear of Connected System '{connectedSystem.Name}' were recalled; a surviving contributor was re-elected.",
                clearedDetailMessage: $"Values stranded by an earlier Connector Space clear of Connected System '{connectedSystem.Name}' were recalled; no remaining contributor supplied the attribute value(s), which were cleared.",
                trackActivityProgress: false,
                skipMetaverseObjectsPendingDeletion: true,
                affectedMetaverseObjectIds: strandedMvoIds,
                remainingImportSourceEvaluator: remainingImportSourceEvaluator,
                preservedDetailMessage: $"Values stranded by an earlier Connector Space clear of Connected System '{connectedSystem.Name}' were found; the Metaverse Object has no remaining import source, so its values were preserved as last known state.");

            result.MetaverseObjectsProcessed += ruleResult.MetaverseObjectsProcessed;
            result.ValuesRecalled += ruleResult.ValuesRecalled;
            result.AttributesReElected += ruleResult.AttributesReElected;
            result.AttributesCleared += ruleResult.AttributesCleared;
            result.MetaverseObjectsPreserved += ruleResult.MetaverseObjectsPreserved;
            result.ValuesPreserved += ruleResult.ValuesPreserved;
            result.PendingExportsStaged += ruleResult.PendingExportsStaged;
        }

        // #1605 Functional Requirement 7: evaluate the Deletion Rule for every recorded object that still
        // lacks a re-join. Runs after the value recall above (which may itself have touched some of these
        // objects' attribute values, but never their joins or their deletion marking).
        if (missingMvoIds.Count > 0)
        {
            await EvaluateDeletionRulesForMissingObjectsAsync(
                connectedSystem, activity, missingMvoIds, joinedSystemIdsByMvoId, syncEngine, exportEvaluationCache, result);
        }

        // #1605 Functional Requirement 10: the state-convergent zero-join pass. Metaverse-wide, not scoped
        // to this system, so it also catches historical strays (a clear that predates this feature, or
        // several disconnections over time).
        result.MetaverseObjectsMarkedWithNoConnector = await Application.Metaverse.MarkStateConvergentZeroJoinMvosForDeletionAsync(
            activity, "the post-clear reconciliation");

        // The join record has served its purpose; delete it before clearing the arming. The repository
        // update is deliberately immune to context tracking (its own contract), so the caller's in-memory
        // instance is updated directly here too, letting the Full Synchronisation task processor observe
        // the change without a re-fetch.
        await Application.Repository.ConnectedSystems.DeleteConnectorSpaceClearJoinRecordsAsync(connectedSystem.Id);
        await Application.Repository.ConnectedSystems.SetStrandedValueSweepArmedAtAsync(connectedSystem.Id, armedAt: null);
        connectedSystem.StrandedValueSweepArmedAt = null;

        Log.Information(
            "ExecuteStrandedValueSweepAsync: Connected System {ConnectedSystemId}: {RuleCount} Synchronisation Rule(s) swept, " +
            "{ObjectCount} Metaverse Object(s) processed, {ValueCount} value(s) recalled, {ReElectedCount} attribute(s) re-elected, " +
            "{ClearedCount} attribute(s) cleared, {PreservedObjectCount} Metaverse Object(s) preserved ({PreservedValueCount} value(s)); " +
            "{EvaluatedCount} Metaverse Object(s) evaluated against their Deletion Rules ({MarkedCount} marked, {DeletedCount} deleted); " +
            "{NoConnectorCount} object(s) with no connector remaining marked for deletion; {PendingExportCount} Pending Export(s) staged.",
            connectedSystem.Id, result.SyncRulesSwept, result.MetaverseObjectsProcessed, result.ValuesRecalled,
            result.AttributesReElected, result.AttributesCleared, result.MetaverseObjectsPreserved, result.ValuesPreserved,
            result.MetaverseObjectsEvaluatedForDeletion, result.MetaverseObjectsMarkedForDeletion, result.MetaverseObjectsDeleted,
            result.MetaverseObjectsMarkedWithNoConnector, result.PendingExportsStaged);

        return result;
    }

    /// <summary>
    /// #1605 Functional Requirement 7: evaluates the Deletion Rule for each still-missing recorded Metaverse
    /// Object that still exists, in pages of the sync page size, applying the decision through the shared
    /// <see cref="MetaverseObjectDeletionRuleApplier"/> (the same code the obsoletion path uses). A grace
    /// fate is marked and persisted; a no-grace fate is queued and flushed per page with the #809 batch
    /// sequence (capture reference-recall context, evaluate deletions, delete, stage reference-recall
    /// exports). Objects already pending deletion are skipped, so an earlier decision's markers are never
    /// overwritten. Records one Run Profile Execution Item per evaluated object whose fate is not NotDeleted.
    /// </summary>
    private async Task EvaluateDeletionRulesForMissingObjectsAsync(
        ConnectedSystem connectedSystem,
        Activity activity,
        List<Guid> missingMvoIds,
        Dictionary<Guid, List<int>> joinedSystemIdsByMvoId,
        SyncEngine syncEngine,
        ExportEvaluationCache exportEvaluationCache,
        StrandedValueSweepResult result)
    {
        var syncServer = new SyncServer(Application);
        var syncPageSize = await Application.ServiceSettings.GetSyncPageSizeAsync();
        // A single-entry map: the cleared system is the only disconnecting system this pass ever evaluates.
        var systemNamesById = new Dictionary<int, string> { [connectedSystem.Id] = connectedSystem.Name };

        foreach (var pageIds in missingMvoIds.Chunk(Math.Max(syncPageSize, 1)))
        {
            var pageMvos = await Application.SyncRepo.GetMetaverseObjectsByIdsForUpdateAsync(pageIds);
            if (pageMvos.Count == 0)
                continue;

            // Refresh the export cache for this page's Metaverse Objects before evaluating, exactly as the
            // deprovisioning batch does at its top.
            await Application.ExportEvaluation.RefreshExportEvaluationCacheForPageAsync(exportEvaluationCache, pageMvos.Select(mvo => mvo.Id).ToList());

            var graceMarkedMvos = new List<MetaverseObject>();
            var pendingImmediateDeletions = new List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> FinalAttributeValues)>();
            var executionItems = new List<ActivityRunProfileExecutionItem>();

            // Already pending deletion is filtered out here: an earlier decision's markers stand.
            foreach (var mvo in pageMvos.Where(m => m.LastConnectorDisconnectedDate == null))
            {
                var remainingConnectedSystemIds = joinedSystemIdsByMvoId.TryGetValue(mvo.Id, out var joinedIds)
                    ? (IReadOnlyCollection<int>)joinedIds
                    : Array.Empty<int>();

                var (decision, policySnapshotJson) = MetaverseObjectDeletionRuleApplier.Apply(
                    syncEngine, mvo, connectedSystem.Id, remainingConnectedSystemIds, systemNamesById, connectedSystem.Name,
                    activity.InitiatedByType, activity.InitiatedById, activity.InitiatedByName);

                result.MetaverseObjectsEvaluatedForDeletion++;

                switch (decision.Fate)
                {
                    case MvoDeletionFate.DeletionScheduled:
                        graceMarkedMvos.Add(mvo);
                        result.MetaverseObjectsMarkedForDeletion++;
                        executionItems.Add(BuildDeletionRuleExecutionItem(
                            mvo, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled,
                            ConnectedSystemObjectObsoletionService.BuildMvoDeletionDetailMessage(
                                decision.Fate, decision.Reason, decision.GracePeriod ?? mvo.Type?.DeletionGracePeriod, mvo.DeletionEligibleDate),
                            policySnapshotJson));
                        break;

                    case MvoDeletionFate.DeletedImmediately:
                        pendingImmediateDeletions.Add((mvo, mvo.AttributeValues.ToList()));
                        executionItems.Add(BuildDeletionRuleExecutionItem(
                            mvo, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
                            ConnectedSystemObjectObsoletionService.BuildMvoDeletionDetailMessage(decision.Fate, decision.Reason, null, null),
                            policySnapshotJson));
                        break;
                }
            }

            if (graceMarkedMvos.Count > 0)
                await Application.SyncRepo.UpdateMetaverseObjectsAsync(graceMarkedMvos);

            if (pendingImmediateDeletions.Count > 0)
            {
                var deletionCandidateIds = pendingImmediateDeletions.Select(d => d.Mvo.Id).ToList();
                var referenceRecallContext = await syncServer.CaptureReferenceRecallContextAsync(deletionCandidateIds);

                var deleteExports = await syncServer.EvaluateMvoDeletionsAsync(
                    pendingImmediateDeletions.Select(d => d.Mvo).ToList(), exportEvaluationCache);
                result.PendingExportsStaged += deleteExports.Count;

                await syncServer.DeleteMetaverseObjectsAsync(
                    pendingImmediateDeletions, activity.InitiatedByType, activity.InitiatedById, activity.InitiatedByName);
                result.MetaverseObjectsDeleted += pendingImmediateDeletions.Count;

                var referenceRecallResult = await syncServer.StageReferenceRecallExportsAsync(
                    referenceRecallContext, deletionCandidateIds, exportEvaluationCache);
                result.PendingExportsStaged += referenceRecallResult.PendingExportsStaged;
            }

            if (executionItems.Count > 0)
                await Application.Activities.AddRunProfileExecutionItemsAsync(activity, executionItems);
        }
    }

    /// <summary>
    /// Builds the per-object Run Profile Execution Item for a #1605 Deletion Rule evaluation outcome: a
    /// single MvoDeleted or MvoDeletionScheduled outcome, mirroring the shape of a synchronisation-triggered
    /// deletion marking.
    /// </summary>
    private static ActivityRunProfileExecutionItem BuildDeletionRuleExecutionItem(
        MetaverseObject mvo, ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, string? detailMessage, string? policySnapshotJson)
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.AttributeFlow,
            DisplayNameSnapshot = mvo.NameOrId,
            ObjectTypeSnapshot = mvo.Type?.Name,
            DeletionPolicySnapshotJson = policySnapshotJson
        };

        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = outcomeType,
            TargetEntityId = mvo.Id,
            TargetEntityDescription = mvo.NameOrId,
            DetailMessage = detailMessage,
            Ordinal = 0
        });

        item.OutcomeSummary = $"{outcomeType}:1";
        return item;
    }

    /// <summary>
    /// The caller-facing entry point for a Full Synchronisation run: reads
    /// <see cref="ConnectedSystem.StrandedValueSweepArmedAt"/> and returns null immediately when it is null,
    /// so every ordinary run (the overwhelming majority) pays exactly one nullable-timestamp read and never
    /// constructs the sweep's support set. When armed, the #1605 Full Import gate decides what happens next:
    /// <list type="bullet">
    /// <item>Gate closed (no Full Import of this system has completed successfully later than the arming):
    /// the sweep does not run, the arming is left in place, and a sentence stating why is appended to the
    /// Activity Message. The returned result carries <see cref="StrandedValueSweepResult.Skipped"/> true.</item>
    /// <item>Gate open: runs <see cref="ExecuteStrandedValueSweepAsync"/>, appends its outcome (executed or
    /// refused) to the run's Activity Message as a new sentence. A refusal leaves the arming in place; an
    /// execution clears it, as that method already does.</item>
    /// </list>
    /// Either way the Activity is persisted and the result is returned so the caller can log or report on
    /// it further if it chooses to.
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose Full Synchronisation run has just completed
    /// its ordinary passes.</param>
    /// <param name="activity">The Full Synchronisation run's Activity; its Message gains the sweep's summary
    /// sentence when the sweep runs, or the skipped/refused sentence otherwise.</param>
    public async Task<StrandedValueSweepResult?> ExecuteStrandedValueSweepIfArmedAsync(ConnectedSystem connectedSystem, Activity activity)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(activity);
        if (connectedSystem.StrandedValueSweepArmedAt is not { } armedAt)
            return null;

        if (!IsSweepGateOpen(armedAt, connectedSystem.LastSuccessfulFullImportCompletedAt))
        {
            var skippedResult = new StrandedValueSweepResult
            {
                Skipped = true,
                SkipReason = BuildSweepSkippedMessage(armedAt)
            };
            activity.Message = AppendSweepSentence(activity.Message, skippedResult.SkipReason);
            await Application.Repository.Activity.UpdateActivityAsync(activity);

            return skippedResult;
        }

        var result = await ExecuteStrandedValueSweepAsync(connectedSystem, activity);

        var sweepMessage = result.Refused ? result.RefuseReason! : BuildSweepActivityMessage(result);
        activity.Message = AppendSweepSentence(activity.Message, sweepMessage);
        await Application.Repository.Activity.UpdateActivityAsync(activity);

        return result;
    }

    /// <summary>
    /// Stamps <see cref="ConnectedSystem.LastSuccessfulFullImportCompletedAt"/> (#1605) once the worker's
    /// Full Import run-profile branch has determined the run's Activity completed successfully. Delta
    /// Import never calls this: the stranded-value sweep gate cares only about a genuinely rebuilt Connector
    /// Space, which only a Full Import can produce.
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose Full Import just completed successfully.</param>
    /// <param name="completedAt">The UTC time the Full Import's Activity completed.</param>
    public async Task RecordSuccessfulFullImportAsync(ConnectedSystem connectedSystem, DateTime completedAt)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        await Application.Repository.ConnectedSystems.SetLastSuccessfulFullImportCompletedAtAsync(connectedSystem.Id, completedAt);
        connectedSystem.LastSuccessfulFullImportCompletedAt = completedAt;
    }

    /// <summary>
    /// The #1605 Full Import gate: whether a sweep armed at <paramref name="armedAt"/> may run, given the
    /// Connected System's most recent successful Full Import completion. Open only when a Full Import has
    /// completed successfully strictly later than the arming; a null arming, a null import, or an import at
    /// or before the arming all keep the gate closed, because none of them proves the Connector Space has
    /// been genuinely rebuilt since the clear. Pure and static so the decision is directly testable without
    /// constructing a sweep.
    /// </summary>
    /// <param name="armedAt">When the stranded-value sweep was armed, or null if it is not armed at all.</param>
    /// <param name="lastSuccessfulFullImportCompletedAt">When the Connected System's most recent successful
    /// Full Import completed, or null if none ever has.</param>
    internal static bool IsSweepGateOpen(DateTime? armedAt, DateTime? lastSuccessfulFullImportCompletedAt)
    {
        if (armedAt is null)
            return false;

        return lastSuccessfulFullImportCompletedAt.HasValue && lastSuccessfulFullImportCompletedAt.Value > armedAt.Value;
    }

    /// <summary>
    /// The #1605 Functional Requirement 9 re-join shortfall predicate: whether the sweep must refuse rather
    /// than run, given how many of the objects recorded at the clear are still missing a re-join. Refuses
    /// only when there is something to compare against (<paramref name="recordedCount"/> greater than zero)
    /// and the missing share strictly exceeds <paramref name="maxMissingPercentThreshold"/>, computed by
    /// cross-multiplication so no rounding can move the boundary. Pure and static so the decision is
    /// directly testable without constructing a sweep.
    /// </summary>
    /// <param name="recordedCount">How many Metaverse Objects were recorded as joined at the clear.</param>
    /// <param name="missingCount">How many of those still lack a re-join.</param>
    /// <param name="maxMissingPercentThreshold">The configured maximum missing share, as a whole-number percentage.</param>
    internal static bool IsReconciliationRefused(int recordedCount, int missingCount, int maxMissingPercentThreshold)
    {
        if (recordedCount <= 0)
            return false;

        // Widened to long: cross-multiplication of two ordinary int counts cannot overflow int at any
        // realistic scale, but the widening costs nothing and removes the theoretical risk outright.
        return (long)missingCount * 100 > (long)maxMissingPercentThreshold * recordedCount;
    }

    /// <summary>
    /// Composes the sentence appended to the Full Synchronisation Activity's Message when the #1605
    /// Functional Requirement 9 shortfall check refuses the reconciliation. Internal and static so the
    /// wording is directly testable without constructing a sweep.
    /// </summary>
    /// <param name="missingCount">How many recorded objects still lack a re-join.</param>
    /// <param name="recordedCount">How many objects were recorded as joined at the clear.</param>
    /// <param name="armedAt">When the clear armed the sweep.</param>
    /// <param name="thresholdPercent">The configured maximum missing share, as a whole-number percentage.</param>
    internal static string BuildSweepRefusedMessage(int missingCount, int recordedCount, DateTime armedAt, int thresholdPercent)
    {
        var percent = recordedCount == 0 ? 0 : missingCount * 100 / recordedCount;

        return $"Stranded-value sweep refused: {missingCount:N0} of {recordedCount:N0} objects joined before the clear on " +
            $"{armedAt:yyyy-MM-dd HH:mm:ss} UTC have not returned ({percent}%), above the {thresholdPercent}% allowed by the " +
            "'Sync.PostClearReconciliation.MaxMissingPercent' setting. Re-import the Connected System, or raise the setting, " +
            "then run a Full Synchronisation.";
    }

    /// <summary>
    /// Composes the stranded-value sweep's summary sentence for the Full Synchronisation Activity's Message:
    /// that the sweep ran because a Connector Space clear armed it, and what it found across every pass
    /// (value recall, Deletion Rule evaluation, the zero-join pass), including the zero-findings case
    /// explicitly (#1549 Functional Requirement 11, extended by #1605 to cover the newer passes). Internal
    /// and static so the wording is directly testable without constructing a sweep.
    /// </summary>
    /// <param name="result">The completed sweep's counters.</param>
    internal static string BuildSweepActivityMessage(StrandedValueSweepResult result)
    {
        var recallClause = result.MetaverseObjectsProcessed == 0 && result.MetaverseObjectsPreserved == 0
            ? "no stranded values were found"
            : $"{result.ValuesRecalled:N0} stranded value(s) recalled across {result.MetaverseObjectsProcessed:N0} Metaverse Object(s) " +
              $"({result.AttributesReElected:N0} re-elected to a surviving contributor, {result.AttributesCleared:N0} cleared with no remaining contributor); " +
              $"{result.MetaverseObjectsPreserved:N0} Metaverse Object(s) preserved as last known state ({result.ValuesPreserved:N0} value(s))";

        var deletionRuleClause = $"{result.MetaverseObjectsEvaluatedForDeletion:N0} Metaverse Object(s) evaluated against their Deletion Rules: " +
            $"{result.MetaverseObjectsMarkedForDeletion:N0} marked for deletion, {result.MetaverseObjectsDeleted:N0} deleted; " +
            $"{result.MetaverseObjectsMarkedWithNoConnector:N0} object(s) with no connector remaining marked for deletion";

        return "Stranded-value sweep executed (armed by a Connector Space clear): " +
            $"{recallClause}; {deletionRuleClause}; {result.PendingExportsStaged:N0} Pending Export(s) staged.";
    }

    /// <summary>
    /// Composes the sentence appended to the Full Synchronisation Activity's Message when the #1605 gate is
    /// closed: the sweep stays armed, and nothing beyond ordinary synchronisation happens on this run.
    /// Internal and static so the wording is directly testable without constructing a sweep.
    /// </summary>
    /// <param name="armedAt">When the stranded-value sweep was armed.</param>
    internal static string BuildSweepSkippedMessage(DateTime armedAt)
    {
        return $"Stranded-value sweep armed by a Connector Space clear on {armedAt:yyyy-MM-dd HH:mm:ss} UTC; " +
            "skipped: no Full Import of this Connected System has completed successfully since. Run a Full " +
            "Import, then a Full Synchronisation, to reconcile objects that did not return.";
    }

    /// <summary>
    /// Appends a sweep sentence to a run's Activity Message, fixing the punctuation gap between the two: a
    /// message that does not already end with ".", "!" or "?" gets one inserted before the appended
    /// sentence, so "Sync complete: 2 objects" plus "Stranded-value sweep executed (...)" reads as "Sync
    /// complete: 2 objects. Stranded-value sweep executed (...)" rather than running the two together with
    /// only a space. A message that already ends with terminating punctuation is joined with a single space,
    /// unchanged from before. Internal and static so the composition is directly testable.
    /// </summary>
    /// <param name="existingMessage">The Activity's current Message, or null/empty when it has none yet.</param>
    /// <param name="sentence">The sweep's sentence (executed, skipped or refused) to append.</param>
    internal static string AppendSweepSentence(string? existingMessage, string sentence)
    {
        if (string.IsNullOrEmpty(existingMessage))
            return sentence;

        var separator = existingMessage.EndsWith('.') || existingMessage.EndsWith('!') || existingMessage.EndsWith('?')
            ? " "
            : ". ";
        return existingMessage + separator + sentence;
    }
}
