// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// <see cref="SyncRepository.PrepareSynchronisationHistory"/> (#2874): everything a write of the batch needs is
/// assigned before the batch leaves the run, and the batch owns what it holds. CPU-only, so no database is needed; the
/// context is never opened.
/// </summary>
[TestFixture]
public class SynchronisationHistoryPreparationTests
{
    private JimDbContext _context = null!;
    private SyncRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql("Host=never-opened;Database=none")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _context = new JimDbContext(options);
        _repository = new SyncRepository(new PostgresDataRepository(_context));
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private SynchronisationHistoryBatch Prepare(SynchronisationHistoryBatchBuilder flush, IReadOnlySet<Guid>? writtenWithDeletion = null,
        bool includeItemChangeRecords = true, List<MetaverseObjectChange>? appends = null)
        => _repository.PrepareSynchronisationHistory(flush.Items, writtenWithDeletion, includeItemChangeRecords,
            [flush.MetaverseObjectChange], appends ?? [], page: 2, totalPages: 5);

    [Test]
    public void PrepareSynchronisationHistory_AssignsEveryIdAndScalarBeforeHandOff()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());
        var append = SynchronisationHistoryBatchBuilder.AppendTo(Guid.NewGuid());

        var batch = Prepare(flush, appends: [append]);

        var item = flush.ProjectedItem;
        var outcomeValue = flush.OutcomeChangeRecord.AttributeChanges.Single().ValueChanges.Single();
        var itemValue = flush.ItemChangeRecord.AttributeChanges.Single().ValueChanges.Single();
        var mvoValue = flush.MetaverseObjectChange.AttributeChanges.Single().ValueChanges.Single();
        var appendValue = append.AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Items.Select(i => i.Id), Has.None.EqualTo(Guid.Empty), "every item has its id");
            Assert.That(item.ConnectedSystemObjectId, Is.EqualTo(flush.ProjectedObject.Id), "the object's id is copied off the navigation");

            Assert.That(batch.Outcomes.Select(o => o.Id), Has.None.EqualTo(Guid.Empty), "every outcome has its id");
            Assert.That(batch.Outcomes.Select(o => o.ActivityRunProfileExecutionItemId), Is.All.EqualTo(item.Id));
            Assert.That(flush.ChildOutcome.ParentSyncOutcomeId, Is.EqualTo(flush.RootOutcome.Id));
            Assert.That(flush.RootOutcome.ParentSyncOutcomeId, Is.Null);

            Assert.That(flush.OutcomeChangeRecord.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(flush.OutcomeChangeRecord.AttributeChanges.Single().Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(outcomeValue.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(flush.ChildOutcome.ConnectedSystemObjectChangeId, Is.EqualTo(flush.OutcomeChangeRecord.Id),
                "the outcome names its change record by id");

            Assert.That(flush.ItemChangeRecord.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(itemValue.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(flush.ItemChangeRecord.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id),
                "the item's change record names the item");

            Assert.That(flush.Edge.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(flush.Edge.EffectRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(flush.Edge.EffectSyncOutcomeId, Is.EqualTo(flush.ChildOutcome.Id), "the edge names its effect outcome by id");

            Assert.That(flush.Candidate.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(flush.Candidate.ActivityRunProfileExecutionItemId, Is.EqualTo(flush.AmbiguousItem.Id));

            Assert.That(flush.MetaverseObjectChange.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(mvoValue.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(flush.MetaverseObjectChange.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id),
                "the Metaverse Object change record names its item by id");
            Assert.That(append.AttributeChanges.Single().Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(appendValue.Id, Is.Not.EqualTo(Guid.Empty));

            Assert.That(batch.Outcomes, Is.EquivalentTo(new[] { flush.RootOutcome, flush.ChildOutcome }));
            Assert.That(batch.OutcomeChangeRecords, Is.EqualTo(new[] { flush.OutcomeChangeRecord }));
            Assert.That(batch.ItemChangeRecords, Is.EqualTo(new[] { flush.ItemChangeRecord }));
            Assert.That(batch.CausalEdges, Is.EqualTo(new[] { flush.Edge }));
            Assert.That(batch.MatchCandidates, Is.EqualTo(new[] { flush.Candidate }));
            Assert.That(batch.MetaverseObjectChanges, Is.EqualTo(new[] { flush.MetaverseObjectChange }));
            Assert.That(batch.MetaverseObjectChangeAppends, Is.EqualTo(new[] { append }));
            Assert.That(batch.Page, Is.EqualTo(2));
            Assert.That(batch.TotalPages, Is.EqualTo(5));
        }
    }

    [Test]
    public void PrepareSynchronisationHistory_DrainsEdgeAndCandidateBuffers()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());

        var batch = Prepare(flush);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flush.ProjectedItem.CausalEdges, Is.Empty, "the batch owns the edges, so a later flush of the item cannot write them twice");
            Assert.That(flush.AmbiguousItem.MatchCandidates, Is.Empty, "likewise the candidates");
            Assert.That(batch.CausalEdges, Has.Count.EqualTo(1));
            Assert.That(batch.MatchCandidates, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void PrepareSynchronisationHistory_CounterDeltas_CountTheItemsAndOutcomesAsTheItemWriterDoes()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());

        var batch = Prepare(flush);

        var expected = ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(batch.Items.ToList(), batch.Outcomes.ToList());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.CounterDeltas, Is.EquivalentTo(expected));
            Assert.That(batch.CounterDeltas[new ActivityStatCounterKey(flush.ActivityId, ActivityStatDimension.ObjectTypeName, "person")], Is.EqualTo(1),
                "the type is read from the object the item names, while the run still holds it, not from the snapshot");
        }
    }

    [Test]
    public void PrepareSynchronisationHistory_ItemWrittenWithDeletion_LeavesItsChangeRecordOut()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());
        flush.ProjectedItem.Id = Guid.NewGuid();

        var batch = Prepare(flush, writtenWithDeletion: new HashSet<Guid> { flush.ProjectedItem.Id });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.ItemChangeRecords, Is.Empty, "the deletion wrote its change record with its row");
            Assert.That(batch.Items, Does.Contain(flush.ProjectedItem), "its row is still brought up to date, and the rest of it written");
            Assert.That(batch.ItemIdsWrittenWithDeletion, Is.EquivalentTo(new[] { flush.ProjectedItem.Id }));
            Assert.That(batch.Outcomes, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void PrepareSynchronisationHistory_ChangeTrackingOff_WritesNoItemChangeRecord()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());

        var batch = Prepare(flush, includeItemChangeRecords: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.ItemChangeRecords, Is.Empty);
            Assert.That(batch.OutcomeChangeRecords, Has.Count.EqualTo(1), "the outcomes' snapshots are written whatever the setting");
        }
    }

    [Test]
    public void PrepareSynchronisationHistory_CallerClearsItsMetaverseObjectChangeLists_BatchKeepsThem()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());
        var newChanges = new List<MetaverseObjectChange> { flush.MetaverseObjectChange };
        var appends = new List<MetaverseObjectChange> { SynchronisationHistoryBatchBuilder.AppendTo(Guid.NewGuid()) };

        var batch = _repository.PrepareSynchronisationHistory(flush.Items, null, true, newChanges, appends, 1, 1);
        newChanges.Clear();
        appends.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.MetaverseObjectChanges, Has.Count.EqualTo(1));
            Assert.That(batch.MetaverseObjectChangeAppends, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void PrepareSynchronisationHistory_AppendWithoutTheIdOfTheRecordItExtends_Throws()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());

        Assert.That(() => Prepare(flush, appends: [SynchronisationHistoryBatchBuilder.AppendTo(Guid.Empty)]),
            Throws.InvalidOperationException.With.Message.Contain("existing"));
    }

    [Test]
    public void WriteSynchronisationHistoryAsync_BatchWithAnUnassignedId_ThrowsBeforeWritingAnything()
    {
        var flush = new SynchronisationHistoryBatchBuilder(Guid.NewGuid());
        var batch = Prepare(flush);
        flush.RootOutcome.Id = Guid.Empty;

        // The context is never opened: the check precedes every write.
        Assert.That(() => _repository.WriteSynchronisationHistoryAsync(batch),
            Throws.InvalidOperationException.With.Message.Contain("assigns no id"));
    }

    [Test]
    public void HasOpenTransaction_NoTransaction_IsFalse()
    {
        Assert.That(_repository.HasOpenTransaction, Is.False);
    }
}
