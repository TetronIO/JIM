// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Instrumentation;

[TestFixture]
public class SpanBreakdownTests
{
    [Test]
    public void Describe_RepeatedDescendants_AggregatesCountTotalAndMaxPerPath()
    {
        var breakdown = new SpanBreakdown();
        breakdown.Record("AttemptJoin", 2);
        breakdown.Record("AttemptJoin", 6);
        breakdown.Record("ProcessInboundAttributeFlow", 1);

        var entries = breakdown.GetEntries();

        var join = entries.Single(e => e.Path == "AttemptJoin");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Count, Is.EqualTo(2));
            Assert.That(join.TotalMs, Is.EqualTo(8));
            Assert.That(join.MaxMs, Is.EqualTo(6));
            Assert.That(entries.Single(e => e.Path == "ProcessInboundAttributeFlow").Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void GetEntries_SeveralPaths_OrdersByTotalDescending()
    {
        var breakdown = new SpanBreakdown();
        breakdown.Record("Small", 1);
        breakdown.Record("Large", 50);
        breakdown.Record("Medium", 10);

        Assert.That(breakdown.GetEntries().Select(e => e.Path), Is.EqualTo(new[] { "Large", "Medium", "Small" }));
    }

    [Test]
    public void GetUnaccountedMs_NestedPaths_SubtractsOnlyDirectChildren()
    {
        var breakdown = new SpanBreakdown();
        breakdown.Record("ProcessCso", 30);
        breakdown.Record("ProcessCso/AttemptJoin", 20);
        breakdown.Record("FlushMarks", 5);

        // A grandchild's time is already inside its parent's, so counting it again would hide real gaps.
        Assert.That(breakdown.GetUnaccountedMs(100), Is.EqualTo(65));
    }

    [Test]
    public void GetUnaccountedMs_ChildrenOverlappingOnOtherThreads_NeverNegative()
    {
        var breakdown = new SpanBreakdown();
        breakdown.Record("ExportBatch", 80);
        breakdown.Record("ExportBatch", 80);

        Assert.That(breakdown.GetUnaccountedMs(100), Is.Zero);
    }

    [Test]
    public void Describe_MoreEntriesThanTheLimit_KeepsTheLargestAndSaysHowManyWereLeftOut()
    {
        var breakdown = new SpanBreakdown();
        for (var i = 1; i <= 5; i++)
            breakdown.Record($"Step{i}", i);

        var description = breakdown.Describe(ownerDurationMs: 100, maxEntries: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(description, Does.Contain("Step5 1x 5.0ms"));
            Assert.That(description, Does.Contain("Step4 1x 4.0ms"));
            Assert.That(description, Does.Not.Contain("Step3"));
            Assert.That(description, Does.Contain("3 more"));
            Assert.That(description, Does.Contain("unaccounted 85.0ms"));
        }
    }

    [Test]
    public async Task Record_FromManyThreads_LosesNothingAsync()
    {
        var breakdown = new SpanBreakdown();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                breakdown.Record("Child", 1);
        })));

        Assert.That(breakdown.GetEntries().Single().Count, Is.EqualTo(8000));
    }
}
