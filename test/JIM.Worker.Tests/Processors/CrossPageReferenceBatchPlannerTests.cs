// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins how the cross-page reference step divides its deferred objects into batches (#2492): by object count and by
/// reference-value count, so a batch holding several very large groups does not hold all of them in memory at once.
/// </summary>
[TestFixture]
public class CrossPageReferenceBatchPlannerTests
{
    [Test]
    public void Plan_NoObjects_ReturnsNoBatches()
    {
        Assert.That(CrossPageReferenceBatchPlanner.Plan([], maxObjects: 10, valueBudget: 100), Is.Empty);
    }

    [Test]
    public void Plan_SmallObjects_CapsEachBatchAtTheObjectLimit()
    {
        var batches = CrossPageReferenceBatchPlanner.Plan([1, 1, 1, 1, 1], maxObjects: 2, valueBudget: 100);

        Assert.That(batches, Is.EqualTo(new[] { (0, 2), (2, 2), (4, 1) }));
    }

    [Test]
    public void Plan_LargeObjects_CloseABatchOnceTheValueBudgetWouldBeExceeded()
    {
        var batches = CrossPageReferenceBatchPlanner.Plan([40, 40, 40, 10], maxObjects: 10, valueBudget: 100);

        Assert.That(batches, Is.EqualTo(new[] { (0, 2), (2, 2) }));
    }

    [Test]
    public void Plan_ObjectLargerThanTheBudget_GetsABatchOfItsOwn()
    {
        var batches = CrossPageReferenceBatchPlanner.Plan([5, 750_000, 5], maxObjects: 10, valueBudget: 100);

        Assert.That(batches, Is.EqualTo(new[] { (0, 1), (1, 1), (2, 1) }));
    }
}
