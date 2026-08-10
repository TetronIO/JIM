// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Web.Services;
using NUnit.Framework;
using System.Linq;
using System.Text.Json;

namespace JIM.Web.Api.Tests;

/// <summary>
/// What a Configuration Change Preview is allowed to state on a save confirmation (#827/#1114).
///
/// The withholding rules are the whole point of this type. A number on a confirmation dialog is read as an answer
/// whatever caveats sit beside it, so a preview that failed, has not finished counting, or was run against settings
/// the administrator has since edited must contribute nothing at all rather than something hedged.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewCountsTests
{
    [Test]
    public void ForConfirmationVerdict_WithheldWhereTheCountsAre()
    {
        // The sentence and the table are the same answer, so they withhold on exactly the same grounds; a
        // confirmation that suppressed the table but kept the sentence would be worse than either.
        var failed = CompletedPreview();
        failed.SummaryStatus = ConfigurationChangePreviewStageStatus.Failed;

        var stillCounting = CompletedPreview();
        stillCounting.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.InProgress;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationChangePreviewCounts.ForConfirmationVerdict(null, isStale: false), Is.Null);
            Assert.That(ConfigurationChangePreviewCounts.ForConfirmationVerdict(failed, isStale: false), Is.Null);
            Assert.That(ConfigurationChangePreviewCounts.ForConfirmationVerdict(stillCounting, isStale: false), Is.Null);
            Assert.That(ConfigurationChangePreviewCounts.ForConfirmationVerdict(CompletedPreview(), isStale: true), Is.Null);
        }
    }

    [Test]
    public void ForConfirmationVerdict_CompletePreviewWithCounts_LeadsWithTheWorstConsequence()
    {
        var preview = CompletedPreview(
            new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 40_000),
            new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 4));

        var verdict = ConfigurationChangePreviewCounts.ForConfirmationVerdict(preview, isStale: false);

        Assert.That(verdict, Is.Not.Null);
        Assert.That(verdict!.Lead, Is.EqualTo("4 objects would become eligible for deletion."));
    }

    [Test]
    public void ForConfirmationVerdict_CompletePreviewThatFoundNothing_SaysNothing()
    {
        // The counts state a zero here, because an absent table reads as "no preview was run". A sentence does not
        // have that problem: the dialog already lists what is changing, and "0 objects would change" beneath it is
        // a line nobody needs.
        Assert.That(ConfigurationChangePreviewCounts.ForConfirmationVerdict(CompletedPreview(), isStale: false), Is.Null);
    }

    [Test]
    public void ForConfirmation_NoPreview_StatesNothing()
    {
        Assert.That(ConfigurationChangePreviewCounts.ForConfirmation(null, isStale: false), Is.Empty);
    }

    [Test]
    public void ForConfirmation_FailedPreview_StatesNothing()
    {
        var preview = CompletedPreview();
        preview.SummaryStatus = ConfigurationChangePreviewStageStatus.Failed;

        Assert.That(ConfigurationChangePreviewCounts.ForConfirmation(preview, isStale: false), Is.Empty,
            "a failed preview evaluated an arbitrary subset of the population; its counts are not an answer");
    }

    [Test]
    public void ForConfirmation_CountsStillRunning_StatesNothing()
    {
        var preview = CompletedPreview();
        preview.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.InProgress;

        Assert.That(ConfigurationChangePreviewCounts.ForConfirmation(preview, isStale: false), Is.Empty);
    }

    [Test]
    public void ForConfirmation_StalePreview_StatesNothing()
    {
        Assert.That(ConfigurationChangePreviewCounts.ForConfirmation(CompletedPreview(), isStale: true), Is.Empty,
            "the settings have moved on, so these numbers describe a different change from the one being saved");
    }

    [Test]
    public void ForConfirmation_CompletePreviewWithCounts_StatesThemLargestFirst()
    {
        var preview = CompletedPreview(
            new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate, 12),
            new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 4_812));

        var counts = ConfigurationChangePreviewCounts.ForConfirmation(preview, isStale: false);

        Assert.That(counts, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts[0].Count, Is.EqualTo(4_812), "the largest impact is the one being consented to");
            Assert.That(counts[0].Label, Is.Not.Empty, "a transition ordinal is not something to put in front of an administrator");
            Assert.That(counts[1].Count, Is.EqualTo(12));
        }
    }

    [Test]
    public void ForConfirmation_CompletePreviewWithNoCounts_SaysSoRatherThanStayingSilent()
    {
        var counts = ConfigurationChangePreviewCounts.ForConfirmation(CompletedPreview(), isStale: false);

        Assert.That(counts, Has.Count.EqualTo(1),
            "an empty section reads as 'no preview was run', which is a different statement from 'the preview found nothing'");
        Assert.That(counts[0].Count, Is.EqualTo(0));
    }

    [Test]
    public void ForConfirmation_UnreadableCountsDocument_StatesThePreviewFoundNothingRatherThanThrowing()
    {
        var preview = CompletedPreview();
        preview.ImpactCounts = "{ not json";

        Assert.That(() => ConfigurationChangePreviewCounts.ForConfirmation(preview, isStale: false), Throws.Nothing);
    }

    private static ConfigurationChangePreview CompletedPreview(params PreviewImpactCount[] counts) => new()
    {
        ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
        ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete,
        SummaryStatus = ConfigurationChangePreviewStageStatus.Complete,
        DeltasStatus = ConfigurationChangePreviewStageStatus.Complete,
        ImpactCounts = counts.Length == 0 ? null : JsonSerializer.Serialize(counts.ToList())
    };
}
