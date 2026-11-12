// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins how objects are divided into batches by object count and by value count: the cross-page reference step's batches
/// (#2492) and a synchronisation's pages (#2934), so neither holds several very large groups in memory at once.
/// </summary>
[TestFixture]
public class ValueBudgetedBatchPlannerTests
{
    [Test]
    public void Plan_NoObjects_ReturnsNoBatches()
    {
        Assert.That(ValueBudgetedBatchPlanner.Plan([], maxObjects: 10, valueBudget: 100), Is.Empty);
    }

    [Test]
    public void Plan_SmallObjects_CapsEachBatchAtTheObjectLimit()
    {
        var batches = ValueBudgetedBatchPlanner.Plan([1, 1, 1, 1, 1], maxObjects: 2, valueBudget: 100);

        Assert.That(batches, Is.EqualTo(new[] { (0, 2), (2, 2), (4, 1) }));
    }

    [Test]
    public void Plan_LargeObjects_CloseABatchOnceTheValueBudgetWouldBeExceeded()
    {
        var batches = ValueBudgetedBatchPlanner.Plan([40, 40, 40, 10], maxObjects: 10, valueBudget: 100);

        Assert.That(batches, Is.EqualTo(new[] { (0, 2), (2, 2) }));
    }

    [Test]
    public void Plan_ObjectLargerThanTheBudget_GetsABatchOfItsOwn()
    {
        var batches = ValueBudgetedBatchPlanner.Plan([5, 750_000, 5], maxObjects: 10, valueBudget: 100);

        Assert.That(batches, Is.EqualTo(new[] { (0, 1), (1, 1), (2, 1) }));
    }

    [Test]
    public void FirstBatchSize_NoObjects_ReturnsZero()
    {
        Assert.That(ValueBudgetedBatchPlanner.FirstBatchSize([], maxObjects: 10, valueBudget: 100), Is.Zero);
    }

    [Test]
    public void FirstBatchSize_BudgetExactlyMet_TakesEveryObjectThatMeetsIt()
    {
        Assert.That(ValueBudgetedBatchPlanner.FirstBatchSize([40, 60, 1], maxObjects: 10, valueBudget: 100), Is.EqualTo(2));
    }

    [Test]
    public void FirstBatchSize_FirstObjectOverTheBudget_TakesItAlone()
    {
        Assert.That(ValueBudgetedBatchPlanner.FirstBatchSize([101, 1, 1], maxObjects: 10, valueBudget: 100), Is.EqualTo(1));
    }

    [Test]
    public void FirstBatchSize_ObjectOverTheBudgetAfterSmallOnes_EndsTheBatchBeforeIt()
    {
        Assert.That(ValueBudgetedBatchPlanner.FirstBatchSize([12, 12, 500_000, 12], maxObjects: 10, valueBudget: 100), Is.EqualTo(2));
    }

    [Test]
    public void FirstBatchSize_SmallObjects_TakesTheObjectLimit()
    {
        Assert.That(ValueBudgetedBatchPlanner.FirstBatchSize(Enumerable.Repeat(12, 600).ToList(), maxObjects: 500, valueBudget: 100_000),
            Is.EqualTo(500));
    }

    [Test]
    public void FirstBatchSize_EveryStart_MatchesTheBatchesPlanMakes()
    {
        var random = new Random(2934);
        for (var trial = 0; trial < 200; trial++)
        {
            var counts = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(3) == 0 ? random.Next(0, 300) : random.Next(0, 20)).ToList();
            var maxObjects = random.Next(1, 12);
            var budget = random.Next(1, 200);

            var fromFirstBatches = new List<(int Start, int Count)>();
            for (var start = 0; start < counts.Count;)
            {
                var size = ValueBudgetedBatchPlanner.FirstBatchSize(counts.Skip(start).ToList(), maxObjects, budget);
                fromFirstBatches.Add((start, size));
                start += size;
            }

            Assert.That(fromFirstBatches, Is.EqualTo(ValueBudgetedBatchPlanner.Plan(counts, maxObjects, budget)), $"trial {trial}");
        }
    }
}
