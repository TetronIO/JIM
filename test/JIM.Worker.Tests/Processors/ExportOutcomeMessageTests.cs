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
    public void ForWithheld_Deletes_MatchesTheSpecifiedWording()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Delete, limit: 100, pending: 342);

        Assert.That(message, Is.EqualTo(
            "Max deletes is 100, but 342 deletes were pending, so none were attempted and all 342 remain pending. " +
            "Check what staged them, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit."));
    }

    [Test]
    public void ForWithheld_Creates_UsesTheCreateNoun()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Create, limit: 5, pending: 12);

        Assert.That(message, Is.EqualTo(
            "Max creates is 5, but 12 creates were pending, so none were attempted and all 12 remain pending. " +
            "Check what staged them, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit."));
    }

    [Test]
    public void ForWithheld_Updates_UsesTheUpdateNoun()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Update, limit: 0, pending: 7);

        Assert.That(message, Is.EqualTo(
            "Max updates is 0, but 7 updates were pending, so none were attempted and all 7 remain pending. " +
            "Check what staged them, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit."));
    }

    [Test]
    public void ForWithheld_ExactlyOnePending_UsesTheSingularNounAndPronoun()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Delete, limit: 0, pending: 1);

        Assert.That(message, Is.EqualTo(
            "Max deletes is 0, but 1 delete was pending, so it was not attempted and remains pending. " +
            "Check what staged it, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit."));
    }

    [Test]
    public void ForWithheld_LargeCounts_GroupTheDigits()
    {
        var message = ExportOutcomeMessage.ForWithheld(PendingExportChangeType.Update, limit: 10_000, pending: 12_500);

        Assert.That(message, Is.EqualTo(
            "Max updates is 10,000, but 12,500 updates were pending, so none were attempted and all 12,500 remain pending. " +
            "Check what staged them, then raise or clear the limit on this Run Profile, or run an Export Run Profile without the limit."));
    }
}
