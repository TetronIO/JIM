// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// The history a synchronisation flush records, prepared for writing (#2874): the Run Profile Execution Items with
/// their outcomes, the Connected System Object and Metaverse Object change records, the causal edges, the ambiguous
/// match candidates and the stat counter deltas, every id already assigned. One batch is written in one transaction, so
/// a failed write leaves none of it behind.
/// </summary>
/// <remarks>
/// <para>
/// Built by <c>ISyncRepository.PrepareSynchronisationHistory</c> on the thread that runs the synchronisation, and
/// written by <c>ISyncRepository.WriteSynchronisationHistoryAsync</c>, which assigns nothing and reads nothing from the
/// objects the run is still working on. Ownership passes with the batch: once it is handed to a writer, the run does not
/// touch the objects it holds until the write has been waited for, and the writer touches nothing outside it.
/// </para>
/// <para>
/// Nothing here names a Connected System Object or Metaverse Object other than by id; configuration the run shares
/// (attributes, Synchronisation Rules, Object Types) is read-only for the run and may still be reachable.
/// </para>
/// </remarks>
public sealed class SynchronisationHistoryBatch
{
    /// <summary>
    /// The execution items, those whose rows a deletion already wrote included.
    /// </summary>
    public required IReadOnlyList<ActivityRunProfileExecutionItem> Items { get; init; }

    /// <summary>
    /// The ids of the items whose own rows, and own change records, a Connected System Object deletion wrote in its
    /// transaction (#2807): their rows are brought up to date rather than inserted, and their change records are not
    /// written again. Everything else of theirs is written as for any other item.
    /// </summary>
    public required IReadOnlySet<Guid> ItemIdsWrittenWithDeletion { get; init; }

    /// <summary>
    /// Every item's outcome tree, flattened, each node pointed at its item and its parent.
    /// </summary>
    public required IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcome> Outcomes { get; init; }

    /// <summary>
    /// The change records the outcomes carry: what a staged Pending Export would change, snapshotted when it was staged.
    /// </summary>
    public required IReadOnlyList<ConnectedSystemObjectChange> OutcomeChangeRecords { get; init; }

    /// <summary>
    /// The items' own change records (a provisioned Connected System Object's Added record, say), less those of the
    /// items a deletion already wrote; empty when Connected System Object change tracking is off.
    /// </summary>
    public required IReadOnlyList<ConnectedSystemObjectChange> ItemChangeRecords { get; init; }

    /// <summary>
    /// The causal edges the items carry (#1223), every reference resolved to an id.
    /// </summary>
    public required IReadOnlyList<CausalEdge> CausalEdges { get; init; }

    /// <summary>
    /// The ambiguous match candidates the items carry (#2052).
    /// </summary>
    public required IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate> MatchCandidates { get; init; }

    /// <summary>
    /// New Metaverse Object change records, each with its attribute and value rows.
    /// </summary>
    public required IReadOnlyList<MetaverseObjectChange> MetaverseObjectChanges { get; init; }

    /// <summary>
    /// Attribute and value rows appended to Metaverse Object change records written by an earlier flush of the same run
    /// (the cross-page reference step's): each carries the id of the record it extends.
    /// </summary>
    public required IReadOnlyList<MetaverseObjectChange> MetaverseObjectChangeAppends { get; init; }

    /// <summary>
    /// The Activity stat counter deltas the items and outcomes add (#1078).
    /// </summary>
    public required IReadOnlyDictionary<ActivityStatCounterKey, long> CounterDeltas { get; init; }

    /// <summary>
    /// The 1-based page the batch records, or 0 for a flush outside the page loop (the cross-page reference step, the
    /// export scope review, deferred recall items and the like).
    /// </summary>
    public int Page { get; init; }

    /// <summary>
    /// The pages the run expects; 0 alongside a <see cref="Page"/> of 0.
    /// </summary>
    public int TotalPages { get; init; }

    /// <summary>
    /// Whether the batch records nothing at all.
    /// </summary>
    public bool IsEmpty => Items.Count == 0 && MetaverseObjectChanges.Count == 0 && MetaverseObjectChangeAppends.Count == 0;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when any row the batch holds has no id: a writer assigns none, so
    /// the batch was not prepared, or was changed after it was. Writers call this before writing anything.
    /// </summary>
    /// <param name="writer">The writer, named in the message.</param>
    public void RequireEveryIdAssigned(string writer)
    {
        void Require(bool assigned, string what)
        {
            if (!assigned)
                throw new InvalidOperationException(
                    $"{writer} assigns no id, and {what} has none: prepare the batch with PrepareSynchronisationHistory.");
        }

        foreach (var item in Items)
        {
            Require(item.Id != Guid.Empty, "an execution item");
            Require(item.ActivityId != Guid.Empty, "an execution item's Activity");
        }

        foreach (var outcome in Outcomes)
        {
            Require(outcome.Id != Guid.Empty, "an outcome");
            Require(outcome.ActivityRunProfileExecutionItemId != Guid.Empty, "an outcome's execution item");
        }

        foreach (var change in OutcomeChangeRecords.Concat(ItemChangeRecords))
        {
            Require(change.Id != Guid.Empty, "a Connected System Object change record");
            foreach (var attribute in change.AttributeChanges)
            {
                Require(attribute.Id != Guid.Empty, "a Connected System Object change record's attribute row");
                Require(attribute.ValueChanges.All(v => v.Id != Guid.Empty), "a Connected System Object change record's value row");
            }
        }

        foreach (var edge in CausalEdges)
        {
            Require(edge.Id != Guid.Empty, "a causal edge");
            Require(edge.EffectRunProfileExecutionItemId != Guid.Empty, "a causal edge's effect");
        }

        foreach (var candidate in MatchCandidates)
        {
            Require(candidate.Id != Guid.Empty, "a match candidate");
            Require(candidate.ActivityRunProfileExecutionItemId != Guid.Empty, "a match candidate's execution item");
        }

        foreach (var change in MetaverseObjectChanges.Concat(MetaverseObjectChangeAppends))
        {
            Require(change.Id != Guid.Empty, "a Metaverse Object change record");
            foreach (var attribute in change.AttributeChanges)
            {
                Require(attribute.Id != Guid.Empty, "a Metaverse Object change record's attribute row");
                Require(attribute.ValueChanges.All(v => v.Id != Guid.Empty), "a Metaverse Object change record's value row");
            }
        }
    }
}
