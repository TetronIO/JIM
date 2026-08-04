// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One recorded cause, held in memory by the worker between the moment the causing event happens and the
/// moment the effect it explains is created, so a <see cref="CausalEdge"/> can be written for the pair (#1223).
/// </summary>
/// <remarks>
/// Causes and effects routinely happen far apart. Reference recall is the extreme case: a group's Pending
/// Export is emitted once at end of run, while the member deletions that caused it happened on any number of
/// earlier pages. So the causing side is captured where it is known and carried forward.
///
/// It carries object <b>references</b> rather than ids for the two run-scoped records, because neither has an
/// id when the cause is captured: a Run Profile Execution Item is assigned one by the flush, and a sync outcome
/// likewise. Reading them through the reference at edge-creation time gets the assigned values, exactly as the
/// effect side does. Everything else is a snapshot scalar, which is the point: a cause must still read
/// correctly after the object, the Connected System or the Run Profile Execution Item recording it is gone.
/// </remarks>
public class CausalCause
{
    /// <summary>
    /// The Run Profile Execution Item that recorded the causing event, if one did.
    /// </summary>
    public ActivityRunProfileExecutionItem? RunProfileExecutionItem { get; init; }

    /// <summary>
    /// The specific sync outcome node that was the causing event, if one was recorded.
    /// </summary>
    public ActivityRunProfileExecutionItemSyncOutcome? SyncOutcome { get; init; }

    /// <summary>
    /// The Metaverse Object that was the cause, where the cause is best identified by object rather than by
    /// event (a deletion cascade names the deleted object).
    /// </summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>
    /// The Connected System Object that was the cause, where the cause is best identified by object.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>
    /// How the cause was named at the time, so a chain still reads sensibly after the cause is purged.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Why the cause produced its effect. Part of the attribution tuple cohorts group on.
    /// </summary>
    public CausalReasonCode ReasonCode { get; init; }

    /// <summary>
    /// The Connected System the cause occurred on, where one applies. Part of the attribution tuple.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// The Connected System's name at the time, so the chain reads correctly after a rename or deletion.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// The Synchronisation Rule responsible, where one applies. Part of the attribution tuple.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// The Synchronisation Rule's name at the time.
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// Builds the edge recording that this cause produced <paramref name="effectOutcome"/>, resolving the
    /// run-scoped ids through the references held since capture.
    /// </summary>
    /// <param name="edgeType">Which cascade seam the pair sits on.</param>
    /// <param name="effectOutcome">The outcome node the effect was recorded as, where there is one. The edge
    /// carries it as a transient reference; the flush resolves it to an id.</param>
    public CausalEdge ToEdge(CausalEdgeType edgeType, ActivityRunProfileExecutionItemSyncOutcome? effectOutcome)
    {
        return new CausalEdge
        {
            EffectSyncOutcome = effectOutcome,
            // An id of Guid.Empty means the causing item was never flushed, which is not a cause anyone can
            // navigate to; store null rather than a reference that resolves to nothing.
            CauseRunProfileExecutionItemId = RunProfileExecutionItem != null && RunProfileExecutionItem.Id != Guid.Empty
                ? RunProfileExecutionItem.Id
                : null,
            CauseSyncOutcomeId = SyncOutcome != null && SyncOutcome.Id != Guid.Empty ? SyncOutcome.Id : null,
            CauseMetaverseObjectId = MetaverseObjectId,
            CauseConnectedSystemObjectId = ConnectedSystemObjectId,
            CauseDisplayName = DisplayName,
            EdgeType = edgeType,
            ReasonCode = ReasonCode,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemName = ConnectedSystemName,
            SyncRuleId = SyncRuleId,
            SyncRuleName = SyncRuleName
        };
    }
}
