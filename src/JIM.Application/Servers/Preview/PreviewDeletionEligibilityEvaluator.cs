// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Preview;
using JIM.Models.Sync;
using JIM.Utilities;
using System.Runtime.CompilerServices;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// The Metaverse consequence shared by every preview adapter whose proposal disconnects objects: which Metaverse
/// Objects would become eligible for automatic deletion once the proposed disconnections land, decided by putting
/// the question to the synchronisation engine's own deletion rule.
///
/// The rule is intricate: two trigger modes, a fallback when the authoritative-source rule names no sources, and
/// an exemption for internal objects. Reimplementing it in an adapter would produce a preview that eventually
/// disagreed with the engine about whether an object dies, and having it in two adapters would let the two
/// previews disagree with each other, so both put the identical question to the identical code here.
/// </summary>
internal static class PreviewDeletionEligibilityEvaluator
{
    /// <summary>
    /// How a deletion-eligibility transition is written into a delta row's value columns.
    /// </summary>
    internal const string DeletionEligibilityAttributeName = "Deletion eligibility";
    internal const string NotDeletionEligible = "Not eligible for deletion";
    internal const string DeletionEligible = "Eligible for deletion";

    /// <summary>
    /// Yields a <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible"/> delta for
    /// each Metaverse Object the proposed disconnections would make eligible for automatic deletion, with the
    /// state the proposal would leave behind.
    /// </summary>
    /// <param name="disconnectionsByMetaverseObject">
    /// One entry per Metaverse Object that would lose a connector, counting how many of its Connected System
    /// Objects in the disconnecting system leave; a system holding two joined objects where one stays is still a
    /// connector.
    /// </param>
    internal static async IAsyncEnumerable<PreviewDelta> EvaluateAsync(
        JimApplication application,
        ISyncEngine syncEngine,
        int disconnectingSystemId,
        Dictionary<Guid, int> disconnectionsByMetaverseObject,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (disconnectionsByMetaverseObject.Count == 0)
            yield break;

        var candidates = await application.Metaverse.GetMetaverseObjectDisconnectionCandidatesAsync(
            disconnectionsByMetaverseObject.Keys);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = RemainingConnectorsAfterDisconnection(
                candidate, disconnectingSystemId, disconnectionsByMetaverseObject[candidate.Id]);

            var decision = syncEngine.EvaluateMvoDeletionRule(ToEvaluableObject(candidate), disconnectingSystemId, remaining);
            if (decision.Fate == MvoDeletionFate.NotDeleted)
                continue;

            yield return new PreviewDelta(
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
                ObjectDisplayName: candidate.DisplayName,
                ObjectTypeName: candidate.TypeName,
                MetaverseObjectTypeId: candidate.TypeId,
                MetaverseObjectId: candidate.Id,
                ConnectedSystemId: disconnectingSystemId,
                AttributeName: DeletionEligibilityAttributeName,
                OldValue: NotDeletionEligible,
                NewValue: DescribeDeletionOutcome(decision));
        }
    }

    /// <summary>
    /// The Connected Systems that would still hold an object joined to this Metaverse Object, one entry per joined
    /// object because that is what the engine counts.
    /// </summary>
    private static List<int> RemainingConnectorsAfterDisconnection(
        MetaverseObjectDisconnectionCandidate candidate, int disconnectingSystemId, int disconnectingCount)
    {
        var remaining = new List<int>(candidate.JoinedConnectedSystemIds.Count);
        var stillToRemove = disconnectingCount;

        foreach (var systemId in candidate.JoinedConnectedSystemIds)
        {
            // Only the disconnecting system's entries are removed, and only as many as actually leave scope: a
            // system holding two joined objects where one stays is still a connector.
            if (systemId == disconnectingSystemId && stillToRemove > 0)
                stillToRemove--;
            else
                remaining.Add(systemId);
        }

        return remaining;
    }

    /// <summary>
    /// The candidate as the shape the engine's deletion rule reads: its origin, and its type's deletion settings.
    /// Nothing is persisted and nothing is loaded; this exists so the preview and a synchronisation run put the
    /// identical question to the identical code.
    /// </summary>
    private static MetaverseObject ToEvaluableObject(MetaverseObjectDisconnectionCandidate candidate) => new()
    {
        Id = candidate.Id,
        Origin = candidate.Origin,
        Type = new MetaverseObjectType
        {
            Id = candidate.TypeId,
            Name = candidate.TypeName,
            DeletionRule = candidate.DeletionRule,
            DeletionTriggerMode = candidate.DeletionTriggerMode,
            DeletionGracePeriod = candidate.DeletionGracePeriod,
            DeletionTriggerConnectedSystemIds = [.. candidate.DeletionTriggerConnectedSystemIds]
        }
    };

    private static string DescribeDeletionOutcome(MvoDeletionDecision decision) =>
        decision is { Fate: MvoDeletionFate.DeletionScheduled, GracePeriod: { } grace }
            ? $"{DeletionEligible} after {grace.ToFriendlyDuration()}"
            : $"{DeletionEligible} immediately";
}
