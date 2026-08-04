// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// The sentence a finished export leaves on its Activity. It is the last thing an administrator
/// reads about the run, and it is read at a glance, so its counts are grouped: "10000 succeeded"
/// has to be counted digit by digit, and it sat beside a throughput figure that was already
/// grouped, so the same message formatted its numbers two different ways.
/// </summary>
[TestFixture]
public class ExportOutcomeMessageTests
{
    [Test]
    public void ForExport_LargeCounts_GroupsTheDigits()
    {
        var message = ExportOutcomeMessage.ForExport(succeeded: 10_000, failed: 1_500, deferred: 2_250, throughput: " in 7 sec (avg 1,343 obj/s)");

        Assert.That(message, Is.EqualTo("Export complete: 10,000 succeeded, 1,500 failed, 2,250 deferred in 7 sec (avg 1,343 obj/s)"));
    }

    [Test]
    public void ForExport_SmallCounts_ReadsExactlyAsBefore()
    {
        var message = ExportOutcomeMessage.ForExport(succeeded: 1, failed: 0, deferred: 0, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Export complete: 1 succeeded, 0 failed, 0 deferred"));
    }

    [Test]
    public void ForPreview_LargeCount_GroupsTheDigits()
    {
        var message = ExportOutcomeMessage.ForPreview(pendingExports: 10_000);

        Assert.That(message, Is.EqualTo("Preview complete: 10,000 export(s) would be processed"));
    }
}
