// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The per-run totals of the export's shared context gate (#2895): what each purpose waited for the gate and held it for.
/// </summary>
[TestFixture]
public class ExportContextGateTimingsTests
{
    [Test]
    public void Record_SeveralEntriesForOnePurpose_SumsWaitHeldAndCount()
    {
        var timings = new ExportContextGateTimings();

        timings.Record(ExportContextGatePurpose.ExecutionItems, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(40));
        timings.Record(ExportContextGatePurpose.ExecutionItems, TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(60));

        var entry = timings.Snapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Purpose, Is.EqualTo(ExportContextGatePurpose.ExecutionItems));
            Assert.That(entry.Count, Is.EqualTo(2));
            Assert.That(entry.TotalWait, Is.EqualTo(TimeSpan.FromMilliseconds(20)));
            Assert.That(entry.TotalHeld, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
        }
    }

    [Test]
    public void Snapshot_SeveralPurposes_KeepsThemApartAndTotalsAcrossThem()
    {
        var timings = new ExportContextGateTimings();

        timings.Record(ExportContextGatePurpose.NextBatch, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10));
        timings.Record(ExportContextGatePurpose.Progress, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3));
        timings.Record(ExportContextGatePurpose.NextBatch, TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(20));

        var byPurpose = timings.Snapshot().ToDictionary(e => e.Purpose);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(byPurpose.Keys, Is.EquivalentTo(new[] { ExportContextGatePurpose.NextBatch, ExportContextGatePurpose.Progress }),
                "only the purposes that took the gate are listed");
            Assert.That(byPurpose[ExportContextGatePurpose.NextBatch].Count, Is.EqualTo(2));
            Assert.That(byPurpose[ExportContextGatePurpose.NextBatch].TotalHeld, Is.EqualTo(TimeSpan.FromMilliseconds(30)));
            Assert.That(byPurpose[ExportContextGatePurpose.Progress].TotalWait, Is.EqualTo(TimeSpan.FromMilliseconds(2)));
            Assert.That(timings.TotalWait, Is.EqualTo(TimeSpan.FromMilliseconds(7)));
            Assert.That(timings.TotalHeld, Is.EqualTo(TimeSpan.FromMilliseconds(33)));
            Assert.That(timings.Count, Is.EqualTo(3));
        }
    }

    [Test]
    public void Snapshot_NothingRecorded_IsEmptyWithZeroTotals()
    {
        var timings = new ExportContextGateTimings();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timings.Snapshot(), Is.Empty);
            Assert.That(timings.Count, Is.Zero);
            Assert.That(timings.TotalWait, Is.EqualTo(TimeSpan.Zero));
            Assert.That(timings.TotalHeld, Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public void Snapshot_ListsPurposesWithTheMostHeldTimeFirst()
    {
        var timings = new ExportContextGateTimings();

        timings.Record(ExportContextGatePurpose.Progress, TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
        timings.Record(ExportContextGatePurpose.ExecutionItems, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        timings.Record(ExportContextGatePurpose.NextBatch, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));

        Assert.That(timings.Snapshot().Select(e => e.Purpose), Is.EqualTo(new[]
        {
            ExportContextGatePurpose.ExecutionItems, ExportContextGatePurpose.NextBatch, ExportContextGatePurpose.Progress
        }));
    }

    /// <summary>
    /// Parallel export workers record at once; nothing may be lost.
    /// </summary>
    [Test]
    public void Record_FromManyThreadsAtOnce_LosesNothing()
    {
        var timings = new ExportContextGateTimings();
        const int perThread = 1_000;
        const int threads = 8;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                timings.Record(ExportContextGatePurpose.ExecutionItems, TimeSpan.FromTicks(1), TimeSpan.FromTicks(2));
        });

        var entry = timings.Snapshot().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Count, Is.EqualTo(perThread * threads));
            Assert.That(entry.TotalWait, Is.EqualTo(TimeSpan.FromTicks(perThread * threads)));
            Assert.That(entry.TotalHeld, Is.EqualTo(TimeSpan.FromTicks(2L * perThread * threads)));
        }
    }
}
