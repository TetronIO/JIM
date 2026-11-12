// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Processors;

/// <summary>
/// Divides objects, in processing order, into batches bounded by an object limit and a budget of values: a
/// synchronisation's pages (#2934) and the cross-page reference step's batches (#2492).
/// </summary>
/// <remarks>
/// A batch is closed when it reaches the object limit (the synchronisation page size) or when the next object would
/// take it past the value budget. A page, like a cross-page batch, loads, flows and re-evaluates every value of every
/// object in it at once, so counting objects alone let one page hold several groups of hundreds of thousands of members
/// together: one page at 500,000 objects held 2.0 million member values and took the Worker from 3.2 GB to 5.4 GB. An
/// object larger than the budget on its own is never split; it gets a batch of its own.
/// </remarks>
public static class ValueBudgetedBatchPlanner
{
    /// <summary>
    /// The values one batch may hold, unless a single object holds more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page costs the Worker about 1.1 KB for each value it holds, all in: the Connected System Object's value and its
    /// tracking, the Metaverse Object's value, the Pending Export's change and the history records. (In the 500,000-object
    /// run on 6519b5368, the page of 2.0 million member values ran 2.2 GB above the run's largest page of people.) So
    /// 100,000 values is about 110 MB a page. A page of people, 500 objects of about 12 values (6,000 in all), is far below
    /// it and keeps the full page size, as is a page at the largest page size the loaders allow (2,000 objects).
    /// </para>
    /// <para>
    /// A constant rather than a Service Setting: the page size is the administrator's lever, and this is a memory guard
    /// that only takes effect for groups of thousands of members, where a smaller page costs nothing measurable.
    /// </para>
    /// </remarks>
    public const int DefaultValueBudget = 100_000;

    /// <summary>
    /// How many objects the first batch takes: exactly as many as the first batch <see cref="Plan"/> makes.
    /// </summary>
    /// <param name="valueCounts">Each object's value count, in processing order. A count above
    /// <paramref name="valueBudget"/> may be capped at any value above it: such an object goes alone whatever its count.</param>
    /// <param name="maxObjects">The most objects one batch may hold.</param>
    /// <param name="valueBudget">The most values one batch may hold, unless a single object holds more.</param>
    /// <returns>The first batch's object count: zero for no objects, otherwise at least one.</returns>
    public static int FirstBatchSize(IReadOnlyList<int> valueCounts, int maxObjects, int valueBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueBudget, 1);

        var count = 0;
        long values = 0;
        foreach (var valueCount in valueCounts)
        {
            var objectValues = Math.Max(valueCount, 0);
            if (ClosesBefore(count, values, objectValues, maxObjects, valueBudget))
                break;

            count++;
            values += objectValues;
        }

        return count;
    }

    /// <summary>
    /// Plans the batches.
    /// </summary>
    /// <param name="valueCounts">Each object's value count, in processing order.</param>
    /// <param name="maxObjects">The most objects one batch may hold.</param>
    /// <param name="valueBudget">The most values one batch may hold, unless a single object holds more.</param>
    /// <returns>Each batch as its first index and its object count, in order, covering every object once.</returns>
    public static List<(int Start, int Count)> Plan(IReadOnlyList<int> valueCounts, int maxObjects, int valueBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueBudget, 1);

        var batches = new List<(int Start, int Count)>();
        var start = 0;
        var count = 0;
        long values = 0;

        for (var i = 0; i < valueCounts.Count; i++)
        {
            var objectValues = Math.Max(valueCounts[i], 0);
            if (ClosesBefore(count, values, objectValues, maxObjects, valueBudget))
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

    /// <summary>
    /// Whether a batch holding <paramref name="count"/> objects and <paramref name="values"/> values closes before the next
    /// object, of <paramref name="objectValues"/> values: at the object limit, or when that object would take it past the
    /// budget. An empty batch always takes the next object, however large.
    /// </summary>
    private static bool ClosesBefore(int count, long values, int objectValues, int maxObjects, int valueBudget) =>
        count > 0 && (count >= maxObjects || values + objectValues > valueBudget);
}
