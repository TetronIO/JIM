// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// What a prepared batch needs of another batch's rows (#2924): when a page's history write fails, the next page's batch
/// is written only if it stands alone, so <see cref="SynchronisationHistoryBatch.FindDependencyOn"/> names any row of it
/// that refers to a row only the failed batch would have written. Also the referenced Connected System Objects a batch's
/// change values name, which the run looks up before it hands the batch off.
/// </summary>
[TestFixture]
public class SynchronisationHistoryBatchDependencyTests
{
    private static SynchronisationHistoryBatch Batch(
        IReadOnlyList<ActivityRunProfileExecutionItem>? items = null,
        IReadOnlyList<ActivityRunProfileExecutionItemSyncOutcome>? outcomes = null,
        IReadOnlyList<ConnectedSystemObjectChange>? outcomeChangeRecords = null,
        IReadOnlyList<ConnectedSystemObjectChange>? itemChangeRecords = null,
        IReadOnlyList<CausalEdge>? edges = null,
        IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate>? candidates = null,
        IReadOnlyList<MetaverseObjectChange>? metaverseObjectChanges = null,
        IReadOnlyList<MetaverseObjectChange>? appends = null) => new()
    {
        Items = items ?? [],
        ItemIdsWrittenWithDeletion = new HashSet<Guid>(),
        Outcomes = outcomes ?? [],
        OutcomeChangeRecords = outcomeChangeRecords ?? [],
        ItemChangeRecords = itemChangeRecords ?? [],
        CausalEdges = edges ?? [],
        MatchCandidates = candidates ?? [],
        MetaverseObjectChanges = metaverseObjectChanges ?? [],
        MetaverseObjectChangeAppends = appends ?? [],
        CounterDeltas = new Dictionary<ActivityStatCounterKey, long>(),
        Page = 1,
        TotalPages = 2
    };

    private static ActivityRunProfileExecutionItem Item() => new() { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid() };

