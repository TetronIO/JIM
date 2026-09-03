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
/// The executor for a gated stranded-value sweep (#1549, gated on a genuine re-import by #1605): a
/// Connector Space clear hard-deletes Connected System Objects without obsoletion, so Metaverse attribute
/// values the cleared system contributed survive with live provenance and no joined Connected System
/// Object of that system, indefinitely stranded until something recalls them. The next Full Synchronisation
/// of the cleared system reads <see cref="ConnectedSystem.StrandedValueSweepArmedAt"/> and, once a Full
/// Import of the system has completed successfully later than that arming (see
/// <see cref="IsSweepGateOpen"/>), runs this sweep after its ordinary passes: per import Synchronisation
/// Rule (enabled and disabled alike), selects the stranded candidates by provenance-plus-join-absence and
/// recalls them through the shipped #1537/#809 recall engine under the
/// <see cref="ContributorRecallScope.ForStrandedContribution"/> scope, then clears the arming. A Full
/// Synchronisation run while the gate is closed leaves the arming in place instead; see
/// <see cref="ExecuteStrandedValueSweepIfArmedAsync"/>.
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
        if (connectedSystem.StrandedValueSweepArmedAt is null)
            throw new InvalidDataException(
                $"ExecuteStrandedValueSweepAsync: Connected System {connectedSystem.Id} is not armed " +
                "(StrandedValueSweepArmedAt is null); refusing to sweep. The caller must check the arming, " +
                "and the #1605 gate, before invoking the sweep.");

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

        // Clear the arming: the repository update is deliberately immune to context tracking (its own
        // contract), so the caller's in-memory instance is updated directly here too, letting the Full
        // Synchronisation task processor observe the change without a re-fetch.
        await Application.Repository.ConnectedSystems.SetStrandedValueSweepArmedAtAsync(connectedSystem.Id, armedAt: null);
        connectedSystem.StrandedValueSweepArmedAt = null;

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

    /// <summary>
    /// The caller-facing entry point for a Full Synchronisation run: reads
    /// <see cref="ConnectedSystem.StrandedValueSweepArmedAt"/> and returns null immediately when it is null,
    /// so every ordinary run (the overwhelming majority) pays exactly one nullable-timestamp read and never
    /// constructs the sweep's support set. When armed, the #1605 Full Import gate decides what happens next:
    /// <list type="bullet">
    /// <item>Gate closed (no Full Import of this system has completed successfully later than the arming):
    /// the sweep does not run, the arming is left in place, and a sentence stating why is appended to the
    /// Activity Message. The returned result carries <see cref="StrandedValueSweepResult.Skipped"/> true.</item>
    /// <item>Gate open: runs <see cref="ExecuteStrandedValueSweepAsync"/>, appends its outcome to the run's
    /// Activity Message as a new sentence, and clears the arming as that method already does.</item>
    /// </list>
    /// Either way the Activity is persisted and the result is returned so the caller can log or report on
    /// it further if it chooses to.
    /// </summary>
    /// <param name="connectedSystem">The Connected System whose Full Synchronisation run has just completed
    /// its ordinary passes.</param>
    /// <param name="activity">The Full Synchronisation run's Activity; its Message gains the sweep's summary
    /// sentence when the sweep runs, or the skipped sentence when the gate is closed.</param>
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
            activity.Message = string.IsNullOrEmpty(activity.Message)
                ? skippedResult.SkipReason
                : activity.Message + " " + skippedResult.SkipReason;
            await Application.Repository.Activity.UpdateActivityAsync(activity);

            return skippedResult;
        }

        var result = await ExecuteStrandedValueSweepAsync(connectedSystem, activity);

        var sweepMessage = BuildSweepActivityMessage(result);
        activity.Message = string.IsNullOrEmpty(activity.Message) ? sweepMessage : activity.Message + " " + sweepMessage;
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
    /// Composes the stranded-value sweep's summary sentence for the Full Synchronisation Activity's Message:
    /// that the sweep ran because a Connector Space clear armed it, and what it found, including the
    /// zero-findings case explicitly (#1549 Functional Requirement 11). Internal and static so the wording
    /// is directly testable without constructing a sweep.
    /// </summary>
    /// <param name="result">The completed sweep's counters.</param>
    internal static string BuildSweepActivityMessage(StrandedValueSweepResult result)
    {
        if (result.MetaverseObjectsProcessed == 0 && result.MetaverseObjectsPreserved == 0)
            return "Stranded-value sweep executed (armed by a Connector Space clear): no stranded values were found.";

        return "Stranded-value sweep executed (armed by a Connector Space clear): " +
            $"{result.ValuesRecalled:N0} stranded value(s) recalled across {result.MetaverseObjectsProcessed:N0} Metaverse Object(s) " +
            $"({result.AttributesReElected:N0} re-elected to a surviving contributor, {result.AttributesCleared:N0} cleared with no remaining contributor); " +
            $"{result.MetaverseObjectsPreserved:N0} Metaverse Object(s) preserved as last known state ({result.ValuesPreserved:N0} value(s)); " +
            $"{result.PendingExportsStaged:N0} Pending Export(s) staged.";
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
}
