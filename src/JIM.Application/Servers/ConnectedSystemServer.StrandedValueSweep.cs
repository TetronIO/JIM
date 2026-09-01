// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The executor for a flag-gated stranded-value sweep (#1549): a Connector Space clear hard-deletes
/// Connected System Objects without obsoletion, so Metaverse attribute values the cleared system
/// contributed survive with live provenance and no joined Connected System Object of that system,
/// indefinitely stranded until something recalls them. The next Full Synchronisation of the cleared system
/// reads <see cref="ConnectedSystem.StrandedValueSweepPending"/> and, when set, runs this sweep after its
/// ordinary passes: per import Synchronisation Rule (enabled and disabled alike), selects the stranded
/// candidates by provenance-plus-join-absence and recalls them through the shipped #1537/#809 recall
/// engine under the <see cref="ContributorRecallScope.ForStrandedContribution"/> scope, then clears the
/// flag. See <see cref="ExecuteStrandedValueSweepAsync"/>.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Executes the stranded-value sweep for one Connected System, once its Full Synchronisation run has
    /// completed its ordinary passes. Refuses (fast, hard) unless the caller has already confirmed the
    /// system is armed (<see cref="ConnectedSystem.StrandedValueSweepPending"/> true): the flag read is the
    /// caller's job (Full Synchronisation pays one boolean read on every other run, and must not construct
    /// the sweep's support set unless the flag says there is work to do), so a call here without it is a
    /// caller defect, not a normal "nothing to do" outcome.
    /// <para>
    /// Sweeps EVERY import Synchronisation Rule of the system, enabled and disabled alike: the sweep
    /// substitutes for the obsoletion that never ran when the Connector Space was cleared, and obsoletion
    /// recalls by system regardless of rule enablement. The #1537 disable-retention doctrine protects a
    /// paused flow whose source object remains; here the source object is gone, which is a different
    /// question entirely. A rule is skipped only when its Connected System Object Type has
    /// RemoveContributedAttributesOnObsoletion disabled: those retained values are policy, not strands, and
    /// the sweep must not override an administrator's deliberate choice.
    /// </para>
    /// <para>
    /// Metaverse Objects pending deletion are left untouched (their single-source values were already
    /// frozen for the grace window by whatever triggered the pending deletion, and housekeeping owns their
    /// removal); the sweep does not evaluate Metaverse Object Deletion Rules itself.
    /// </para>
    /// <para>
    /// Does not complete the Activity or set its Message: the caller (the Full Synchronisation task
    /// processor) owns run-level messaging and completion. Execution items are recorded per Metaverse
    /// Object as the recall executor processes them, the zero-findings case included (nothing is recorded
    /// when a rule has no stranded candidates at all).
    /// </para>
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose Connector Space was cleared; must be armed.</param>
    /// <param name="activity">The Full Synchronisation run's Activity to record execution items against.</param>
    public async Task<StrandedValueSweepResult> ExecuteStrandedValueSweepAsync(ConnectedSystem connectedSystem, Activity activity)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(activity);
        if (!connectedSystem.StrandedValueSweepPending)
            throw new InvalidDataException(
                $"ExecuteStrandedValueSweepAsync: Connected System {connectedSystem.Id} is not armed " +
                "(StrandedValueSweepPending is false); refusing to sweep. The caller must check the flag " +
                "before invoking the sweep.");

        var result = new StrandedValueSweepResult();

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

        // Clear the flag: the repository update is deliberately immune to context tracking (its own
        // contract), so the caller's in-memory instance is updated directly here too, letting the Full
        // Synchronisation task processor observe the change without a re-fetch.
        await Application.Repository.ConnectedSystems.SetStrandedValueSweepPendingAsync(connectedSystem.Id, pending: false);
        connectedSystem.StrandedValueSweepPending = false;

        Log.Information(
            "ExecuteStrandedValueSweepAsync: Connected System {ConnectedSystemId}: {RuleCount} Synchronisation Rule(s) swept, " +
            "{ObjectCount} Metaverse Object(s) processed, {ValueCount} value(s) recalled, {ReElectedCount} attribute(s) re-elected, " +
            "{ClearedCount} attribute(s) cleared, {PreservedObjectCount} Metaverse Object(s) preserved ({PreservedValueCount} value(s)), " +
            "{PendingExportCount} Pending Export(s) staged.",
            connectedSystem.Id, result.SyncRulesSwept, result.MetaverseObjectsProcessed, result.ValuesRecalled,
            result.AttributesReElected, result.AttributesCleared, result.MetaverseObjectsPreserved, result.ValuesPreserved,
            result.PendingExportsStaged);

        return result;
    }
}
