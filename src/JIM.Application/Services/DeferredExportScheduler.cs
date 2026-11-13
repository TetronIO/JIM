// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Application.Services;

/// <summary>
/// Orders the export's deferred pass so the largest work starts first (#2905), without resolving fewer references within
/// the run than queue order, (CreatedAt, Id), did.
/// </summary>
/// <remarks>
/// <para>
/// With several workers the pass can finish no sooner than its longest batch, and reaches that floor only when the longest
/// batch starts early enough for the others to overlap it. In queue order where the largest groups fell was arbitrary: at
/// 500,000 objects the largest group's batch was in the last chunk, and ran alone for over two minutes at the end.
/// </para>
/// <para>
/// The constraint is reference resolution. A chunk is resolved against the referenced objects' anchors as it is loaded,
/// before any of it is written. A reference to an object whose anchor is already held (a DN staged when the object was
/// provisioned, say) resolves wherever the export falls. A reference to an object written by another export of the pass,
/// whose anchor the target system generates or which the export renames, resolves only once that export has been
/// written; queue order wrote it first only when it was queued earlier and in an earlier chunk. Such a dependency is kept:
/// the waiting export goes in a later stage than the export it waits on, and stages run one after another, so that export
/// has always been written by the time the waiting export's chunk is loaded. A reference to an export queued later never
/// resolved within the run in queue order, so it imposes nothing.
/// </para>
/// <para>
/// Within a stage, exports run largest first. Chunks and batches are cut by weight as well as count, so gathering the
/// heaviest exports together neither loads more at once than queue order's heaviest chunk did (the pass holds one chunk in
/// memory) nor packs them into one batch that would outlast everything else.
/// </para>
/// <para>
/// A stage's first chunk is its first batch alone (#2943): the run of its largest exports that the pass cuts into one batch,
/// and never more than a chunk would hold. That chunk cannot be prepared ahead, since it waits on the stages before it, so
/// it is prepared with no worker writing anything; kept to what one worker starts on, a worker starts writing the largest
/// work as soon as it is ready, and the next chunk is prepared while it does. At 500,000 objects a first chunk of the 18
/// largest groups took 23 seconds to prepare, with every worker idle. The rest of the stage is chunked as before, largest
/// first, except that no chunk of such a stage takes on more than <see cref="DeferredChunkWeightCap"/> changes, unless it
/// is a single export with more: chunks are prepared one at a time, and the 29 largest groups after the lead took as long
/// to prepare as the lead chunk had, with three workers waiting. Stages keep their order, and every chunk stays within the
/// bound above. A stage holding a reference
/// from one of its exports to another (see below) has no lead chunk and is chunked as before: behind a lead chunk of one
/// batch, the chunk after it would be loaded by the next free worker while that batch might still be being written, and
/// so would resolve less.
/// </para>
/// <para>
/// The plan also says which chunks may be prepared ahead (#2605), while the batches of the chunk before them are still
/// being written: see <see cref="DeferredExportPlan.CanPrepareAhead"/>. No export of a stage waits on another export of
/// it in the sense above, but a stage may still hold a reference to an export queued later whose anchor it supplies; the
/// pass has always resolved such a reference when the referrer's chunk was loaded after that export was written, so a
/// chunk holding one is not prepared ahead.
/// </para>
/// </remarks>
public static class DeferredExportScheduler
{
    /// <summary>
    /// The most changes a chunk of a stage with a lead chunk takes on (#2943), unless it holds a single export with more.
    /// Preparing a chunk costs about 7 microseconds per change (measured at 500,000 objects: 2,135,016 changes in 23.1
    /// seconds) and one chunk is prepared at a time, so a chunk of 250,000 changes takes about 1.75 seconds: short enough to
    /// be ready before the free workers have drained the chunk before it. Uncapped, the chunk after the lead held 29 of the
    /// largest groups, and three workers waited 23 seconds for it.
    /// </summary>
    public const int DeferredChunkWeightCap = 250_000;

