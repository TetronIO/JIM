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
/// </remarks>
public static class DeferredExportScheduler
{
    /// <summary>
    /// Plans the order of the deferred pass.
    /// </summary>
    /// <param name="queueOrder">The pass's Pending Export ids, in the (CreatedAt, Id) order they were collected in.</param>
    /// <param name="summary">How much each export writes, and which wait on another export of the pass.</param>
    /// <param name="chunkSize">The most exports a chunk holds (<see cref="ExportExecutionOptions.DeferredChunkSize"/>).</param>
    /// <param name="batchSize">The most exports a batch holds (<see cref="ExportExecutionOptions.BatchSize"/>).</param>
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

        var stages = ordered
            .GroupBy(id => stageOf[id])
            .OrderBy(stage => stage.Key)
            .Select(stage => (IReadOnlyList<Guid[]>)CutIntoBatches(
                    stage.OrderByDescending(id => weights[id]).ThenBy(id => position[id]).ToList(),
                    id => weights[id], chunkSize, chunkWeightBudget)
                .Select(chunk => chunk.ToArray())
                .ToList())
            .ToList();

        return new DeferredExportPlan
        {
            Stages = stages,
            Weights = weights,
            BatchWeightBudget = batchWeightBudget,
            DependentCount = waitsOn.Count
        };
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
