// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// Divides the cross-page reference step's deferred objects into batches (#2492).
/// </summary>
/// <remarks>
/// A batch is closed when it reaches the object limit (the synchronisation page size) or when the next object would
/// take it past the reference-value budget. The step reloads, flows and re-evaluates every reference value of every
/// object in a batch at once, so counting objects alone let one batch hold several groups of hundreds of thousands
/// of members together. An object larger than the budget on its own is never split; it gets a batch of its own.
/// </remarks>
public static class CrossPageReferenceBatchPlanner
{
    /// <summary>
    /// The reference values one batch may hold, unless a single object holds more.
    /// </summary>
    public const int DefaultValueBudget = 100_000;

    /// <summary>
    /// Plans the batches.
    /// </summary>
    /// <param name="referenceValueCounts">Each deferred object's reference-value count, in processing order.</param>
    /// <param name="maxObjects">The most objects one batch may hold.</param>
    /// <param name="valueBudget">The most reference values one batch may hold, unless a single object holds more.</param>
    /// <returns>Each batch as its first index and its object count, in order, covering every object once.</returns>
    public static List<(int Start, int Count)> Plan(IReadOnlyList<int> referenceValueCounts, int maxObjects, int valueBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueBudget, 1);

        var batches = new List<(int Start, int Count)>();
        var start = 0;
        var count = 0;
        long values = 0;

        for (var i = 0; i < referenceValueCounts.Count; i++)
        {
            var objectValues = Math.Max(referenceValueCounts[i], 0);
            if (count > 0 && (count >= maxObjects || values + objectValues > valueBudget))
            {
                batches.Add((start, count));
                start = i;
                count = 0;
                values = 0;
            }

            count++;
            values += objectValues;
        }

        if (count > 0)
            batches.Add((start, count));

        return batches;
    }
}