    /// <summary>The failed page's batch: an item with an outcome, its change records and a Metaverse Object change.</summary>
    private static (SynchronisationHistoryBatch Batch, ActivityRunProfileExecutionItem Item, ActivityRunProfileExecutionItemSyncOutcome Outcome,
        ConnectedSystemObjectChange OutcomeChange, MetaverseObjectChange MetaverseObjectChange) FailedPage()
    {
        var item = Item();
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id };
        var outcomeChange = new ConnectedSystemObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id };
        var mvoChange = new MetaverseObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id };
        return (Batch([item], [outcome], [outcomeChange], [], [], [], [mvoChange]), item, outcome, outcomeChange, mvoChange);
    }

    [Test]
    public void FindDependencyOn_NextPageOfItsOwn_IsNull()
    {
        var failed = FailedPage();
        var item = Item();
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id };
        var next = Batch([item], [outcome],
            edges: [new CausalEdge { Id = Guid.NewGuid(), EffectRunProfileExecutionItemId = item.Id, EffectSyncOutcomeId = outcome.Id }],
            metaverseObjectChanges: [new MetaverseObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id }]);

        Assert.That(next.FindDependencyOn(failed.Batch), Is.Null);
    }

    private static IEnumerable<TestCaseData> Dependencies()
    {
        yield return Case("an outcome's execution item", f => Batch(
            outcomes: [new ActivityRunProfileExecutionItemSyncOutcome { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = f.Item.Id }]));
        yield return Case("an outcome's parent outcome", f =>
        {
            var item = Item();
            return Batch([item], [new ActivityRunProfileExecutionItemSyncOutcome
                { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id, ParentSyncOutcomeId = f.Outcome.Id }]);
        });
        yield return Case("an outcome's change record", f =>
        {
            var item = Item();
            return Batch([item], [new ActivityRunProfileExecutionItemSyncOutcome
                { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = item.Id, ConnectedSystemObjectChangeId = f.OutcomeChange.Id }]);
        });
        yield return Case("a change record's execution item", f => Batch(
            itemChangeRecords: [new ConnectedSystemObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = f.Item.Id }]));
        yield return Case("a causal edge's cause", f =>
        {
            var item = Item();
            return Batch([item], edges: [new CausalEdge { Id = Guid.NewGuid(), EffectRunProfileExecutionItemId = item.Id, CauseRunProfileExecutionItemId = f.Item.Id }]);
        });
        yield return Case("a causal edge's cause outcome", f =>
        {
            var item = Item();
            return Batch([item], edges: [new CausalEdge { Id = Guid.NewGuid(), EffectRunProfileExecutionItemId = item.Id, CauseSyncOutcomeId = f.Outcome.Id }]);
        });
        yield return Case("a causal edge's effect", f => Batch(
            edges: [new CausalEdge { Id = Guid.NewGuid(), EffectRunProfileExecutionItemId = f.Item.Id }]));
        yield return Case("a match candidate's execution item", f => Batch(
            candidates: [new ActivityRunProfileExecutionItemMatchCandidate { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = f.Item.Id }]));
        yield return Case("a Metaverse Object change record's execution item", f => Batch(
            metaverseObjectChanges: [new MetaverseObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItemId = f.Item.Id }]));
        yield return Case("an append to a Metaverse Object change record", f => Batch(
            appends: [new MetaverseObjectChange { Id = f.MetaverseObjectChange.Id }]));

        static TestCaseData Case(string what, Func<(SynchronisationHistoryBatch Batch, ActivityRunProfileExecutionItem Item,
            ActivityRunProfileExecutionItemSyncOutcome Outcome, ConnectedSystemObjectChange OutcomeChange, MetaverseObjectChange MetaverseObjectChange),
            SynchronisationHistoryBatch> next) =>
            new TestCaseData(what, next).SetName($"FindDependencyOn_{what.Replace(' ', '_').Replace("'", "")}_NamesIt");
    }

    [TestCaseSource(nameof(Dependencies))]
    public void FindDependencyOn_RowReferringToTheFailedBatch_NamesIt(string what, Func<(SynchronisationHistoryBatch Batch, ActivityRunProfileExecutionItem Item,
        ActivityRunProfileExecutionItemSyncOutcome Outcome, ConnectedSystemObjectChange OutcomeChange, MetaverseObjectChange MetaverseObjectChange),
        SynchronisationHistoryBatch> next)
    {
        var failed = FailedPage();

        Assert.That(next(failed).FindDependencyOn(failed.Batch), Does.StartWith(what));
    }

    [Test]
    public void ConnectedSystemObjectReferenceIds_ChangeValues_ListsEachReferencedObjectOnce()
    {
        var referenced = Guid.NewGuid();
        var other = Guid.NewGuid();
        ConnectedSystemObjectChange Change(params Guid?[] references)
        {
            var change = new ConnectedSystemObjectChange { Id = Guid.NewGuid() };
            var attribute = new ConnectedSystemObjectChangeAttribute { Id = Guid.NewGuid(), AttributeType = AttributeDataType.Reference };
            foreach (var reference in references)
                attribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue { Id = Guid.NewGuid(), ReferenceValueId = reference });
            change.AttributeChanges.Add(attribute);
            return change;
        }

        var batch = Batch(outcomeChangeRecords: [Change(referenced, null)], itemChangeRecords: [Change(referenced, other, Guid.Empty)]);

        Assert.That(batch.ConnectedSystemObjectReferenceIds(), Is.EquivalentTo(new[] { referenced, other }));
    }

    [Test]
    public void RecordConnectedSystemObjectReferencesFound_KeepsWhatWasFound()
    {
        var batch = Batch();
        var found = new HashSet<Guid> { Guid.NewGuid() };

        Assert.That(batch.ConnectedSystemObjectReferencesFound, Is.Null, "nothing looked up yet: the writer looks them up itself");
        batch.RecordConnectedSystemObjectReferencesFound(found);

        Assert.That(batch.ConnectedSystemObjectReferencesFound!.ToList(), Is.EquivalentTo(found));
    }
}
