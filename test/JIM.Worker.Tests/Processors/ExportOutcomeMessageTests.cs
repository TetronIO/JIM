// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
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
    public void ForExport_SomeWrittenInPart_SaysSoBesideSucceeded()
    {
        // #1398: an export written without its unresolved references counts as succeeded (something was
        // written) but is not finished; "succeeded" alone would read as though it were.
        var message = ExportOutcomeMessage.ForExport(succeeded: 43, failed: 0, deferred: 0, throughput: string.Empty, writtenInPart: 4);

        Assert.That(message, Is.EqualTo("Export complete: 43 succeeded (4 written in part, awaiting references), 0 failed, 0 deferred"));
    }

    [Test]
    public void ForPreview_LargeCount_GroupsTheDigits()
    {
        var message = ExportOutcomeMessage.ForPreview(pendingExports: 10_000);

        Assert.That(message, Is.EqualTo("Preview complete: 10,000 export(s) would be processed"));
    }

    [Test]
    public void ForWithheld_Deletes_MatchesThePrdWording()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Delete, attempted: 100, withheld: 342);

        Assert.That(message, Is.EqualTo("Stopped processing deletes after 100, this Run Profile's limit; 342 deletes remain pending."));
    }

    [Test]
    public void ForWithheld_Creates_UsesTheCreateNoun()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Create, attempted: 5, withheld: 12);

        Assert.That(message, Is.EqualTo("Stopped processing creates after 5, this Run Profile's limit; 12 creates remain pending."));
    }

    [Test]
    public void ForWithheld_Updates_UsesTheUpdateNoun()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Update, attempted: 0, withheld: 7);

        Assert.That(message, Is.EqualTo("Stopped processing updates after 0, this Run Profile's limit; 7 updates remain pending."));
    }

    [Test]
    public void ForWithheld_ExactlyOneWithheld_UsesTheSingularNoun()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Delete, attempted: 100, withheld: 1);

        Assert.That(message, Is.EqualTo("Stopped processing deletes after 100, this Run Profile's limit; 1 delete remains pending."));
    }

    [Test]
    public void ForWithheld_LargeAttemptedCount_GroupsTheDigits()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Update, attempted: 10_000, withheld: 2_500);

        Assert.That(message, Is.EqualTo("Stopped processing updates after 10,000, this Run Profile's limit; 2,500 updates remain pending."));
    }
}
