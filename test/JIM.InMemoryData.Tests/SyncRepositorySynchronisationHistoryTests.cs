// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;

namespace JIM.InMemoryData.Tests;

/// <summary>
/// The in-memory store's synchronisation history preparation and write (#2874): what the production store records,
/// recorded the same way here, and the store written on the run's own thread because its collections are not safe to
/// use from two.
/// </summary>
[TestFixture]
public class SyncRepositorySynchronisationHistoryTests
{
    private SyncRepository _repo = null!;

    [SetUp]
    public void SetUp() => _repo = new SyncRepository();

    private static (ActivityRunProfileExecutionItem Item, CausalEdge Edge, ActivityRunProfileExecutionItemMatchCandidate Candidate, MetaverseObjectChange Change)
        NewFlush()
    {
        var item = new ActivityRunProfileExecutionItem { ActivityId = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Projected };
        var root = new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected };
        var child = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, ParentSyncOutcome = root
        };
        root.Children.Add(child);
        item.SyncOutcomes.Add(root);
        item.SyncOutcomes.Add(child);
        var edge = new CausalEdge { EffectSyncOutcome = child, EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval };
        item.CausalEdges.Add(edge);
        var candidate = new ActivityRunProfileExecutionItemMatchCandidate { CandidateId = Guid.NewGuid() };
        item.MatchCandidates.Add(candidate);
        item.ConnectedSystemObjectChange = new ConnectedSystemObjectChange { ChangeType = ObjectChangeType.Added };
        var change = new MetaverseObjectChange { ActivityRunProfileExecutionItem = item, ChangeType = ObjectChangeType.Projected };
        change.AttributeChanges.Add(new MetaverseObjectChangeAttribute { MetaverseObjectChange = change, AttributeName = "Display Name" });
        return (item, edge, candidate, change);
    }

    [Test]
    public void PrepareSynchronisationHistory_AssignsIdsAndTakesTheBuffers()
    {
        var (item, edge, candidate, change) = NewFlush();

        var batch = _repo.PrepareSynchronisationHistory([item], null, true, [change], [], 1, 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(item.SyncOutcomes.Select(o => o.Id), Has.None.EqualTo(Guid.Empty));
            Assert.That(item.SyncOutcomes[1].ParentSyncOutcomeId, Is.EqualTo(item.SyncOutcomes[0].Id));
            Assert.That(edge.EffectRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(candidate.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(change.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(item.ConnectedSystemObjectChange!.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(item.CausalEdges, Is.Empty, "the batch owns the edges");
            Assert.That(item.MatchCandidates, Is.Empty, "and the candidates");
            Assert.That(batch.CausalEdges, Is.EqualTo(new[] { edge }));
            Assert.That(batch.MatchCandidates, Is.EqualTo(new[] { candidate }));
            Assert.That(batch.ItemChangeRecords, Is.EqualTo(new[] { item.ConnectedSystemObjectChange }));
            Assert.That(_repo.Rpeis, Is.Empty, "preparing writes nothing");
        }
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_RecordsWhatTheBatchHoldsAsync()
    {
        var (item, edge, candidate, change) = NewFlush();
        var batch = _repo.PrepareSynchronisationHistory([item], null, true, [change], [], 1, 2);

        var released = await _repo.WriteSynchronisationHistoryAsync(batch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.False, "items stay on the Activity for test assertions unless raw SQL is simulated");
            Assert.That(_repo.Rpeis[item.Id], Is.SameAs(item));
            Assert.That(_repo.CausalEdges, Is.EqualTo(new[] { edge }));
            Assert.That(_repo.MatchCandidates, Is.EqualTo(new[] { candidate }));
            Assert.That(_repo.MetaverseObjectChanges[change.Id], Is.SameAs(change));
        }
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_SimulatingRawSql_ReleasesTheItemsAsync()
    {
        _repo.SimulateRawSqlPersistence = true;
        var (item, _, _, _) = NewFlush();

        var released = await _repo.WriteSynchronisationHistoryAsync(_repo.PrepareSynchronisationHistory([item], null, false, [], [], 1, 1));

        Assert.That(released, Is.True);
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_Append_AddsItsRowsToTheExistingRecordAsync()
    {
        var existing = new MetaverseObjectChange { ChangeType = ObjectChangeType.Projected };
        await _repo.PersistPendingMvoChangesAsync([existing], []);
        var append = new MetaverseObjectChange { Id = existing.Id };
        append.AttributeChanges.Add(new MetaverseObjectChangeAttribute { MetaverseObjectChange = append, AttributeName = "Manager" });

        await _repo.WriteSynchronisationHistoryAsync(_repo.PrepareSynchronisationHistory([], null, false, [], [append], 0, 0));

        Assert.That(_repo.MetaverseObjectChanges[existing.Id].AttributeChanges.Select(a => a.AttributeName), Is.EqualTo(new[] { "Manager" }));
    }

    [Test]
    public void SupportsConcurrentHistoryWrites_InMemory_IsFalse()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_repo.SupportsConcurrentHistoryWrites, Is.False, "the store's collections are not safe to use from two threads");
            Assert.That(_repo.HasOpenTransaction, Is.False);
        }
    }
}