    /// <summary>
    /// Plans the order of the deferred pass.
    /// </summary>
    /// <param name="queueOrder">The pass's Pending Export ids, in the (CreatedAt, Id) order they were collected in.</param>
    /// <param name="summary">How much each export writes, and which wait on another export of the pass.</param>
    /// <param name="chunkSize">The most exports a chunk holds (<see cref="ExportExecutionOptions.DeferredChunkSize"/>).</param>
    /// <param name="batchSize">The most exports a batch holds (<see cref="ExportExecutionOptions.BatchSize"/>); also the most
    /// a stage's first chunk holds.</param>
    public static DeferredExportPlan Plan(IReadOnlyList<Guid> queueOrder, DeferredExportOrderingSummary summary, int chunkSize, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(queueOrder);
        ArgumentNullException.ThrowIfNull(summary);
        chunkSize = Math.Max(1, chunkSize);
        batchSize = Math.Max(1, batchSize);

        // Position in queue order, the first occurrence winning (the collection never repeats an id; this is defensive).
        var position = new Dictionary<Guid, int>(queueOrder.Count);
        var ordered = new List<Guid>(queueOrder.Count);
        foreach (var id in queueOrder.Where(id => !position.ContainsKey(id)))
        {
            position[id] = ordered.Count;
            ordered.Add(id);
        }

        var weights = ordered.ToDictionary(id => id, id => Math.Max(1, summary.ChangeCounts.TryGetValue(id, out var count) ? count : 0));
        var totalWeight = weights.Values.Sum(w => (long)w);

        // The dependencies queue order honoured: on an export of the pass queued earlier.
        var waitsOn = summary.Dependencies
            .Where(dependency => position.TryGetValue(dependency.ReferrerId, out var referrerPosition)
                && position.TryGetValue(dependency.TargetId, out var targetPosition)
                && targetPosition < referrerPosition)
            .GroupBy(dependency => dependency.ReferrerId, dependency => dependency.TargetId)
            .ToDictionary(group => group.Key, group => group.ToList());

        // Each export's stage: one after the latest of the stages it waits on. Every target precedes its referrer in queue
        // order, so a single walk in that order has every target's stage before it is needed.
        var stageOf = new Dictionary<Guid, int>(ordered.Count);
        foreach (var id in ordered)
            stageOf[id] = waitsOn.TryGetValue(id, out var targets) ? 1 + targets.Max(target => stageOf[target]) : 0;

        // No chunk outweighs the heaviest chunk queue order loaded, so the pass never holds more at once than it did. A batch
        // takes on no more than its share by count of the pass's weight (the batch size's worth of the average export), nor
        // more than queue order's heaviest batch. Where every export weighs the same, both cut exactly where the count does.
        long HeaviestQueueOrderRun(int runSize) => ordered.Count == 0
            ? long.MaxValue
            : ordered.Chunk(runSize).Max(run => run.Sum(id => (long)weights[id]));
        var chunkWeightBudget = HeaviestQueueOrderRun(chunkSize);
        var batchWeightBudget = ordered.Count == 0
            ? long.MaxValue
            : Math.Min((totalWeight * batchSize + ordered.Count - 1) / ordered.Count, HeaviestQueueOrderRun(batchSize));

        // The stages holding a reference from one of their exports to another (to one queued later, so imposing no stage).
        var stagesReferringWithin = summary.Dependencies
            .Where(dependency => dependency.ReferrerId != dependency.TargetId
                && stageOf.TryGetValue(dependency.ReferrerId, out var referrerStage)
                && stageOf.TryGetValue(dependency.TargetId, out var targetStage)
                && referrerStage == targetStage)
            .Select(dependency => stageOf[dependency.ReferrerId])
            .ToHashSet();

        // A stage's lead chunk is its first batch (#2943), cut as the pass cuts a chunk into batches, and never more than a
        // chunk would hold: so it is exactly one batch, which a worker starts on as soon as it is prepared, while the next
        // chunk is prepared ahead. The rest of the stage is chunked as it always was, but no chunk takes on more than the cap,
        // so the next chunk is prepared in time. A stage referring within itself is chunked exactly as it always was: such a
        // reference resolves within the run only when the referrer's chunk is loaded after the export it names has been
        // written, and behind a lead chunk of one batch, or smaller chunks, the next chunk would be loaded by the next free
        // worker, while the batch before it may still be being written.
        var leadChunkSize = Math.Min(batchSize, chunkSize);
        var cappedChunkWeightBudget = Math.Min(chunkWeightBudget, DeferredChunkWeightCap);
        var leadChunkWeightBudget = Math.Min(batchWeightBudget, cappedChunkWeightBudget);
        var stages = ordered
            .GroupBy(id => stageOf[id])
            .OrderBy(stage => stage.Key)
            .Select(stage =>
            {
                var stageOrder = stage.OrderByDescending(id => weights[id]).ThenBy(id => position[id]).ToList();
                if (stagesReferringWithin.Contains(stage.Key))
                {
                    return (IReadOnlyList<Guid[]>)CutIntoBatches(stageOrder, id => weights[id], chunkSize, chunkWeightBudget)
                        .Select(chunk => chunk.ToArray())
                        .ToList();
                }

                var leadChunk = CutIntoBatches(stageOrder, id => weights[id], leadChunkSize, leadChunkWeightBudget)[0];
                var chunks = new List<Guid[]> { leadChunk.ToArray() };
                chunks.AddRange(CutIntoBatches(stageOrder.Skip(leadChunk.Count).ToList(), id => weights[id], chunkSize, cappedChunkWeightBudget)
                    .Select(chunk => chunk.ToArray()));
                return (IReadOnlyList<Guid[]>)chunks;
            })
            .ToList();

        return new DeferredExportPlan
        {
            Stages = stages,
            Weights = weights,
            BatchWeightBudget = batchWeightBudget,
            DependentCount = waitsOn.Count,
            PreparableAhead = PreparableAhead(stages, summary.Dependencies)
        };
    }

