// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Sync;

namespace JIM.Application.Services;

/// <summary>
/// Evaluates a Metaverse Object's type Deletion Rule via <see cref="SyncEngine.EvaluateMvoDeletionRule"/> and
/// applies the decision's marking fields to the object, building the decision-time policy snapshot (#119)
/// along the way. Shared by Synchronised Deprovisioning's per-object obsoletion pass
/// (<c>ConnectedSystemServer.SynchronisedDeprovisioning.cs</c>) and the post-clear reconciliation sweep's
/// evaluation of recorded objects that did not return (#1605), so the marking rules are written once rather
/// than drifting between two copies.
/// <para>
/// <see cref="Apply"/> only mutates the passed Metaverse Object in memory; it never persists anything and it
/// never queues an immediate delete. The caller decides what happens next: a
/// <see cref="MvoDeletionFate.DeletionScheduled"/> object still needs
/// <c>ISyncRepository.UpdateMetaverseObjectsAsync</c> to persist the marking, and a
/// <see cref="MvoDeletionFate.DeletedImmediately"/> object still needs to be queued into the caller's own
/// #809 batch flush (capture reference-recall context, evaluate deletions, delete, stage reference-recall
/// exports). A <see cref="MvoDeletionFate.NotDeleted"/> decision mutates nothing.
/// </para>
/// </summary>
public static class MetaverseObjectDeletionRuleApplier
{
    /// <summary>
    /// Evaluates <paramref name="mvo"/>'s type Deletion Rule against the disconnection of
    /// <paramref name="disconnectingSystemId"/> and, for a fate other than
    /// <see cref="MvoDeletionFate.NotDeleted"/>, sets the deletion-marker fields (grace fates only:
    /// <c>LastConnectorDisconnectedDate</c>, the initiator triad, the policy snapshot) plus
    /// <c>DeletionTriggeredBySystemId</c>/<c>Name</c> on both marked fates.
    /// </summary>
    /// <param name="syncEngine">The pure decision engine.</param>
    /// <param name="mvo">The Metaverse Object being evaluated; mutated in place for a triggered fate.</param>
    /// <param name="disconnectingSystemId">The Connected System whose disconnection is being evaluated.</param>
    /// <param name="remainingConnectedSystemIds">One entry per Connected System Object still joined to the
    /// object after the disconnection (duplicates per system deliberate).</param>
    /// <param name="systemNamesById">Connected System display names, for the human-readable reason and the
    /// policy snapshot's source-system names.</param>
    /// <param name="fallbackSystemName">The name to use for <paramref name="disconnectingSystemId"/> when it
    /// is absent from <paramref name="systemNamesById"/>.</param>
    /// <param name="initiatedByType">The run's initiator type, recorded on a grace marking.</param>
    /// <param name="initiatedById">The run's initiator id, when the initiator is a Person.</param>
    /// <param name="initiatedByName">The run's initiator display name snapshot.</param>
    public static (MvoDeletionDecision Decision, string? PolicySnapshotJson) Apply(
        SyncEngine syncEngine,
        MetaverseObject mvo,
        int disconnectingSystemId,
        IReadOnlyCollection<int> remainingConnectedSystemIds,
        IReadOnlyDictionary<int, string> systemNamesById,
        string fallbackSystemName,
        ActivityInitiatorType initiatedByType,
        Guid? initiatedById,
        string? initiatedByName)
    {
        ArgumentNullException.ThrowIfNull(syncEngine);
        ArgumentNullException.ThrowIfNull(mvo);

        var disconnectingSystemName = systemNamesById.GetValueOrDefault(disconnectingSystemId, fallbackSystemName);
        var decision = syncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingConnectedSystemIds, disconnectingSystemName);
        var policySnapshotJson = BuildPolicySnapshotJson(mvo, disconnectingSystemId, remainingConnectedSystemIds, decision, systemNamesById, disconnectingSystemName);

        switch (decision.Fate)
        {
            case MvoDeletionFate.DeletedImmediately:
                mvo.DeletionTriggeredBySystemId = disconnectingSystemId;
                mvo.DeletionTriggeredBySystemName = disconnectingSystemName;
                break;

            case MvoDeletionFate.DeletionScheduled:
                // Grace period configured: mark for deferred deletion by housekeeping, capturing the
                // initiator triad and the decision-time policy snapshot (#119) exactly as the worker path
                // does for a synchronisation-triggered marking.
                mvo.DeletionTriggeredBySystemId = disconnectingSystemId;
                mvo.DeletionTriggeredBySystemName = disconnectingSystemName;
                mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
                mvo.DeletionInitiatedByType = initiatedByType;
                mvo.DeletionInitiatedById = initiatedById;
                mvo.DeletionInitiatedByName = initiatedByName;
                mvo.DeletionPolicySnapshotJson = policySnapshotJson;
                break;
        }

        return (decision, policySnapshotJson);
    }

    /// <summary>
    /// Builds the serialised decision-time deletion policy snapshot (#119) for a deletion rule evaluation:
    /// produced whenever the evaluation records an outcome (triggered, or evaluated against the source list
    /// without triggering); null for a plain non-event or an untyped Metaverse Object.
    /// </summary>
    private static string? BuildPolicySnapshotJson(
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
