// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the order the export's deferred pass works through its exports in (#2905): the largest work first, so the
/// longest batch starts early enough for the others to overlap it, without resolving fewer references within the run
/// than queue order, (CreatedAt, Id), did. A reference to another export of the pass resolves within the run only once
/// that export has been written; queue order wrote it first only when it was queued earlier, so an export waiting on an
/// export queued before it must stay behind it, in a later stage.
/// </summary>
[TestFixture]
public class DeferredExportSchedulerTests
{
    private const int LargeChunk = 1_000;
    private const int LargeBatch = 1_000;

    [Test]
    public void Plan_IndependentExports_OrdersThemLargestFirst()
    {
        var ids = Ids(5);
        var summary = Summary(ids, [1, 5, 3, 8, 2]);

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);

        Assert.That(Order(plan), Is.EqualTo(new[] { ids[3], ids[1], ids[2], ids[4], ids[0] }));
    }

    [Test]
    public void Plan_EqualSizes_KeepsQueueOrderBetweenThem()
    {
        var ids = Ids(5);
        var summary = Summary(ids, [4, 7, 4, 7, 4]);

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);

        Assert.That(Order(plan), Is.EqualTo(new[] { ids[1], ids[3], ids[0], ids[2], ids[4] }),
            "a tie keeps the (CreatedAt, Id) order the ids were collected in");
    }

    [Test]
    public void Plan_NoChangeCountsKnown_KeepsQueueOrder()
    {
        var ids = Ids(4);

        var plan = DeferredExportScheduler.Plan(ids, new DeferredExportOrderingSummary(), chunkSize: 2, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(ids));
            Assert.That(plan.WeightOf(ids[0]), Is.EqualTo(1), "an export with no known changes still weighs something");
        }
    }

    [Test]
    public void Plan_DependencyChain_KeepsItsOrderOneStageApart()
    {
        // A nested group chain whose anchors the target system generates: C contains B, B contains A. Queue order wrote A,
        // then B, then C, so each could resolve the one before it; the largest first would put C ahead of both.
        var ids = Ids(3);
        var summary = Summary(ids, [1, 5, 9], (1, 0), (2, 1));

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(ids), "each export after the one it waits on");
            Assert.That(plan.Stages.Select(stage => stage.SelectMany(chunk => chunk).ToArray()),
                Is.EqualTo(new[] { new[] { ids[0] }, new[] { ids[1] }, new[] { ids[2] } }), "each in a stage of its own");
            Assert.That(plan.DependentCount, Is.EqualTo(2));
        }
    }

    [Test]
    public void Plan_ExportWaitingOnAnIndependentExport_StartsInAChunkAfterItsEvenWithRoomToSpare()
    {
        // A chunk is resolved when it is loaded, before any of it is written, so an export sharing a chunk with the export
        // it waits on cannot resolve against it.
        var ids = Ids(3);
        var summary = Summary(ids, [2, 1, 3], (1, 0));

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);
        var chunks = plan.Chunks.ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(new[] { ids[2], ids[0], ids[1] }));
            Assert.That(ChunkIndexOf(chunks, ids[1]), Is.GreaterThan(ChunkIndexOf(chunks, ids[0])));
        }
    }

    [Test]
    public void Plan_ReferenceToAnExportQueuedLater_ImposesNoOrder()
    {
        // Queue order never wrote the later export before the earlier one's chunk was resolved, so nothing it resolved
        // depended on that order, and the largest may go first.
        var ids = Ids(2);
        var summary = Summary(ids, [1, 5], (0, 1));

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(new[] { ids[1], ids[0] }));
            Assert.That(plan.Stages, Has.Count.EqualTo(1));
            Assert.That(plan.DependentCount, Is.Zero);
        }
    }

    [Test]
    public void Plan_MixedIndependentAndDependentExports_OrdersEachStageLargestFirstAfterTheStagesItWaitsOn()
    {
        // Queue order: U1(3), G1(10), G2(2, waits on G1), U2(7), G3(6, waits on G2), G4(4, waits on G1).
        var ids = Ids(6);
        var summary = Summary(ids, [3, 10, 2, 7, 6, 4], (2, 1), (4, 2), (5, 1));

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Stages.Select(stage => stage.SelectMany(chunk => chunk).ToArray()), Is.EqualTo(new[]
            {
                new[] { ids[1], ids[3], ids[0] },
                new[] { ids[5], ids[2] },
                new[] { ids[4] }
            }));
            Assert.That(plan.DependentCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void Plan_DependenciesOnItselfOrOutsideThePass_AreIgnored()
    {
        var ids = Ids(2);
        var summary = new DeferredExportOrderingSummary
        {
            ChangeCounts = new Dictionary<Guid, int> { [ids[0]] = 1, [ids[1]] = 5 },
            Dependencies = [new(ids[1], ids[1]), new(ids[1], Guid.NewGuid()), new(Guid.NewGuid(), ids[0])]
        };

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(new[] { ids[1], ids[0] }));
            Assert.That(plan.Stages, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Plan_SkewedSizes_NoChunkIsHeavierOrLongerThanQueueOrderWouldHaveLoaded()
    {
        // Largest first gathers the heaviest exports together; cut by count alone, the first chunk would hold the heaviest
        // chunk's worth of every heavy export at once. The pass holds one chunk in memory, so no chunk may outweigh the
        // heaviest chunk queue order loaded.
        var random = new Random(2905);
        var ids = Ids(200);
        var sizes = ids.Select(_ => random.Next(10) == 0 ? random.Next(500, 5_000) : random.Next(1, 40)).ToArray();
        var summary = Summary(ids, sizes);
        const int chunkSize = 16;

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize, batchSize: 4);

        var weightOf = ids.Select((id, i) => (id, size: sizes[i])).ToDictionary(p => p.id, p => p.size);
        var heaviestQueueOrderChunk = ids.Chunk(chunkSize).Max(chunk => chunk.Sum(id => weightOf[id]));
        var chunks = plan.Chunks.ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks.Max(chunk => chunk.Sum(id => weightOf[id])), Is.LessThanOrEqualTo(heaviestQueueOrderChunk));
            Assert.That(chunks.Max(chunk => chunk.Length), Is.LessThanOrEqualTo(chunkSize));
            Assert.That(Order(plan), Is.EquivalentTo(ids), "every export of the pass, once");
            Assert.That(Order(plan).First(), Is.EqualTo(ids[Array.IndexOf(sizes, sizes.Max())]), "the largest first");
        }
    }

    [Test]
    public void Plan_BatchWeightBudget_IsTheBatchSizesWorthOfTheAverageExport()
    {
        var ids = Ids(10);
        var summary = Summary(ids, [30, 20, 10, 10, 10, 5, 5, 5, 3, 2]);

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, batchSize: 2);

        Assert.That(plan.BatchWeightBudget, Is.EqualTo(20), "two exports' worth of an average of 10 changes");
    }

    [Test]
    public void Plan_BatchWeightBudget_NeverExceedsQueueOrdersHeaviestBatch()
    {
        // One heavy export alone at the end: four exports' worth of the average (12) outweighs any batch queue order made.
        var ids = Ids(5);
        var summary = Summary(ids, [1, 1, 1, 1, 10]);

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, batchSize: 4);

        Assert.That(plan.BatchWeightBudget, Is.EqualTo(10), "queue order's heaviest batch was the heavy export alone");
    }

    [Test]
    public void Plan_EqualSizes_CutsChunksAndBatchesWhereTheCountDoes()
    {
        // Weight only bites where exports differ: equal exports are cut exactly as queue order cut them.
        var ids = Ids(5);
        var summary = Summary(ids, [3, 3, 3, 3, 3]);

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 2, batchSize: 2);
        var batches = DeferredExportScheduler.CutIntoBatches(ids, plan.WeightOf, batchSize: 2, plan.BatchWeightBudget);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Chunks.Select(chunk => chunk.Length), Is.EqualTo(new[] { 2, 2, 1 }));
            Assert.That(batches.Select(batch => batch.Count), Is.EqualTo(new[] { 2, 2, 1 }));
        }
    }

    [Test]
    public void CutIntoBatches_ClosesABatchAtTheBatchSizeOrTheWeightBudget()
    {
        int[] weights = [10, 6, 5, 4, 1, 1, 1];

        var batches = DeferredExportScheduler.CutIntoBatches(weights, w => w, batchSize: 3, weightBudget: 10);

        Assert.That(batches, Is.EqualTo(new[] { new[] { 10 }, new[] { 6 }, new[] { 5, 4, 1 }, new[] { 1, 1 } }));
    }

    [Test]
    public void CutIntoBatches_AnExportHeavierThanTheBudget_TakesABatchOfItsOwn()
    {
        int[] weights = [50, 1, 1];

        var batches = DeferredExportScheduler.CutIntoBatches(weights, w => w, batchSize: 5, weightBudget: 10);

        Assert.That(batches, Is.EqualTo(new[] { new[] { 50 }, new[] { 1, 1 } }));
    }

    // ─── Helpers ───

    private static List<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    /// <summary>The sizes by queue position, and each dependency as (referrer position, target position).</summary>
    private static DeferredExportOrderingSummary Summary(IReadOnlyList<Guid> ids, int[] sizes, params (int Referrer, int Target)[] dependencies) => new()
    {
        ChangeCounts = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => sizes[p.i]),
        Dependencies = dependencies.Select(d => new DeferredExportDependency(ids[d.Referrer], ids[d.Target])).ToList()
    };

    private static List<Guid> Order(DeferredExportPlan plan) => plan.Chunks.SelectMany(chunk => chunk).ToList();

    private static int ChunkIndexOf(List<Guid[]> chunks, Guid id) => chunks.FindIndex(chunk => chunk.Contains(id));
}
