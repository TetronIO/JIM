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

    // ─── Preparing a chunk ahead (#2605) ───

    [Test]
    public void Plan_ChunksOfAStageReferringToNoOtherExportOfIt_CanEachBePreparedAheadButTheFirst()
    {
        var ids = Ids(4);
        var summary = Summary(ids, [4, 3, 2, 1]);

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 1, LargeBatch);

        Assert.That(Enumerable.Range(0, 4).Select(plan.CanPrepareAhead), Is.EqualTo(new[] { false, true, true, true }),
            "a stage's first chunk is never prepared before the stages ahead of it have finished");
    }

    [Test]
    public void Plan_ReferenceToAnExportInAnEarlierChunkOfTheSameStage_IsNotPreparedAhead()
    {
        // R refers to T, queued after it, so the reference imposes no stage; but T is the largest and goes first, and a chunk
        // prepared once T has been written resolves R's reference, as the pass always has. Preparing R's chunk while T's
        // batch still runs would leave it unresolved.
        var ids = Ids(3);
        var summary = Summary(ids, [1, 4, 5], (0, 2));

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 1, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(new[] { ids[2], ids[1], ids[0] }));
            Assert.That(plan.Stages, Has.Count.EqualTo(1));
            Assert.That(plan.CanPrepareAhead(1), Is.True, "the chunk between them refers to nothing");
            Assert.That(plan.CanPrepareAhead(2), Is.False, "R's chunk waits until T's chunk has been handed out, as before");
        }
    }

    [Test]
    public void Plan_ReferenceToAnExportInALaterChunk_DoesNotHoldBackPreparingAhead()
    {
        // R, the largest, refers to T queued after it; T's chunk comes after R's and refers to nothing.
        var ids = Ids(2);
        var summary = Summary(ids, [5, 1], (0, 1));

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 1, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Order(plan), Is.EqualTo(ids));
            Assert.That(plan.CanPrepareAhead(1), Is.True);
        }
    }

    [Test]
    public void Plan_FirstChunkOfALaterStage_IsNotPreparedAhead()
    {
        var ids = Ids(3);
        var summary = Summary(ids, [1, 5, 4], (1, 0));

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 1, LargeBatch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Stages.Select(stage => stage.Count), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(Enumerable.Range(0, 3).Select(plan.CanPrepareAhead), Is.EqualTo(new[] { false, true, false }));
            Assert.That(plan.CanPrepareAhead(3), Is.False, "past the last chunk");
        }
    }

    // ─── A stage's lead chunk (#2943) ───

    [Test]
    public void Plan_StagesFirstChunk_IsExactlyItsFirstBatchAndTheRestIsChunkedAsBefore()
    {
        // Queue order weights 4, 6, 5, 3, 6, 2, 5, 4, 3, 2 (40 in all). A batch of three takes on no more than 12 (three
        // exports' worth of the average of 4), so the first batch, largest first, is the two exports of 6. A chunk of four
        // takes on no more than 18, the heaviest four queue order loaded together (4 + 6 + 5 + 3).
        var ids = Ids(10);
        var summary = Summary(ids, [4, 6, 5, 3, 6, 2, 5, 4, 3, 2]);

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 4, batchSize: 3);
        var chunks = plan.Chunks.ToList();
        var firstBatch = DeferredExportScheduler.CutIntoBatches(Order(plan), plan.WeightOf, batchSize: 3, plan.BatchWeightBudget)[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.BatchWeightBudget, Is.EqualTo(12));
            Assert.That(chunks[0], Is.EqualTo(firstBatch), "the stage starts with what one worker starts on");
            Assert.That(chunks, Is.EqualTo(new[]
            {
                new[] { ids[1], ids[4] },
                new[] { ids[2], ids[6], ids[0], ids[7] },
                new[] { ids[3], ids[8], ids[5], ids[9] }
            }), "after the lead chunk the stage is cut by the chunk size and weight, as it always was");
            Assert.That(DeferredExportScheduler.CutIntoBatches(chunks[0], plan.WeightOf, batchSize: 3, plan.BatchWeightBudget), Has.Count.EqualTo(1),
                "cut into batches as the pass cuts a chunk, the lead chunk is one batch");
            Assert.That(Enumerable.Range(0, 3).Select(plan.CanPrepareAhead), Is.EqualTo(new[] { false, true, true }),
                "the chunk after the lead chunk is prepared while the lead chunk's batch is written");
        }
    }

    [Test]
    public void Plan_LargestExportHeavierThanTheBatchBudget_IsTheLeadChunkAlone()
    {
        // The budget is three exports' worth of the average (28 of 55), which the export of 50 alone outweighs.
        var ids = Ids(6);
        var summary = Summary(ids, [1, 1, 50, 1, 1, 1]);

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, batchSize: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Chunks, Is.EqualTo(new[]
            {
                new[] { ids[2] },
                new[] { ids[0], ids[1], ids[3], ids[4], ids[5] }
            }));
            Assert.That(plan.CanPrepareAhead(1), Is.True);
        }
    }

    [Test]
    public void Plan_StageWithAReferenceToAnotherExportOfIt_KeepsItsChunksWithoutALeadChunk()
    {
        // R refers to T, queued after it, so the reference imposes no stage. Queue order: R 2, X1 9, X2 9, T 10; chunks of
        // three take on no more than 20. R resolves within the run only if its chunk is loaded once T has been written, and
        // a chunk that may not be prepared ahead is loaded once the batches before it have been handed out. Behind a lead
        // chunk of T's batch alone ([T], then [X1, X2, R]), R's chunk would be loaded by the next free worker while T is
        // still being written. So the stage is chunked as before, [T, X1] then [X2, R], and R's chunk is loaded only once
        // X1's batch has been handed out too, as the pass always has.
        var ids = Ids(4);
        var summary = Summary(ids, [2, 9, 9, 10], (0, 3));

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 3, batchSize: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Stages, Has.Count.EqualTo(1));
            Assert.That(plan.Chunks, Is.EqualTo(new[] { new[] { ids[3], ids[1] }, new[] { ids[2], ids[0] } }));
            Assert.That(plan.CanPrepareAhead(1), Is.False, "R's chunk waits until T's chunk has been handed out");
        }
    }

    [Test]
    public void Plan_ReferenceWithinOneStage_LeavesTheOtherStagesLeadChunksInPlace()
    {
        // Stage 0: A, B, C, D, all weighing the same, so a batch is two. Stage 1: E, F and G each wait on A, and F also refers
        // to G, queued after it. Stage 0 starts with a lead chunk; stage 1 is chunked as before, in one chunk.
        var ids = Ids(7);
        var summary = Summary(ids, [1, 1, 1, 1, 1, 1, 1], (4, 0), (5, 0), (6, 0), (5, 6));

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 4, batchSize: 2);

        Assert.That(plan.Stages.Select(stage => stage.ToArray()), Is.EqualTo(new[]
        {
            new[] { new[] { ids[0], ids[1] }, new[] { ids[2], ids[3] } },
            new[] { new[] { ids[4], ids[5], ids[6] } }
        }));
    }

    [Test]
    public void Plan_StageThatFitsInOneBatch_StaysOneChunk()
    {
        var ids = Ids(3);
        var summary = Summary(ids, [3, 2, 1]);

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 10, batchSize: 10);

        Assert.That(plan.Chunks, Is.EqualTo(new[] { new[] { ids[0], ids[1], ids[2] } }));
    }

    [Test]
    public void Plan_StageOfOneExport_StaysOneChunk()
    {
        var ids = Ids(1);

        var plan = DeferredExportScheduler.Plan(ids, Summary(ids, [7]), LargeChunk, batchSize: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Chunks, Is.EqualTo(new[] { new[] { ids[0] } }));
            Assert.That(plan.CanPrepareAhead(0), Is.False);
        }
    }

    [Test]
    public void Plan_SeveralStages_EachStartsWithALeadChunk()
    {
        // Four independent exports, then three each waiting on one of them: every export weighs the same, so a batch is two.
        var ids = Ids(7);
        var summary = Summary(ids, [1, 1, 1, 1, 1, 1, 1], (4, 0), (5, 1), (6, 2));

        var plan = DeferredExportScheduler.Plan(ids, summary, LargeChunk, batchSize: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Stages.Select(stage => stage.ToArray()), Is.EqualTo(new[]
            {
                new[] { new[] { ids[0], ids[1] }, new[] { ids[2], ids[3] } },
                new[] { new[] { ids[4], ids[5] }, new[] { ids[6] } }
            }));
            Assert.That(Enumerable.Range(0, 4).Select(plan.CanPrepareAhead), Is.EqualTo(new[] { false, true, false, true }),
                "a later stage's lead chunk waits on the stages before it; the chunk after it does not");
        }
    }

    [Test]
    public void Plan_ChunkSizeBelowTheBatchSize_LeadChunkIsNoLargerThanAChunk()
    {
        // The pass holds a chunk in memory, so the lead chunk never takes more than a chunk would, even where a batch could.
        var ids = Ids(6);
        var summary = Summary(ids, [1, 1, 1, 1, 1, 1]);

        var plan = DeferredExportScheduler.Plan(ids, summary, chunkSize: 2, batchSize: 5);

        Assert.That(plan.Chunks, Is.EqualTo(new[] { new[] { ids[0], ids[1] }, new[] { ids[2], ids[3] }, new[] { ids[4], ids[5] } }));
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