    /// <summary>
    /// Per chunk, whether it may be prepared while the batches of the chunks before it in its stage are still being written
    /// (#2605; <see cref="DeferredExportPlan.CanPrepareAhead"/>). Every chunk but a stage's first, unless one of its exports
    /// refers to the object of an export in an earlier chunk of the stage whose anchor that export supplies. Such a
    /// reference imposes no stage (the export it names was queued later, so queue order never resolved it within the run),
    /// but the pass resolves a chunk against what has been written by the time it is loaded, and so may resolve it; the
    /// chunk is loaded when it always was, so it resolves no less. Every other reference resolves the same whenever the
    /// chunk is prepared: to an object the pass does not give an anchor, a stage before (finished), or this chunk or one
    /// after it (not yet written either way).
    /// </summary>
    private static List<bool> PreparableAhead(IReadOnlyList<IReadOnlyList<Guid[]>> stages, IReadOnlyList<DeferredExportDependency> dependencies)
    {
        var located = new Dictionary<Guid, (int Stage, int Chunk)>();
        var preparable = new List<bool>();
        for (var stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            for (var chunkInStage = 0; chunkInStage < stages[stageIndex].Count; chunkInStage++)
            {
                foreach (var id in stages[stageIndex][chunkInStage])
                    located.TryAdd(id, (stageIndex, preparable.Count));
                preparable.Add(chunkInStage > 0);
            }
        }

        foreach (var dependency in dependencies)
        {
            if (located.TryGetValue(dependency.ReferrerId, out var referrer)
                && located.TryGetValue(dependency.TargetId, out var target)
                && referrer.Stage == target.Stage
                && target.Chunk < referrer.Chunk)
            {
                preparable[referrer.Chunk] = false;
            }
        }

        return preparable;
    }

    /// <summary>
    /// Cuts <paramref name="items"/> into consecutive runs, keeping their order: a run closes once it holds
    /// <paramref name="batchSize"/> items, or when the next item would take it over <paramref name="weightBudget"/>. An
    /// item heavier than the budget makes a run of its own. Cuts both the pass's chunks and their batches.
    /// </summary>
    public static List<List<T>> CutIntoBatches<T>(IReadOnlyList<T> items, Func<T, int> weightOf, int batchSize, long weightBudget)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(weightOf);
        batchSize = Math.Max(1, batchSize);

        var batches = new List<List<T>>();
        var current = new List<T>();
        long currentWeight = 0;
        foreach (var item in items)
        {
            var weight = Math.Max(1, weightOf(item));
            if (current.Count > 0 && (current.Count >= batchSize || currentWeight + weight > weightBudget))
            {
                batches.Add(current);
                current = [];
                currentWeight = 0;
            }

            current.Add(item);
            currentWeight += weight;
        }

        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }
}
